using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Tradux.Knowledge;

/// <summary>
/// Implementação inicial do índice (ADR-0013 §8): BM25 lexical em memória,
/// persistível em JSON. Determinística e sem serviço externo; o filtro de
/// acesso (tenant/nível) é aplicado ANTES de pontuar.
/// </summary>
public sealed class Bm25KnowledgeIndex : IKnowledgeIndex
{
    private const double K1 = 1.2;
    private const double B = 0.75;

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "de", "da", "do", "das", "dos", "e", "o", "a", "os", "as", "um", "uma", "que", "em", "no", "na", "nos", "nas", "para",
        "por", "com", "se", "ao", "aos", "ou", "como", "qual", "quais", "quando", "onde", "sao", "ser", "ha", "the", "of", "to", "is", "and",
    };

    private readonly Dictionary<string, SourceEntry> _sources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, KnowledgeChunk> _chunks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, int>> _termFrequencies = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _documentFrequency = new(StringComparer.Ordinal);
    private long _totalLength;

    private sealed record SourceEntry(string Hash, string TenantScope, List<string> ChunkIds);

    public IReadOnlyCollection<string> SourceIds => _sources.Keys;

    public int ChunkCount => _chunks.Count;

    public IEnumerable<KnowledgeChunk> Chunks => _chunks.Values;

    public string? SourceHash(string sourceId) => _sources.TryGetValue(sourceId, out var entry) ? entry.Hash : null;

    public string? SourceTenantScope(string sourceId) => _sources.TryGetValue(sourceId, out var entry) ? entry.TenantScope : null;

    public (int Written, int Removed) ReplaceSource(string sourceId, string sourceHash, string tenantScope, IReadOnlyList<KnowledgeChunk> chunks)
    {
        var previous = _sources.TryGetValue(sourceId, out var entry) ? entry.ChunkIds.Select(id => _chunks[id]).ToList() : [];
        var keep = previous.Select(c => c.Metadata.ContentHash).ToHashSet(StringComparer.Ordinal);
        var incoming = chunks.Select(c => c.Metadata.ContentHash).ToHashSet(StringComparer.Ordinal);

        var removed = 0;
        foreach (var old in previous)
        {
            RemoveChunk(old.Id);
            if (!incoming.Contains(old.Metadata.ContentHash))
            {
                removed++;
            }
        }

        foreach (var chunk in chunks)
        {
            AddChunk(chunk);
        }

        _sources[sourceId] = new SourceEntry(sourceHash, tenantScope, chunks.Select(c => c.Id).ToList());
        return (chunks.Count(c => !keep.Contains(c.Metadata.ContentHash)), removed);
    }

    public int RemoveSource(string sourceId)
    {
        if (!_sources.Remove(sourceId, out var entry))
        {
            return 0;
        }

        foreach (var id in entry.ChunkIds)
        {
            RemoveChunk(id);
        }

        return entry.ChunkIds.Count;
    }

    public IReadOnlyList<SearchHit> Search(string query, AccessScope scope, int limit = 5)
    {
        var terms = Tokenize(query).Distinct().ToList();
        if (terms.Count == 0 || _chunks.Count == 0)
        {
            return [];
        }

        var averageLength = (double)_totalLength / _chunks.Count;
        var scored = new List<(KnowledgeChunk Chunk, double Score)>();
        foreach (var chunk in _chunks.Values.Where(c => scope.Allows(c.Metadata)))
        {
            var frequencies = _termFrequencies[chunk.Id];
            var length = frequencies.Values.Sum();
            double score = 0;
            foreach (var term in terms)
            {
                if (!frequencies.TryGetValue(term, out var tf))
                {
                    continue;
                }

                var df = _documentFrequency[term];
                var idf = Math.Log(1 + ((_chunks.Count - df + 0.5) / (df + 0.5)));
                score += idf * (tf * (K1 + 1)) / (tf + (K1 * (1 - B + (B * length / averageLength))));
            }

            if (score > 0)
            {
                scored.Add((chunk, score));
            }
        }

        return scored
            .OrderByDescending(s => s.Score).ThenBy(s => s.Chunk.Id, StringComparer.Ordinal)
            .Take(limit)
            .Select(s => new SearchHit(s.Chunk.Metadata.SourceId, s.Chunk.Metadata.SourceType, s.Chunk.Metadata.Version, s.Chunk.Metadata.Anchor,
                s.Chunk.Metadata.EffectiveDate, Math.Round(s.Score, 4), Snippet(s.Chunk.Text), s.Chunk.Id))
            .ToList();
    }

    // ---------------------------------------------------------------- persistência

    /// <summary>Formato do arquivo do índice; outro formato é recusado na leitura (reindexe com a versão atual).</summary>
    public const string PersistedFormat = "tradux-knowledge-index/1";

    private sealed record Persisted(string Format, List<PersistedSource> Sources, List<KnowledgeChunk> Chunks);

    private sealed record PersistedSource(string SourceId, string Hash, string TenantScope, List<string> ChunkIds);

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var persisted = new Persisted(
            PersistedFormat,
            _sources.OrderBy(s => s.Key, StringComparer.Ordinal).Select(s => new PersistedSource(s.Key, s.Value.Hash, s.Value.TenantScope, s.Value.ChunkIds)).ToList(),
            _chunks.Values.OrderBy(c => c.Id, StringComparer.Ordinal).ToList());
        // Grava num temporário e troca: leitor (ou outra instância) nunca vê arquivo pela metade.
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(persisted, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }), Encoding.UTF8);
        File.Move(temporary, path, overwrite: true);
    }

    public static Bm25KnowledgeIndex Load(string path)
    {
        var index = new Bm25KnowledgeIndex();
        if (!File.Exists(path))
        {
            return index;
        }

        var persisted = JsonSerializer.Deserialize<Persisted>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("Índice de conhecimento vazio.");
        if (persisted.Format != PersistedFormat)
        {
            throw new InvalidDataException($"Índice de conhecimento em formato '{persisted.Format}'; esperado '{PersistedFormat}'. Apague o arquivo e reindexe.");
        }

        var chunks = persisted.Chunks.ToDictionary(c => c.Id, StringComparer.Ordinal);
        foreach (var source in persisted.Sources)
        {
            index.ReplaceSource(source.SourceId, source.Hash, source.TenantScope, source.ChunkIds.Select(id => chunks[id]).ToList());
        }

        return index;
    }

    // ---------------------------------------------------------------- interno

    private void AddChunk(KnowledgeChunk chunk)
    {
        var frequencies = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var token in Tokenize(chunk.Metadata.Anchor + " " + chunk.Text))
        {
            frequencies[token] = frequencies.GetValueOrDefault(token) + 1;
        }

        _chunks[chunk.Id] = chunk;
        _termFrequencies[chunk.Id] = frequencies;
        _totalLength += frequencies.Values.Sum();
        foreach (var term in frequencies.Keys)
        {
            _documentFrequency[term] = _documentFrequency.GetValueOrDefault(term) + 1;
        }
    }

    private void RemoveChunk(string id)
    {
        if (!_termFrequencies.Remove(id, out var frequencies))
        {
            return;
        }

        _chunks.Remove(id);
        _totalLength -= frequencies.Values.Sum();
        foreach (var term in frequencies.Keys)
        {
            if (--_documentFrequency[term] == 0)
            {
                _documentFrequency.Remove(term);
            }
        }
    }

    /// <summary>Minúsculas, sem acento, só letras/dígitos, sem stopwords.</summary>
    public static IEnumerable<string> Tokenize(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormD);
        var token = new StringBuilder();
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(c))
            {
                token.Append(char.ToLowerInvariant(c));
                continue;
            }

            if (token.Length > 0)
            {
                var value = token.ToString();
                token.Clear();
                if (value.Length > 1 && !StopWords.Contains(value))
                {
                    yield return value;
                }
            }
        }

        if (token.Length > 1 && !StopWords.Contains(token.ToString()))
        {
            yield return token.ToString();
        }
    }

    private static string Snippet(string text)
    {
        var flat = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= 280 ? flat : flat[..280] + "…";
    }
}

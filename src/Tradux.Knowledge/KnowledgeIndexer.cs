using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tradux.Knowledge;

/// <summary>
/// Pipeline incremental (ADR-0013 §10): fonte com hash inalterado não é
/// reprocessada; chunk com hash inalterado é mantido; fonte do mesmo
/// escopo que saiu do corpus tem todos os chunks removidos.
/// </summary>
public static class KnowledgeIndexer
{
    /// <param name="index">Índice a atualizar.</param>
    /// <param name="sources">Corpus completo do escopo.</param>
    /// <param name="scope">Escopo gerenciado por esta chamada (<c>global</c> ou uma organização): só fontes desse escopo são removidas por ausência.</param>
    public static IndexReport Index(IKnowledgeIndex index, IEnumerable<KnowledgeSource> sources, string scope = KnowledgeSource.GlobalScope)
    {
        var added = new List<string>();
        var updated = new List<string>();
        var unchanged = new List<string>();
        var removed = new List<string>();
        int written = 0, dropped = 0;

        var incoming = sources.ToList();
        var duplicate = incoming.GroupBy(s => s.SourceId).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException($"source_id repetido no corpus: '{duplicate.Key}'.", nameof(sources));
        }

        if (incoming.FirstOrDefault(s => s.TenantScope != scope) is { } foreign)
        {
            throw new ArgumentException($"Fonte '{foreign.SourceId}' é do escopo '{foreign.TenantScope}', não '{scope}'.", nameof(sources));
        }

        foreach (var source in incoming.OrderBy(s => s.SourceId, StringComparer.Ordinal))
        {
            var hash = source.ContentHash;
            var current = index.SourceHash(source.SourceId);
            if (current == hash)
            {
                unchanged.Add(source.SourceId);
                continue;
            }

            var (w, r) = index.ReplaceSource(source.SourceId, hash, source.TenantScope, Chunker.Chunk(source));
            written += w;
            dropped += r;
            (current is null ? added : updated).Add(source.SourceId);
        }

        var present = incoming.Select(s => s.SourceId).ToHashSet(StringComparer.Ordinal);
        foreach (var obsolete in index.SourceIds.Where(id => !present.Contains(id) && index.SourceTenantScope(id) == scope).ToList())
        {
            dropped += index.RemoveSource(obsolete);
            removed.Add(obsolete);
        }

        return new IndexReport(added, updated, removed, unchanged, written, dropped);
    }
}

/// <summary>Uma linha de <c>docs/rag/sources.json</c> (glob aceito no caminho: <c>*</c> no nome do arquivo).</summary>
public sealed record SourceRegistration
{
    public required string SourceId { get; init; }
    public required string Path { get; init; }
    public required string SourceType { get; init; }
    public required string Version { get; init; }
    public AccessLevel AccessLevel { get; init; } = AccessLevel.Internal;
    public string Language { get; init; } = "pt-BR";
    public string? EffectiveDate { get; init; }
    public string? SchemaVersion { get; init; }
    public string? Provider { get; init; }
    public string? Municipality { get; init; }
    public string? Operation { get; init; }
}

/// <summary>Registro de fontes aprovadas → fontes globais (lidas do repositório).</summary>
public static class SourceRegistry
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        Converters = { new JsonStringEnumConverter() },
    };

    public static IReadOnlyList<SourceRegistration> Load(string registryPath) =>
        JsonSerializer.Deserialize<List<SourceRegistration>>(File.ReadAllText(registryPath), Options) ?? [];

    /// <summary>Lê o conteúdo das fontes; glob expande um id por arquivo (<c>{sourceId}:{nome}</c>).</summary>
    /// <exception cref="FileNotFoundException">Fonte registrada que não existe (registro desatualizado).</exception>
    public static IReadOnlyList<KnowledgeSource> Read(string repositoryRoot, IEnumerable<SourceRegistration> registrations)
    {
        var sources = new List<KnowledgeSource>();
        foreach (var registration in registrations)
        {
            var relative = registration.Path.Replace('\\', '/');
            var files = relative.Contains('*')
                ? Directory.GetFiles(System.IO.Path.Combine(repositoryRoot, System.IO.Path.GetDirectoryName(relative)!), System.IO.Path.GetFileName(relative)).Order(StringComparer.Ordinal).ToArray()
                : [System.IO.Path.Combine(repositoryRoot, relative)];
            if (files.Length == 0 || files.Any(f => !File.Exists(f)))
            {
                throw new FileNotFoundException($"Fonte registrada '{registration.SourceId}' não encontrada: {registration.Path}");
            }

            foreach (var file in files)
            {
                var id = relative.Contains('*') ? $"{registration.SourceId}:{System.IO.Path.GetFileNameWithoutExtension(file)}" : registration.SourceId;
                sources.Add(new KnowledgeSource
                {
                    SourceId = id,
                    SourceType = registration.SourceType,
                    Version = registration.Version,
                    Content = File.ReadAllText(file),
                    AccessLevel = registration.AccessLevel,
                    Language = registration.Language,
                    EffectiveDate = registration.EffectiveDate,
                    SchemaVersion = registration.SchemaVersion,
                    Provider = registration.Provider,
                    Municipality = registration.Municipality,
                    Operation = registration.Operation,
                    Path = System.IO.Path.GetRelativePath(repositoryRoot, file).Replace('\\', '/'),
                });
            }
        }

        return sources;
    }
}

/// <summary>Pergunta de avaliação: fontes (ou prefixos de source_id) que precisam aparecer.</summary>
public sealed record EvalQuestion(string Question, IReadOnlyList<string> ExpectedSources);

/// <summary>Resultado da avaliação (seção 24: "métricas de precisão de recuperação e cobertura").</summary>
public sealed record EvalResult(double PrecisionAtK, double Coverage, int K, IReadOnlyList<EvalDetail> Details);

public sealed record EvalDetail(string Question, IReadOnlyList<string> Retrieved, bool Covered, double Precision);

public static class KnowledgeEvaluator
{
    public static IReadOnlyList<EvalQuestion> Load(string path) =>
        JsonSerializer.Deserialize<List<EvalQuestion>>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];

    /// <summary>
    /// Precisão@k = fração dos k resultados cuja fonte está entre as esperadas;
    /// cobertura = fração de perguntas com ao menos uma fonte esperada nos k.
    /// </summary>
    public static EvalResult Evaluate(IKnowledgeIndex index, IReadOnlyList<EvalQuestion> questions, AccessScope scope, int k = 5)
    {
        var details = new List<EvalDetail>();
        foreach (var question in questions)
        {
            var hits = index.Search(question.Question, scope, k);
            bool Matches(string sourceId) => question.ExpectedSources.Any(e => sourceId == e || sourceId.StartsWith(e + ":", StringComparison.Ordinal));
            var relevant = hits.Count(h => Matches(h.SourceId));
            details.Add(new EvalDetail(question.Question, hits.Select(h => h.SourceId).ToList(), relevant > 0, hits.Count == 0 ? 0 : (double)relevant / hits.Count));
        }

        return new EvalResult(
            details.Count == 0 ? 0 : details.Average(d => d.Precision),
            details.Count == 0 ? 0 : (double)details.Count(d => d.Covered) / details.Count,
            k,
            details);
    }
}

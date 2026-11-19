using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Tradux.Application.Snapshots;

namespace Tradux.Application.Packaging;

/// <summary>Uma entrada do pacote: caminho, SHA-256 e tamanho (ADR-0013 §1).</summary>
public sealed record PackageEntry(string Path, string Sha256, long Size);

/// <summary>Manifesto do pacote <c>.traduxpkg</c> (<c>manifest.json</c>).</summary>
public sealed record PackageManifest
{
    public const string CurrentFormat = "tradux-package/1";

    public required string Format { get; init; }
    public required string OrganizationId { get; init; }
    public required string TemplateId { get; init; }
    public required string Operation { get; init; }
    public required int SnapshotVersion { get; init; }

    /// <summary>Hash do snapshot na origem — preservado no destino (gate da Fase 10).</summary>
    public required string SnapshotContentHash { get; init; }

    public required DateTimeOffset ExportedAtUtc { get; init; }
    public required IReadOnlyList<PackageEntry> Entries { get; init; }
}

/// <summary>Pacote recusado: lista de problemas (integridade, formato, limites, caminhos, segredos).</summary>
public sealed class PackageValidationException(IReadOnlyList<string> problems)
    : Exception("Pacote inválido: " + string.Join(" | ", problems))
{
    public IReadOnlyList<string> Problems { get; } = problems;
}

/// <summary>Limites de leitura de pacote (mesma ordem de grandeza da importação de XSD).</summary>
public sealed record PackageLimits(int MaxEntries = 500, long MaxEntryBytes = 10 * 1024 * 1024, long MaxTotalBytes = 40 * 1024 * 1024)
{
    public static PackageLimits Default { get; } = new();
}

/// <summary>Caminhos fixos das entradas.</summary>
public static class PackagePaths
{
    public const string Manifest = "manifest.json";
    public const string SnapshotContent = "snapshot/content.json";
    public const string SnapshotManifest = "snapshot/manifest.json";
    public const string Scenarios = "snapshot/scenarios.json";
    public const string Binding = "binding.json";

    public static string Layer(Domain.Layers.LayerRef reference) => $"layers/{reference.OrganizationId}/{reference.LayerId}/{reference.Version}.json";
}

/// <summary>
/// JSON canônico: mesma entrada → mesmos bytes em qualquer ambiente
/// (propriedades de objeto em ordem ordinal, sem indentação). É o que
/// garante hash por entrada preservado entre exportações (ADR-0013 §2).
/// </summary>
public static class CanonicalJson
{
    public static byte[] Serialize<T>(T value)
    {
        var node = JsonSerializer.SerializeToNode(value, SnapshotJson.Options);
        return Encoding.UTF8.GetBytes(Canonicalize(node)?.ToJsonString() ?? "null");
    }

    public static T Deserialize<T>(byte[] bytes) =>
        JsonSerializer.Deserialize<T>(bytes, SnapshotJson.Options) ?? throw new JsonException("Entrada JSON vazia.");

    private static JsonNode? Canonicalize(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => KeyValuePair.Create(p.Key, Canonicalize(p.Value?.DeepClone())))),
        JsonArray array => new JsonArray(array.Select(item => Canonicalize(item?.DeepClone())).ToArray()),
        _ => node?.DeepClone(),
    };
}

/// <summary>
/// Recusa segredo no pacote (seção 22: "nunca inclua chaves, certificados
/// ou segredos") — antes de exportar e ao importar.
/// </summary>
public static partial class PackageSecretScanner
{
    private static readonly string[] ForbiddenExtensions = [".pem", ".key", ".pfx", ".p12", ".cer", ".crt", ".jks", ".der"];

    /// <summary>Motivo da recusa, ou nulo.</summary>
    public static string? Find(string path, byte[] content)
    {
        if (ForbiddenExtensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
        {
            return $"'{path}': extensão de chave/certificado não é permitida em pacote.";
        }

        var text = Encoding.UTF8.GetString(content);
        if (text.Contains("-----BEGIN", StringComparison.Ordinal))
        {
            return $"'{path}': contém bloco PEM (chave ou certificado).";
        }

        if (CredentialSecret().IsMatch(text))
        {
            return $"'{path}': contém o que parece ser uma credencial da API.";
        }

        if (SecretField().IsMatch(text))
        {
            return $"'{path}': contém campo de segredo (senha/password/secret/token/chave privada) com valor.";
        }

        return null;
    }

    [GeneratedRegex(@"tradux_[A-Za-z0-9_\-]{20,}")]
    private static partial Regex CredentialSecret();

    [GeneratedRegex("\"(senha|password|passwd|secret|clientSecret|privateKey|apiKey|token|accessToken)\"\\s*:\\s*\"[^\"]+\"", RegexOptions.IgnoreCase)]
    private static partial Regex SecretField();
}

/// <summary>Monta e lê o ZIP do pacote com validação completa (ADR-0013 §4).</summary>
public static class PackageArchive
{
    // Data fixa nas entradas: mesmo conteúdo → mesmos bytes do ZIP.
    private static readonly DateTimeOffset FixedTimestamp = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static byte[] Write(PackageManifest manifestWithoutEntries, IReadOnlyDictionary<string, byte[]> entries)
    {
        var problems = entries.Select(e => PackageSecretScanner.Find(e.Key, e.Value)).OfType<string>().ToList();
        if (problems.Count > 0)
        {
            throw new PackageValidationException(problems);
        }

        var manifest = manifestWithoutEntries with
        {
            Entries = entries.OrderBy(e => e.Key, StringComparer.Ordinal)
                .Select(e => new PackageEntry(e.Key, Sha256(e.Value), e.Value.LongLength)).ToList(),
        };

        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, bytes) in entries.OrderBy(e => e.Key, StringComparer.Ordinal).Append(KeyValuePair.Create(PackagePaths.Manifest, CanonicalJson.Serialize(manifest))))
            {
                var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
                entry.LastWriteTime = FixedTimestamp;
                using var stream = entry.Open();
                stream.Write(bytes);
            }
        }

        return buffer.ToArray();
    }

    /// <summary>Lê e valida: limites, caminhos, manifesto, hash de cada entrada, entradas não declaradas, segredos.</summary>
    /// <exception cref="PackageValidationException">Qualquer problema — nada é importado.</exception>
    public static (PackageManifest Manifest, IReadOnlyDictionary<string, byte[]> Entries) Read(byte[] package, PackageLimits? limits = null)
    {
        limits ??= PackageLimits.Default;
        var problems = new List<string>();
        var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (package.LongLength > limits.MaxTotalBytes)
        {
            throw new PackageValidationException([$"Pacote acima de {limits.MaxTotalBytes} bytes."]);
        }

        try
        {
            using var zip = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read);
            if (zip.Entries.Count > limits.MaxEntries)
            {
                throw new PackageValidationException([$"Pacote com {zip.Entries.Count} entradas (máximo {limits.MaxEntries})."]);
            }

            long total = 0;
            foreach (var entry in zip.Entries)
            {
                var name = entry.FullName;
                if (name.Length == 0 || name.EndsWith('/'))
                {
                    continue; // diretório
                }

                if (name.Contains('\\') || name.StartsWith('/') || name.Contains(':') || name.Split('/').Any(s => s is ".." or "."))
                {
                    problems.Add($"Caminho de entrada perigoso: '{name}'.");
                    continue;
                }

                if (entry.Length > limits.MaxEntryBytes || (total += entry.Length) > limits.MaxTotalBytes)
                {
                    problems.Add($"Entrada '{name}' excede os limites de tamanho.");
                    continue;
                }

                using var stream = entry.Open();
                using var copy = new MemoryStream();
                // Leitura limitada: o tamanho declarado no ZIP pode mentir (zip bomb).
                var chunk = new byte[81920];
                int read;
                while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
                {
                    if (copy.Length + read > limits.MaxEntryBytes)
                    {
                        throw new PackageValidationException([$"Entrada '{name}' descomprimida acima do limite."]);
                    }

                    copy.Write(chunk, 0, read);
                }

                entries[name] = copy.ToArray();
            }
        }
        catch (InvalidDataException ex)
        {
            throw new PackageValidationException([$"Arquivo não é um ZIP válido: {ex.Message}"]);
        }

        if (!entries.TryGetValue(PackagePaths.Manifest, out var manifestBytes))
        {
            problems.Add("Pacote sem manifest.json.");
            throw new PackageValidationException(problems);
        }

        PackageManifest manifest;
        try
        {
            manifest = CanonicalJson.Deserialize<PackageManifest>(manifestBytes);
        }
        catch (JsonException ex)
        {
            throw new PackageValidationException([.. problems, $"manifest.json inválido: {ex.Message}"]);
        }

        if (manifest.Format != PackageManifest.CurrentFormat)
        {
            problems.Add($"Formato '{manifest.Format}' não suportado (esperado '{PackageManifest.CurrentFormat}').");
        }

        var declared = manifest.Entries.ToDictionary(e => e.Path, StringComparer.Ordinal);
        foreach (var (path, bytes) in entries.Where(e => e.Key != PackagePaths.Manifest))
        {
            if (!declared.TryGetValue(path, out var expected))
            {
                problems.Add($"Entrada '{path}' não está no manifesto.");
            }
            else if (expected.Sha256 != Sha256(bytes) || expected.Size != bytes.LongLength)
            {
                problems.Add($"Entrada '{path}' não confere com o hash do manifesto (pacote adulterado ou corrompido).");
            }

            if (PackageSecretScanner.Find(path, bytes) is { } secret)
            {
                problems.Add(secret);
            }
        }

        problems.AddRange(declared.Keys.Where(p => !entries.ContainsKey(p)).Select(p => $"Entrada '{p}' declarada no manifesto e ausente."));
        if (!entries.ContainsKey(PackagePaths.SnapshotContent) || !entries.ContainsKey(PackagePaths.Scenarios))
        {
            problems.Add("Pacote sem snapshot/content.json ou snapshot/scenarios.json.");
        }

        if (problems.Count > 0)
        {
            throw new PackageValidationException(problems);
        }

        return (manifest, entries);
    }

    public static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}

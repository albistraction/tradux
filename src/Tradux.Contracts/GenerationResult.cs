using System.Text.Json.Serialization;

namespace Tradux.Contracts;

/// <summary>
/// Resultado da geração de XML (seção 26 do requisito de origem,
/// docs/api/README.md §3–4), igual em <c>POST …/xml</c>, <c>…/transform</c> e
/// <c>…/convert?format=json</c>.
/// </summary>
public sealed record GenerationResult(
    bool Success,
    string? Xml,
    IReadOnlyList<Diagnostic> Diagnostics,
    [property: JsonPropertyName("durationMs"), JsonConverter(typeof(MillisecondsTimeSpanJsonConverter))]
    TimeSpan Duration)
{
    /// <summary>Versão de snapshot que produziu o resultado; <c>null</c> quando nenhuma versão gerou XML válido.</summary>
    public string? VersionUsed { get; init; }

    /// <summary>Versões tentadas, em ordem (seção 17). Vazio quando o template não tem versão ativa.</summary>
    public IReadOnlyList<VersionAttempt> VersionsAttempted { get; init; } = [];

    /// <summary>Ecoa o identificador de correlação da entrada, ou um gerado pelo motor quando ausente.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>Dados não normativos (ex.: <c>snapshotContentHash</c>, identificadores ecoados).</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}

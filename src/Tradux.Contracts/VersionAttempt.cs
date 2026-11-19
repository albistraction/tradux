using System.Text.Json.Serialization;

namespace Tradux.Contracts;

/// <summary>
/// Uma versão de snapshot tentada durante a geração (docs/api/README.md §4,
/// <c>TentativaVersao</c>): uma só (a ativa ou a fixada) ou, no fallback
/// (seção 17), uma por versão candidata tentada.
/// </summary>
public sealed record VersionAttempt(
    string Version,
    bool Success,
    IReadOnlyList<Diagnostic> Diagnostics,
    [property: JsonPropertyName("durationMs"), JsonConverter(typeof(MillisecondsTimeSpanJsonConverter))]
    TimeSpan Duration);

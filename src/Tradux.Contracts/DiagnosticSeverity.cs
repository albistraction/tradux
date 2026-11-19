using System.Text.Json.Serialization;

namespace Tradux.Contracts;

/// <summary>
/// Espelha Tradux.Engine.Diagnostics.EngineDiagnosticSeverity no
/// contrato público estável. Serializado como texto (<c>"Critical"</c>), não
/// como número — o valor numérico não é contrato.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<DiagnosticSeverity>))]
public enum DiagnosticSeverity
{
    Info,
    Warning,
    Critical,
}

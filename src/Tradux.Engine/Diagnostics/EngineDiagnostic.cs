namespace Tradux.Engine.Diagnostics;

/// <summary>
/// Um item do relatório estruturado (seção 18 do requisito de origem):
/// severidade, código estável, mensagem, origem/destino e sugestão.
/// <c>Application</c> traduz isto para o contrato público
/// (<c>Tradux.Contracts.Diagnostic</c>).
/// </summary>
public sealed record EngineDiagnostic(
    EngineDiagnosticSeverity Severity,
    string Code,
    string Message,
    string? DestinationXPath = null,
    string? SourcePath = null,
    string? Suggestion = null)
{
    public static EngineDiagnostic Info(
        string code, string message, string? destinationXPath = null, string? sourcePath = null, string? suggestion = null) =>
        new(EngineDiagnosticSeverity.Info, code, message, destinationXPath, sourcePath, suggestion);

    public static EngineDiagnostic Warning(
        string code, string message, string? destinationXPath = null, string? sourcePath = null, string? suggestion = null) =>
        new(EngineDiagnosticSeverity.Warning, code, message, destinationXPath, sourcePath, suggestion);

    public static EngineDiagnostic Critical(
        string code, string message, string? destinationXPath = null, string? sourcePath = null, string? suggestion = null) =>
        new(EngineDiagnosticSeverity.Critical, code, message, destinationXPath, sourcePath, suggestion);
}

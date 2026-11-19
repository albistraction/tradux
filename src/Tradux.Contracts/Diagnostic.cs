namespace Tradux.Contracts;

/// <summary>
/// Item do relatório estruturado (seção 18 do requisito de origem,
/// docs/api/README.md §4): a única forma de relatório da API.
/// </summary>
public sealed record Diagnostic(
    DiagnosticSeverity Severity,
    string Code,
    string Message,
    string? DestinationXPath,
    string? SourcePath,
    string? Suggestion)
{
    /// <summary>Diagnóstico crítico sem caminho (falha do pedido ou do conteúdo como um todo).</summary>
    public static Diagnostic Critical(string code, string message, string? suggestion = null) =>
        new(DiagnosticSeverity.Critical, code, message, null, null, suggestion);
}

using Tradux.Contracts;
using Tradux.Engine;
using EngineDiagnostic = Tradux.Engine.Diagnostics.EngineDiagnostic;
using EngineDiagnosticSeverity = Tradux.Engine.Diagnostics.EngineDiagnosticSeverity;

namespace Tradux.Application.Generation;

/// <summary>
/// Única conversão do resultado do motor para o contrato público
/// (docs/api/README.md §0: nenhuma adaptação duplicada entre rotas).
/// </summary>
public static class GenerationResultMapper
{
    public static GenerationResult ToContract(GenerationOutcome outcome) => new(
        Success: outcome.Success,
        Xml: outcome.Xml,
        Diagnostics: ToContract(outcome.Diagnostics),
        Duration: outcome.Duration);

    public static IReadOnlyList<Diagnostic> ToContract(IEnumerable<EngineDiagnostic> diagnostics) =>
        diagnostics.Select(ToContract).ToList();

    public static Diagnostic ToContract(EngineDiagnostic diagnostic) => new(
        Severity: diagnostic.Severity switch
        {
            EngineDiagnosticSeverity.Info => DiagnosticSeverity.Info,
            EngineDiagnosticSeverity.Warning => DiagnosticSeverity.Warning,
            EngineDiagnosticSeverity.Critical => DiagnosticSeverity.Critical,
            _ => throw new ArgumentOutOfRangeException(nameof(diagnostic)),
        },
        Code: diagnostic.Code,
        Message: diagnostic.Message,
        DestinationXPath: diagnostic.DestinationXPath,
        SourcePath: diagnostic.SourcePath,
        Suggestion: diagnostic.Suggestion);
}

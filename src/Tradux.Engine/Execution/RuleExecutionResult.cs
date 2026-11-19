using Tradux.Engine.Diagnostics;

namespace Tradux.Engine.Execution;

public sealed class RuleExecutionResult
{
    public required IReadOnlyList<ResolvedItem> Items { get; init; }
    public required IReadOnlyList<EngineDiagnostic> Diagnostics { get; init; }

    public bool HasCriticalDiagnostics =>
        Diagnostics.Any(d => d.Severity == EngineDiagnosticSeverity.Critical);
}

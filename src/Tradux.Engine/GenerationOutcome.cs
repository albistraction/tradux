using Tradux.Engine.Diagnostics;

namespace Tradux.Engine;

/// <summary>
/// Resultado de uma geração pelo motor (seção 18 do requisito de origem), sem
/// versão usada/versões tentadas: isso é de <c>Application</c>, que traduz para
/// o contrato público (<c>Tradux.Contracts.GenerationResult</c>).
/// </summary>
public sealed class GenerationOutcome
{
    public required bool Success { get; init; }
    public string? Xml { get; init; }
    public required IReadOnlyList<EngineDiagnostic> Diagnostics { get; init; }
    public required TimeSpan Duration { get; init; }
}

using Tradux.Engine.Diagnostics;
using Tradux.Xsd;

namespace Tradux.Engine.Compilation;

/// <summary>
/// Snapshot de template já compilado (representação executável, ADR-0001) —
/// pronto para ser reutilizado em toda geração seguinte sem recompilar.
/// <see cref="CompileTimeDiagnostics"/> traz achados que só dependem do
/// schema + mapeamentos, não do dado de entrada (ex.: obrigatório sem
/// mapeamento — seção 12/28 do requisito de origem).
/// </summary>
public sealed class CompiledTemplate
{
    public required ImportedXsd Xsd { get; init; }
    public required CompiledParticle RootContent { get; init; }
    public required IReadOnlyList<EngineDiagnostic> CompileTimeDiagnostics { get; init; }

    public bool HasCriticalCompileTimeDiagnostics =>
        CompileTimeDiagnostics.Any(d => d.Severity == EngineDiagnosticSeverity.Critical);
}

namespace Tradux.Engine.Diagnostics;

/// <summary>
/// Níveis de severidade de diagnóstico (seção 18 do requisito de origem).
/// Falhas de cardinalidade/nulabilidade/validação de schema são sempre
/// <see cref="Critical"/> — nunca rebaixadas silenciosamente.
/// </summary>
public enum EngineDiagnosticSeverity
{
    Info,
    Warning,
    Critical,
}

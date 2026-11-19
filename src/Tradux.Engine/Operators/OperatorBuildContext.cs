namespace Tradux.Engine.Operators;

/// <summary>
/// Contexto disponível para um operador durante a compilação (não durante a
/// execução — isto roda uma única vez, quando <c>TemplateCompiler</c>
/// constrói a expressão). Hoje carrega só as tabelas DE-PARA (seção 13,
/// categoria "Tabelas DE-PARA") usadas pelo operador <c>lookup</c>.
/// </summary>
public sealed class OperatorBuildContext
{
    public OperatorBuildContext(IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> lookupTables)
    {
        LookupTables = lookupTables;
    }

    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> LookupTables { get; }
}

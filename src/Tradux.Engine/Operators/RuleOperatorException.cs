namespace Tradux.Engine.Operators;

/// <summary>
/// Erro de avaliação de um operador (argumento de tipo/formato inválido,
/// fora de faixa, etc. — seção 13: "cada operador declara... validações [e]
/// códigos de erro"). Lançada dentro do delegate compilado; o
/// <c>RuleExecutor</c> a converte em um <c>EngineDiagnostic</c> crítico com
/// o caminho de destino/origem, em vez de deixar a geração inteira
/// quebrar com uma exceção não tratada.
/// </summary>
public sealed class RuleOperatorException : Exception
{
    public RuleOperatorException(string code, string message) : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

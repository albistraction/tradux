namespace Tradux.Engine.Operators;

/// <summary>Tipo esperado de um argumento — orienta o editor visual (qual controle mostrar), não o motor.</summary>
public enum RuleArgumentKind
{
    Any,
    Text,
    Number,
    Integer,
    Date,
    Boolean,
    Character,

    /// <summary>Nome de uma tabela de lookup — sempre literal (ver <c>lookup</c>).</summary>
    LookupTable,
}

/// <summary>O que um operador produz: um valor, um booleano (usado em condições/filtros) ou um marcador de omissão/nulo.</summary>
public enum RuleOperatorResultKind
{
    Value,
    Boolean,
    Omission,
}

public sealed record RuleOperatorParameter(string Name, string Description, RuleArgumentKind Kind);

/// <summary>
/// Descrição de um operador para quem monta regras (editor visual, seção 12
/// do requisito de origem): categoria da seção 13, texto legível e
/// parâmetros. Puramente informativo — a compilação continua usando só
/// <see cref="RuleOperatorDefinition"/>. Quando <see cref="RepeatsLastParameter"/>
/// é verdadeiro, o último parâmetro pode se repetir até
/// <see cref="RuleOperatorDefinition.MaxArguments"/>.
/// </summary>
public sealed record RuleOperatorMetadata(
    string Category,
    string Description,
    IReadOnlyList<RuleOperatorParameter> Parameters,
    RuleOperatorResultKind Result,
    bool RepeatsLastParameter = false)
{
    /// <summary>Metadados dos operadores iniciais (seção 13, mais ifElse e truncate da Fase 21).</summary>
    public static IReadOnlyDictionary<string, RuleOperatorMetadata> BuiltIn { get; } = BuildBuiltIn();

    /// <summary>Descrição genérica para operador customizado registrado sem metadados.</summary>
    public static RuleOperatorMetadata Generic(RuleOperatorDefinition definition) => new(
        "Customizado",
        $"Operador customizado '{definition.Name}'.",
        Enumerable.Range(1, Math.Max(definition.MinArguments, Math.Min(definition.MaxArguments, 1)))
            .Select(i => new RuleOperatorParameter($"arg{i}", $"Argumento {i}", RuleArgumentKind.Any))
            .ToList(),
        RuleOperatorResultKind.Value,
        RepeatsLastParameter: definition.MaxArguments > definition.MinArguments && definition.MaxArguments == int.MaxValue);

    private static Dictionary<string, RuleOperatorMetadata> BuildBuiltIn()
    {
        static RuleOperatorParameter P(string name, string description, RuleArgumentKind kind = RuleArgumentKind.Any) =>
            new(name, description, kind);

        var value = P("valor", "Valor a avaliar");
        var left = P("esquerda", "Primeiro valor");
        var right = P("direita", "Segundo valor");
        var text = P("texto", "Texto de entrada", RuleArgumentKind.Text);

        return new Dictionary<string, RuleOperatorMetadata>(StringComparer.Ordinal)
        {
            ["equals"] = new("Comparação", "Igual a (datas e números comparados pelo valor).", [left, right], RuleOperatorResultKind.Boolean),
            ["notEquals"] = new("Comparação", "Diferente de.", [left, right], RuleOperatorResultKind.Boolean),
            ["greaterThan"] = new("Comparação", "Maior que.", [left, right], RuleOperatorResultKind.Boolean),
            ["lessThan"] = new("Comparação", "Menor que.", [left, right], RuleOperatorResultKind.Boolean),

            ["isEmpty"] = new("Existência e nulidade", "Está vazio (nulo ou texto vazio).", [value], RuleOperatorResultKind.Boolean),
            ["notEmpty"] = new("Existência e nulidade", "Está preenchido.", [value], RuleOperatorResultKind.Boolean),

            ["and"] = new("Lógica booleana", "Todas as condições (E).", [P("condição", "Condição", RuleArgumentKind.Boolean)], RuleOperatorResultKind.Boolean, RepeatsLastParameter: true),
            ["or"] = new("Lógica booleana", "Qualquer condição (OU).", [P("condição", "Condição", RuleArgumentKind.Boolean)], RuleOperatorResultKind.Boolean, RepeatsLastParameter: true),
            ["not"] = new("Lógica booleana", "Negação (NÃO).", [P("condição", "Condição", RuleArgumentKind.Boolean)], RuleOperatorResultKind.Boolean),

            ["ifElse"] = new("Lógica booleana", "Se a condição for verdadeira, o 1º valor; senão, o 2º (só o escolhido é calculado).", [P("condição", "Condição", RuleArgumentKind.Boolean), P("então", "Valor se verdadeira"), P("senão", "Valor se falsa")], RuleOperatorResultKind.Value),

            ["copy"] = new("Constantes e defaults", "Copia o valor sem alteração.", [value], RuleOperatorResultKind.Value),
            ["constant"] = new("Constantes e defaults", "Valor fixo.", [P("valor", "Valor fixo")], RuleOperatorResultKind.Value),
            ["coalesce"] = new("Constantes e defaults", "Primeiro valor não nulo.", [value], RuleOperatorResultKind.Value, RepeatsLastParameter: true),
            ["setDefault"] = new("Constantes e defaults", "Usa o padrão quando o valor é nulo.", [value, P("padrão", "Valor padrão")], RuleOperatorResultKind.Value),

            ["concat"] = new("Texto", "Junta os valores como texto.", [P("parte", "Parte do texto")], RuleOperatorResultKind.Value, RepeatsLastParameter: true),
            ["substring"] = new("Texto", "Trecho do texto.", [text, P("início", "Posição inicial (0 = primeiro caractere)", RuleArgumentKind.Integer), P("tamanho", "Quantidade de caracteres", RuleArgumentKind.Integer)], RuleOperatorResultKind.Value),
            ["replace"] = new("Texto", "Substitui um trecho por outro.", [text, P("procurar", "Texto a procurar", RuleArgumentKind.Text), P("substituir", "Texto novo", RuleArgumentKind.Text)], RuleOperatorResultKind.Value),
            ["trim"] = new("Texto", "Remove espaços nas pontas.", [text], RuleOperatorResultKind.Value),
            ["padLeft"] = new("Texto", "Completa à esquerda até o tamanho.", [text, P("tamanho", "Tamanho final", RuleArgumentKind.Integer), P("caractere", "Caractere de preenchimento (padrão: espaço)", RuleArgumentKind.Character)], RuleOperatorResultKind.Value),
            ["truncate"] = new("Texto", "Limita o texto a um tamanho máximo (corta o excesso).", [text, P("tamanho", "Tamanho máximo", RuleArgumentKind.Integer)], RuleOperatorResultKind.Value),
            ["padRight"] = new("Texto", "Completa à direita até o tamanho.", [text, P("tamanho", "Tamanho final", RuleArgumentKind.Integer), P("caractere", "Caractere de preenchimento (padrão: espaço)", RuleArgumentKind.Character)], RuleOperatorResultKind.Value),

            ["formatDate"] = new("Formatação", "Formata uma data (ex.: dd/MM/yyyy).", [P("data", "Data", RuleArgumentKind.Date), P("formato", "Formato .NET, ex.: dd/MM/yyyy", RuleArgumentKind.Text)], RuleOperatorResultKind.Value),
            ["formatNumber"] = new("Formatação", "Formata um número (ex.: 0.00).", [P("número", "Número", RuleArgumentKind.Number), P("formato", "Formato .NET, ex.: 0.00", RuleArgumentKind.Text)], RuleOperatorResultKind.Value),

            ["lookup"] = new("Tabelas DE-PARA", "Traduz o valor por uma tabela DE-PARA (com padrão opcional quando a chave não está na tabela).", [P("chave", "Valor a traduzir"), P("tabela", "Nome da tabela", RuleArgumentKind.LookupTable), P("padrão", "Valor quando a chave é nula ou não está na tabela (opcional; sem ele, é erro)")], RuleOperatorResultKind.Value),

            ["omit"] = new("Omissão de nós", "Não emite o elemento.", [], RuleOperatorResultKind.Omission),
            ["setNull"] = new("Omissão de nós", "Valor nulo (xsi:nil quando o elemento permite).", [], RuleOperatorResultKind.Omission),
        };
    }
}

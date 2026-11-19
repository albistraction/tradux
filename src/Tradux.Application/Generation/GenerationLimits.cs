using System.Globalization;
using System.Text.Json;
using Tradux.Domain.SourceData;
using Tradux.Xsd;

namespace Tradux.Application.Generation;

/// <summary>
/// Limites de entrada (seção 25, ADR-0014 §3), os mesmos na geração, no
/// teste do editor e na leitura de XML de origem (<see cref="SourceInputs"/>).
/// Seção <c>Tradux:Generation:Limits</c>.
/// </summary>
public sealed class GenerationLimits
{
    public const string SectionName = "Tradux:Generation:Limits";

    public const string DepthLimit = "depth";
    public const string NodesLimit = "nodes";
    public const string TextLimit = "text";
    public const string SizeLimit = "size";

    public static GenerationLimits Default { get; } = new();

    /// <summary>Profundidade máxima da árvore de origem (a raiz conta como 1).</summary>
    public int MaxDepth { get; set; } = 32;

    /// <summary>Nós máximos: objetos, itens de coleção e valores.</summary>
    public int MaxNodes { get; set; } = 10_000;

    /// <summary>Tamanho máximo, em caracteres, de cada valor de texto.</summary>
    public int MaxTextLength { get; set; } = 10_000;

    /// <summary>Tamanho máximo, em caracteres, de um XML de origem (ADR-0015 §4).</summary>
    public int MaxSourceXmlCharacters { get; set; } = 1_048_576;

    /// <summary>Os mesmos limites aplicados durante a leitura do XML de origem.</summary>
    public SourceXmlLimits SourceXml => new(MaxSourceXmlCharacters, MaxDepth);

    /// <exception cref="GenerationInputLimitException">A entrada passa de um dos limites.</exception>
    public void Enforce(SourceDataTree data)
    {
        var shape = data.Measure(MaxNodes);
        if (shape.BudgetExceeded)
        {
            throw new GenerationInputLimitException(NodesLimit, $"Dados de origem com mais de {Format(MaxNodes)} nós (objetos, itens e valores).");
        }

        if (shape.Depth > MaxDepth)
        {
            throw new GenerationInputLimitException(DepthLimit, $"Dados de origem com profundidade {Format(shape.Depth)}; o máximo é {Format(MaxDepth)}.");
        }

        if (shape.LongestText > MaxTextLength)
        {
            // Nunca ecoa o valor (pode ser dado fiscal) — só o tamanho.
            throw new GenerationInputLimitException(TextLimit, $"Há um texto com {Format(shape.LongestText)} caracteres; o máximo por valor é {Format(MaxTextLength)}.");
        }
    }

    /// <summary>
    /// Mesmos limites aplicados direto ao JSON recebido, ANTES de montar a
    /// árvore interna (revisão da Fase 11, aviso 1): uma entrada gigante é
    /// recusada sem alocar a árvore — o custo fica limitado ao orçamento de nós.
    /// Mesma contagem de <see cref="SourceDataTree.Measure"/> (raiz = profundidade 1;
    /// cada objeto, coleção e valor = 1 nó).
    /// </summary>
    /// <exception cref="GenerationInputLimitException">A entrada passa de um dos limites.</exception>
    public void Enforce(JsonElement data)
    {
        var nodes = 0;
        var stack = new Stack<(JsonElement Element, int Depth)>();
        stack.Push((data, 1));
        while (stack.Count > 0)
        {
            var (element, depth) = stack.Pop();
            if (++nodes > MaxNodes)
            {
                throw new GenerationInputLimitException(NodesLimit, $"Dados de origem com mais de {Format(MaxNodes)} nós (objetos, itens e valores).");
            }

            if (depth > MaxDepth)
            {
                throw new GenerationInputLimitException(DepthLimit, $"Dados de origem com profundidade acima de {Format(MaxDepth)}.");
            }

            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject())
                    {
                        stack.Push((property.Value, depth + 1));
                    }

                    break;
                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                    {
                        stack.Push((item, depth + 1));
                    }

                    break;
                case JsonValueKind.String when element.GetString()!.Length is var length && length > MaxTextLength:
                    throw new GenerationInputLimitException(TextLimit, $"Há um texto com {Format(length)} caracteres; o máximo por valor é {Format(MaxTextLength)}.");
            }
        }
    }

    /// <exception cref="ArgumentOutOfRangeException">Limite não positivo.</exception>
    public GenerationLimits Validated()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxDepth, 2, nameof(MaxDepth));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxNodes, 1, nameof(MaxNodes));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxTextLength, 1, nameof(MaxTextLength));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxSourceXmlCharacters, 1, nameof(MaxSourceXmlCharacters));
        return this;
    }

    private static string Format(int value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Pedido acima de um limite de entrada (a API responde <c>400</c>).</summary>
public sealed class GenerationInputLimitException(string limit, string message) : ArgumentException(message)
{
    /// <summary><see cref="GenerationLimits.DepthLimit"/>, <see cref="GenerationLimits.NodesLimit"/> ou <see cref="GenerationLimits.TextLimit"/>.</summary>
    public string Limit { get; } = limit;
}

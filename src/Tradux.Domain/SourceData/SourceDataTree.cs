using System.Text.Json;

namespace Tradux.Domain.SourceData;

/// <summary>
/// Árvore interna tipada de dados de origem (seção 10 do requisito de
/// origem): coleção de valores por caminho no escopo atual. Valores podem
/// ser escalares, objetos aninhados ou coleções — uma coleção é percorrida
/// pelo bloco de iteração (seção 14), que cria uma árvore por item.
///
/// A árvore só contém dicionário, lista, texto, <see cref="decimal"/>,
/// <see cref="bool"/> e nulo: é o que <see cref="FromJson"/> e o leitor do XML
/// de origem produzem. Nomes de caminho são exatos (sensíveis a maiúsculas).
/// </summary>
public sealed class SourceDataTree
{
    /// <summary>Chave do valor de um item escalar de coleção, e do texto de um elemento XML com atributos.</summary>
    public const string ValueKey = "$value";

    private readonly IReadOnlyDictionary<string, object?> _values;

    /// <param name="root">Raiz da entrada, para item de bloco "for each"; nulo = esta árvore é a raiz.</param>
    public SourceDataTree(IReadOnlyDictionary<string, object?> values, SourceDataTree? root = null)
    {
        _values = values;
        Root = root ?? this;
    }

    /// <summary>Raiz da entrada (ela mesma, na raiz): o que <c>FieldScope.Root</c> lê.</summary>
    public SourceDataTree Root { get; }

    /// <summary>
    /// Mede a forma da árvore: profundidade, nós (objetos, itens de coleção e
    /// valores) e maior texto. Para de contar ao passar de
    /// <paramref name="nodeBudget"/> nós (<see cref="SourceDataShape.BudgetExceeded"/>)
    /// — medir uma entrada enorme não custa mais que o orçamento. Valores que
    /// não são dicionário/lista/texto contam como um nó folha.
    /// </summary>
    public SourceDataShape Measure(int nodeBudget)
    {
        var nodes = 0;
        var depth = 0;
        var longest = 0;
        var stack = new Stack<(object? Value, int Depth)>();
        stack.Push((_values, 1));
        while (stack.Count > 0)
        {
            var (value, level) = stack.Pop();
            if (++nodes > nodeBudget)
            {
                return new SourceDataShape(depth, nodes, longest, BudgetExceeded: true);
            }

            depth = Math.Max(depth, level);
            switch (value)
            {
                case string text:
                    longest = Math.Max(longest, text.Length);
                    break;
                case IReadOnlyDictionary<string, object?> map:
                    foreach (var child in map.Values)
                    {
                        stack.Push((child, level + 1));
                    }

                    break;
                case System.Collections.IEnumerable items:
                    foreach (var item in items)
                    {
                        stack.Push((item, level + 1));
                    }

                    break;
            }
        }

        return new SourceDataShape(depth, nodes, longest, BudgetExceeded: false);
    }

    /// <summary>
    /// Resolve o valor bruto de um caminho de origem. Retorna
    /// <c>(false, null)</c> quando o caminho não existe na árvore — isso é
    /// distinto de um valor presente e nulo.
    /// </summary>
    /// <remarks>
    /// Caminho com ponto (<c>Tomador.Cnpj</c>) navega objetos aninhados quando
    /// não há uma chave com o nome exato. Um segmento intermediário presente e nulo
    /// torna o caminho presente e nulo (mesma regra de "nulo" do campo
    /// final); um segmento inexistente torna o caminho ausente.
    /// </remarks>
    public bool TryGetValue(string path, out object? value)
    {
        if (_values.TryGetValue(path, out value) || !path.Contains('.'))
        {
            return _values.ContainsKey(path);
        }

        var segments = path.Split('.');
        if (!_values.TryGetValue(segments[0], out var current))
        {
            value = null;
            return false;
        }

        for (var i = 1; i < segments.Length; i++)
        {
            if (current is null)
            {
                value = null;
                return true;
            }

            if (!TryGetMember(current, segments[i], out current))
            {
                value = null;
                return false;
            }
        }

        value = current;
        return true;
    }

    // Só objeto tem membros navegáveis por caminho; coleção se percorre com bloco for each.
    private static bool TryGetMember(object container, string name, out object? member)
    {
        if (container is IReadOnlyDictionary<string, object?> dictionary)
        {
            return dictionary.TryGetValue(name, out member);
        }

        member = null;
        return false;
    }

    /// <summary>
    /// Árvore de um item de coleção (bloco "for each", seção 14). Item objeto:
    /// as chaves dele. Qualquer outro item (texto, número, lista aninhada…):
    /// o próprio valor em <see cref="ValueKey"/>.
    /// </summary>
    /// <param name="extraValues">Valores sintéticos (ex.: <c>"$index"</c>), com precedência sobre chaves de mesmo nome.</param>
    public static SourceDataTree FromItem(object? item, IReadOnlyDictionary<string, object?> extraValues, SourceDataTree root)
    {
        var values = item is IReadOnlyDictionary<string, object?> map
            ? new Dictionary<string, object?>(map, StringComparer.Ordinal)
            : new Dictionary<string, object?>(StringComparer.Ordinal) { [ValueKey] = item };
        foreach (var (key, value) in extraValues)
        {
            values[key] = value;
        }

        return new SourceDataTree(values, root);
    }

    /// <summary>
    /// Constrói uma árvore a partir de uma instância JSON (seção 10). O elemento precisa ser um objeto. Normalização:
    /// objeto → dicionário, array → lista, string → <see cref="string"/>,
    /// número → <see cref="decimal"/> (nunca <see cref="double"/>, para não
    /// perder precisão em valores fiscais), booleano → <see cref="bool"/>,
    /// <c>null</c> → <c>null</c>. Datas chegam como texto ISO-8601 e são
    /// formatadas pelo gerador exatamente como recebidas.
    /// </summary>
    /// <exception cref="ArgumentException">O JSON não é um objeto, ou tem um número fora da faixa de <see cref="decimal"/>.</exception>
    public static SourceDataTree FromJson(JsonElement json)
    {
        if (json.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException(
                $"Os dados de origem precisam ser um objeto JSON, não '{json.ValueKind}'.", nameof(json));
        }

        return new SourceDataTree((IReadOnlyDictionary<string, object?>)NormalizeJson(json, "$")!);
    }

    private static object? NormalizeJson(JsonElement element, string path) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject()
            .ToDictionary(p => p.Name, p => NormalizeJson(p.Value, $"{path}.{p.Name}"), StringComparer.Ordinal)
            as IReadOnlyDictionary<string, object?>,
        JsonValueKind.Array => element.EnumerateArray()
            .Select((item, index) => NormalizeJson(item, $"{path}[{index}]"))
            .ToList(),
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetDecimal(out var number)
            ? number
            : throw new ArgumentException($"Número em '{path}' está fora da faixa suportada (decimal).", nameof(element)),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => throw new ArgumentException($"Valor JSON não suportado em '{path}': {element.ValueKind}.", nameof(element)),
    };
}

/// <summary>Forma medida de uma <see cref="SourceDataTree"/> (ver <see cref="SourceDataTree.Measure"/>).</summary>
public sealed record SourceDataShape(int Depth, int Nodes, int LongestText, bool BudgetExceeded);

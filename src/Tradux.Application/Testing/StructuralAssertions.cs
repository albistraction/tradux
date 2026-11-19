using System.Globalization;
using System.Xml.Linq;
using Tradux.Application.Layers;

namespace Tradux.Application.Testing;

/// <summary>
/// Avalia <see cref="StructuralAssertion"/> sobre o XML gerado. Caminho por
/// nomes locais (ignora prefixo/namespace, como a comparação semântica);
/// último segmento <c>@nome</c> seleciona atributo. As mensagens nunca
/// repetem o valor esperado nem o gerado (seção 20).
/// </summary>
public static class StructuralAssertions
{
    /// <summary>Nulo = asserção satisfeita; senão, o motivo (sem valores).</summary>
    public static string? Check(XDocument document, StructuralAssertion assertion)
    {
        var segments = assertion.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || document.Root is null)
        {
            return $"caminho '{assertion.Path}' inválido na asserção.";
        }

        var nodes = Select(document.Root, segments);
        return assertion.Kind switch
        {
            AssertionKind.Exists => nodes.Count > 0 ? null : $"asserção Exists: nada em '{assertion.Path}'.",
            AssertionKind.Absent => nodes.Count == 0 ? null : $"asserção Absent: '{assertion.Path}' está presente.",
            AssertionKind.Equals => nodes.Count > 0 && nodes[0] == (assertion.Expected ?? string.Empty)
                ? null
                : $"asserção Equals: '{assertion.Path}' {(nodes.Count == 0 ? "ausente" : "com valor diferente do esperado")}.",
            AssertionKind.Count => int.TryParse(assertion.Expected, NumberStyles.None, CultureInfo.InvariantCulture, out var expected)
                ? nodes.Count == expected ? null : $"asserção Count: '{assertion.Path}' tem {nodes.Count} ocorrência(s), esperado {expected}."
                : $"asserção Count em '{assertion.Path}' sem número esperado válido.",
            _ => $"tipo de asserção desconhecido em '{assertion.Path}'.",
        };
    }

    private static List<string> Select(XElement root, string[] segments)
    {
        if (root.Name.LocalName != segments[0])
        {
            return [];
        }

        IEnumerable<XElement> current = [root];
        for (var i = 1; i < segments.Length; i++)
        {
            var segment = segments[i];
            if (segment.StartsWith('@'))
            {
                return i == segments.Length - 1
                    ? current.Select(e => e.Attributes().FirstOrDefault(a => a.Name.LocalName == segment[1..])?.Value).OfType<string>().ToList()
                    : [];
            }

            current = current.SelectMany(e => e.Elements().Where(c => c.Name.LocalName == segment)).ToList();
        }

        return current.Select(e => e.Value).ToList();
    }
}

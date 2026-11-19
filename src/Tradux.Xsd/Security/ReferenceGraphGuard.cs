using System.Xml;
using System.Xml.Schema;

namespace Tradux.Xsd.Security;

/// <summary>
/// Recusa XSD cujo grafo de referências é alto demais, <b>antes</b> de compilar.
/// A compilação do .NET (e a montagem da árvore de destino) segue as
/// referências recursivamente; uma cadeia de alguns milhares de níveis estoura
/// a pilha e derruba o processo, sem 400 nem 500. Exemplos de cadeia:
/// <c>simpleType</c> restringindo o anterior, <c>union</c>/<c>list</c>,
/// extensão ou restrição de tipo complexo, <c>attributeGroup ref</c>,
/// <c>group ref</c>, <c>substitutionGroup</c> e elementos cujo tipo contém o
/// próximo.
///
/// A altura (o caminho mais longo) é calculada por busca em profundidade
/// <b>iterativa</b>, com pilha explícita. Não depende da ordem de declaração
/// nem de qual arquivo incluído declara cada parte. Arestas de volta (ciclos,
/// ex.: tipo recursivo) não somam altura; ciclos ilegais são recusados pelo
/// compilador ou pelos outros guardas. XSDs fiscais reais têm altura de
/// poucas dezenas.
/// </summary>
internal static class ReferenceGraphGuard
{
    /// <exception cref="InvalidOperationException">Altura acima de <see cref="XsdImportSecurityOptions.MaxReferenceHeight"/>.</exception>
    public static void Check(IReadOnlyList<XmlSchema> schemas, XsdImportSecurityOptions options)
    {
        var edges = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var schema in schemas)
        {
            var ns = schema.TargetNamespace ?? "";
            foreach (var item in schema.Items)
            {
                if (Key(item, ns) is { } key)
                {
                    if (!edges.TryGetValue(key, out var list))
                    {
                        edges[key] = list = [];
                    }

                    Walk(item, list, depth: 0);
                }
            }
        }

        var height = Height(edges, options.MaxReferenceHeight);
        if (height > options.MaxReferenceHeight)
        {
            throw new InvalidOperationException(
                $"O XSD tem uma cadeia de referências (herança, list/union, grupos, attributeGroup, substitutionGroup ou tipos aninhados) com mais de {options.MaxReferenceHeight} níveis. " +
                "XSDs fiscais reais têm poucas dezenas.");
        }
    }

    private static string? Key(XmlSchemaObject item, string ns) => item switch
    {
        XmlSchemaType { Name: { } name } => $"t|{ns}|{name}",
        XmlSchemaGroup { Name: { } name } => $"g|{ns}|{name}",
        XmlSchemaAttributeGroup { Name: { } name } => $"ag|{ns}|{name}",
        XmlSchemaElement { Name: { } name } => $"e|{ns}|{name}",
        XmlSchemaAttribute { Name: { } name } => $"a|{ns}|{name}",
        _ => null,
    };

    private static void Add(List<string> into, string kind, XmlQualifiedName? name)
    {
        // Tipos embutidos (xs:string…) não estão no grafo: viram folhas.
        if (name is { IsEmpty: false })
        {
            into.Add($"{kind}|{name.Namespace}|{name.Name}");
        }
    }

    // Referências feitas dentro da definição (inclusive partes anônimas). A
    // profundidade do modelo de objetos acompanha a do XML, já limitada pelo XmlDepthGuard.
    private static void Walk(XmlSchemaObject? item, List<string> refs, int depth)
    {
        if (item is null || depth > 1_000)
        {
            return;
        }

        switch (item)
        {
            case XmlSchemaElement element:
                Add(refs, "e", element.RefName);
                Add(refs, "t", element.SchemaTypeName);
                Add(refs, "e", element.SubstitutionGroup);
                Walk(element.SchemaType, refs, depth + 1);
                break;
            case XmlSchemaAttribute attribute:
                Add(refs, "a", attribute.RefName);
                Add(refs, "t", attribute.SchemaTypeName);
                Walk(attribute.SchemaType, refs, depth + 1);
                break;
            case XmlSchemaAttributeGroupRef reference:
                Add(refs, "ag", reference.RefName);
                break;
            case XmlSchemaAttributeGroup group:
                WalkAll(group.Attributes, refs, depth);
                break;
            case XmlSchemaGroupRef reference:
                Add(refs, "g", reference.RefName);
                break;
            case XmlSchemaGroup group:
                Walk(group.Particle, refs, depth + 1);
                break;
            case XmlSchemaGroupBase particle:
                WalkAll(particle.Items, refs, depth);
                break;
            case XmlSchemaComplexType complex:
                Walk(complex.ContentModel, refs, depth + 1);
                Walk(complex.Particle, refs, depth + 1);
                WalkAll(complex.Attributes, refs, depth);
                break;
            case XmlSchemaComplexContent content:
                Walk(content.Content, refs, depth + 1);
                break;
            case XmlSchemaSimpleContent content:
                Walk(content.Content, refs, depth + 1);
                break;
            case XmlSchemaComplexContentExtension extension:
                Add(refs, "t", extension.BaseTypeName);
                Walk(extension.Particle, refs, depth + 1);
                WalkAll(extension.Attributes, refs, depth);
                break;
            case XmlSchemaComplexContentRestriction restriction:
                Add(refs, "t", restriction.BaseTypeName);
                Walk(restriction.Particle, refs, depth + 1);
                WalkAll(restriction.Attributes, refs, depth);
                break;
            case XmlSchemaSimpleContentExtension extension:
                Add(refs, "t", extension.BaseTypeName);
                WalkAll(extension.Attributes, refs, depth);
                break;
            case XmlSchemaSimpleContentRestriction restriction:
                Add(refs, "t", restriction.BaseTypeName);
                Walk(restriction.BaseType, refs, depth + 1);
                WalkAll(restriction.Attributes, refs, depth);
                break;
            case XmlSchemaSimpleType simple:
                Walk(simple.Content, refs, depth + 1);
                break;
            case XmlSchemaSimpleTypeRestriction restriction:
                Add(refs, "t", restriction.BaseTypeName);
                Walk(restriction.BaseType, refs, depth + 1);
                break;
            case XmlSchemaSimpleTypeList list:
                Add(refs, "t", list.ItemTypeName);
                Walk(list.ItemType, refs, depth + 1);
                break;
            case XmlSchemaSimpleTypeUnion union:
                foreach (var member in union.MemberTypes ?? [])
                {
                    Add(refs, "t", member);
                }

                WalkAll(union.BaseTypes, refs, depth);
                break;
        }
    }

    private static void WalkAll(XmlSchemaObjectCollection items, List<string> refs, int depth)
    {
        foreach (var child in items)
        {
            Walk(child, refs, depth + 1);
        }
    }

    // Caminho mais longo, iterativo. Para assim que passa do limite (nada acima muda a decisão).
    private static int Height(Dictionary<string, List<string>> edges, int limit)
    {
        var height = new Dictionary<string, int>(StringComparer.Ordinal);
        var onStack = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<(string Node, int Next)>();
        var max = 0;

        foreach (var start in edges.Keys)
        {
            if (height.ContainsKey(start))
            {
                continue;
            }

            stack.Push((start, 0));
            onStack.Add(start);
            while (stack.Count > 0)
            {
                var (node, next) = stack.Pop();
                var children = edges.GetValueOrDefault(node);
                if (children is not null && next < children.Count)
                {
                    stack.Push((node, next + 1));
                    var child = children[next];
                    if (!height.ContainsKey(child) && !onStack.Contains(child) && edges.ContainsKey(child))
                    {
                        onStack.Add(child);
                        stack.Push((child, 0));
                        if (stack.Count > limit + 1)
                        {
                            return stack.Count; // caminho atual já passa do limite
                        }
                    }

                    continue;
                }

                // Todos os filhos resolvidos (ou em ciclo, que não soma): altura = 1 + maior filho.
                var best = 0;
                foreach (var child in children ?? [])
                {
                    best = Math.Max(best, height.GetValueOrDefault(child));
                }

                height[node] = best + 1;
                onStack.Remove(node);
                max = Math.Max(max, best + 1);
                if (max > limit)
                {
                    return max;
                }
            }
        }

        return max;
    }
}

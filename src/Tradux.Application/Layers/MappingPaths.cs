using Tradux.Contracts.Design;
using Tradux.Domain.Layers;
using Tradux.Domain.Mapping;

namespace Tradux.Application.Layers;

/// <summary>
/// Conversão entre o <see cref="MappingTemplate"/> (árvore que o motor
/// compila) e o mapa por caminho lógico usado pelas camadas (ADR-0006). Os
/// caminhos são os mesmos da árvore de destino do editor
/// (<c>/DPS/numero</c>, <c>/Pedido/itens/item</c>).
/// </summary>
public static class MappingPaths
{
    /// <summary>Achata um template em entradas por caminho (base de uma camada, diff entre resoluções).</summary>
    public static Dictionary<string, MappingEntry> Flatten(MappingTemplate template, string rootElementName)
    {
        var entries = new Dictionary<string, MappingEntry>(StringComparer.Ordinal);
        Visit(template, "/" + rootElementName, entries);
        return entries;
    }

    private static void Visit(MappingTemplate template, string prefix, Dictionary<string, MappingEntry> entries)
    {
        foreach (var field in template.Fields)
        {
            entries[$"{prefix}/{field.DestinationElementName}"] = MappingEntry.Scalar(field.Value, field.Condition);
        }

        foreach (var complex in template.Complex)
        {
            Visit(complex.Content, $"{prefix}/{complex.DestinationElementName}", entries);
        }

        foreach (var collection in template.Collections)
        {
            var path = $"{prefix}/{collection.DestinationElementName}";
            entries[path] = MappingEntry.ForEach(new CollectionEntry(collection.SourceCollectionPath, collection.Filter, collection.OrderByKey, collection.SingleObjectAsItem));
            Visit(collection.ItemContent, path, entries);
        }
    }

    /// <summary>
    /// Monta o template a partir das entradas e da árvore de destino — mesma
    /// regra do editor: complexo obrigatório sob pai ativo é sempre emitido;
    /// complexo opcional só se algo dentro dele estiver mapeado; bloco
    /// repetível só com coleção definida. Entradas cujo caminho não casa com
    /// um nó mapeável são ignoradas aqui (a resolução já as reportou como conflito).
    /// </summary>
    public static MappingTemplate Build(DestinationNode root, IReadOnlyDictionary<string, MappingEntry> entries) =>
        BuildContent(root, entries, active: true);

    private static MappingTemplate BuildContent(DestinationNode node, IReadOnlyDictionary<string, MappingEntry> entries, bool active)
    {
        var fields = new List<FieldMapping>();
        var complex = new List<ComplexMapping>();
        var collections = new List<CollectionMapping>();
        // Conteúdo xs:choice direto (ramos sem nó de grupo, ex.: Rps): cada ramo é opcional — mesma regra do editor.
        AddContent(node, entries, active && node.ContentModel != Xsd.ParticleRules.ChoiceModel, fields, complex, collections);
        return new MappingTemplate { Fields = fields, Complex = complex, Collections = collections };
    }

    // Grupos (sequence/choice/all) são transparentes: seus filhos entram no conteúdo do elemento (ADR-0018).
    private static void AddContent(
        DestinationNode node, IReadOnlyDictionary<string, MappingEntry> entries, bool active,
        List<FieldMapping> fields, List<ComplexMapping> complex, List<CollectionMapping> collections)
    {
        foreach (var child in node.Children)
        {
            if (!child.Mappable)
            {
                continue;
            }

            if (child.Kind == DestinationNodeKind.Group)
            {
                // Dentro de choice ou grupo opcional, nada é obrigatório por si.
                AddContent(child, entries, active && child.ContentModel != Xsd.ParticleRules.ChoiceModel && child.MinOccurs >= 1, fields, complex, collections);
                continue;
            }

            if (child.Kind == DestinationNodeKind.Scalar)
            {
                if (entries.TryGetValue(child.Path, out var entry) && entry.Value is not null)
                {
                    fields.Add(new FieldMapping(child.Name, entry.Value, entry.Condition));
                }
            }
            else if (child.IsRepeatable)
            {
                if (entries.TryGetValue(child.Path, out var entry) && entry.Collection is { } collection)
                {
                    collections.Add(new CollectionMapping
                    {
                        DestinationElementName = child.Name,
                        SourceCollectionPath = collection.SourceCollectionPath,
                        Filter = collection.Filter,
                        OrderByKey = collection.OrderByKey,
                        SingleObjectAsItem = collection.SingleObjectAsItem,
                        ItemContent = BuildContent(child, entries, active: true),
                    });
                }
            }
            else
            {
                var required = active && child.MinOccurs >= 1;
                var hasMapping = HasMapping(child, entries);
                if (required || hasMapping)
                {
                    complex.Add(new ComplexMapping { DestinationElementName = child.Name, Content = BuildContent(child, entries, active: true) });
                }
            }
        }
    }

    private static bool HasMapping(DestinationNode node, IReadOnlyDictionary<string, MappingEntry> entries)
    {
        if (!node.Mappable)
        {
            return false;
        }

        if (node.Kind == DestinationNodeKind.Group)
        {
            return node.Children.Any(child => HasMapping(child, entries));
        }

        if (node.Kind == DestinationNodeKind.Scalar)
        {
            return entries.TryGetValue(node.Path, out var e) && e.Value is not null;
        }

        return node.IsRepeatable
            ? entries.TryGetValue(node.Path, out var c) && c.Collection is not null
            : node.Children.Any(child => HasMapping(child, entries));
    }
}

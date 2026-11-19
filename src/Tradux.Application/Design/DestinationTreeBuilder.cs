using Tradux.Contracts.Design;
using Tradux.Domain.Schema;
using Tradux.Xsd;

namespace Tradux.Application.Design;

/// <summary>
/// Árvore de destino (caminhos lógicos, cardinalidade, mapeabilidade) a
/// partir do XSD importado — usada pelo editor (Fase 6) e pela resolução de
/// camadas (Fase 7), para que ambos concordem exatamente com o que o
/// <c>TemplateCompiler</c> aceita.
/// </summary>
public static class DestinationTreeBuilder
{
    public static DestinationTree Build(ImportedXsd xsd)
    {
        var schema = xsd.Schema;
        var rootPath = "/" + schema.RootElementName;
        var root = new DestinationNode(
            schema.RootElementName,
            rootPath,
            DestinationNodeKind.Complex,
            1,
            1,
            false,
            null,
            null,
            ParticleRules.ContentModel(schema.RootContent.Kind),
            ParticleRules.ContentMappable(true, schema.RootContent),
            DescribeParticle(schema.RootContent, rootPath, ParticleRules.ContentMappable(true, schema.RootContent)),
            ContentRepeatable: schema.RootContent.Cardinality.IsRepeatable);

        return new DestinationTree(schema.RootElementName, schema.TargetNamespace, xsd.ContentHash, root);
    }

    /// <summary>Todos os nós da árvore em pré-ordem.</summary>
    public static IEnumerable<DestinationNode> Flatten(DestinationNode node) =>
        new[] { node }.Concat(node.Children.SelectMany(Flatten));

    private static IReadOnlyList<DestinationNode> DescribeParticle(SchemaParticle particle, string parentPath, bool mappable)
    {
        var groupIndex = 0;
        return particle.Items.Select(item => DescribeItem(item, parentPath, mappable, ref groupIndex)).ToList();
    }

    private static DestinationNode DescribeItem(SchemaParticleItem item, string parentPath, bool mappable, ref int groupIndex)
    {
        switch (item)
        {
            case ScalarElementItem scalar:
            {
                var e = scalar.Element;
                return new DestinationNode(
                    e.Name, $"{parentPath}/{e.Name}", DestinationNodeKind.Scalar,
                    e.Cardinality.MinOccurs, e.Cardinality.MaxOccurs, e.Nillability == Nillability.Nillable,
                    e.ScalarType.ToString(), e.DefaultValue, null, mappable, []);
            }

            case ComplexElementItem { SignedByCaller: true } signature:
                // Assinatura: um nó só, sem a estrutura do xmldsig; quem chama o serviço assina.
                return new DestinationNode(
                    signature.Name, $"{parentPath}/{signature.Name}", DestinationNodeKind.Complex,
                    signature.Cardinality.MinOccurs, signature.Cardinality.MaxOccurs, false,
                    null, null, null, false, [], SignedByCaller: true);

            case ComplexElementItem complex:
            {
                var path = $"{parentPath}/{complex.Name}";
                var contentMappable = ParticleRules.ContentMappable(mappable, complex.Content);
                return new DestinationNode(
                    complex.Name, path, DestinationNodeKind.Complex,
                    complex.Cardinality.MinOccurs, complex.Cardinality.MaxOccurs, complex.Nillability == Nillability.Nillable,
                    null, null, ParticleRules.ContentModel(complex.Content.Kind), contentMappable,
                    DescribeParticle(complex.Content, path, contentMappable),
                    ContentRepeatable: complex.Content.Cardinality.IsRepeatable);
            }

            case GroupItem group:
            {
                // Grupo sem elemento próprio: transparente no mapeamento. O nó do grupo
                // tem caminho próprio (só para a árvore), e os filhos ficam com o caminho
                // do elemento pai, o mesmo que o motor e o XML usam (ADR-0018).
                var model = ParticleRules.ContentModel(group.Group.Kind);
                var path = $"{parentPath}/({model}#{++groupIndex})";
                var groupMappable = ParticleRules.GroupMappable(mappable, group.Group);
                return new DestinationNode(
                    $"({model})", path, DestinationNodeKind.Group,
                    group.Group.Cardinality.MinOccurs, group.Group.Cardinality.MaxOccurs, false,
                    null, null, model, groupMappable,
                    DescribeParticle(group.Group, parentPath, groupMappable));
            }

            default:
                throw new NotSupportedException($"Item de schema '{item.GetType().Name}' não suportado.");
        }
    }
}

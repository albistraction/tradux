using Tradux.Domain.SourceContracts;
using Tradux.Domain.SourceData;
using System.Xml;
using System.Xml.Schema;

namespace Tradux.Xsd;

/// <summary>
/// Um campo do contrato de origem derivado do XSD de origem (ADR-0015 §2).
/// Objeto = elemento complexo (<see cref="Children"/>); valor = elemento
/// simples, atributo (<c>@nome</c>) ou texto de conteúdo simples
/// (<c>$value</c>). <see cref="Repeated"/> vem do XSD (o próprio elemento ou
/// um grupo que o contém com <c>maxOccurs &gt; 1</c>) — nunca da contagem num
/// XML de exemplo: uma coleção com um item continua coleção.
/// </summary>
public sealed record SourceShapeNode(
    string Name,
    bool IsObject,
    SourceValueType ValueType,
    bool Repeated,
    bool Required,
    bool Nillable,
    IReadOnlyList<SourceShapeNode> Children,
    bool IsExtension = false,
    string? Namespace = null)
{
    /// <summary>Nome qualificado do elemento no XML ("" = sem namespace; nulo em atributo/texto).</summary>
    public System.Xml.Linq.XName ElementName => System.Xml.Linq.XNamespace.Get(Namespace ?? "") + Name;

    /// <summary>Prefixo dos atributos na árvore interna — não colide com nome de elemento.</summary>
    public const string AttributePrefix = "@";

    /// <summary>Texto de um elemento de conteúdo simples com atributos (mesmo nome do valor de item de lista simples).</summary>
    public const string TextField = Domain.SourceData.SourceDataTree.ValueKey;
}

/// <summary>
/// Forma do contrato de origem: o elemento raiz e seus campos. Única fonte da
/// regra "o que vira objeto, lista ou valor" — usada pelo leitor de XML de
/// origem (<see cref="SourceXmlReader"/>) e pela árvore de campos do editor,
/// para que os caminhos que o editor oferece sejam exatamente os que a
/// geração resolve.
/// </summary>
public sealed record SourceShape(string RootElementName, string? TargetNamespace, IReadOnlyList<SourceShapeNode> Fields)
{
    /// <exception cref="NotSupportedException">
    /// Elemento raiz sem tipo complexo, ou forma acima de <see cref="ExpandedTreeLimits"/>
    /// (um XSD pequeno com tipos que se desdobram não explode a memória).
    /// </exception>
    public static SourceShape From(XmlSchemaElement root)
    {
        if (root.ElementSchemaType is not XmlSchemaComplexType complexType)
        {
            throw new NotSupportedException($"O elemento raiz de origem '{root.Name}' precisa ter tipo complexo (com filhos).");
        }

        return new SourceShape(
            root.Name ?? throw new InvalidOperationException("Elemento raiz sem nome."),
            root.QualifiedName.Namespace is { Length: > 0 } ns ? ns : null,
            ComplexChildren(complexType, new BuildContext(complexType), depth: 1));
    }

    /// <summary>Estado da montagem da forma: tipos em visita (recursão), campos gastos (orçamento).</summary>
    private sealed class BuildContext(XmlSchemaComplexType root)
    {
        public HashSet<XmlSchemaComplexType> Visiting { get; } = [root];

        private int _fields;

        public void Spend()
        {
            if (++_fields > ExpandedTreeLimits.MaxNodes)
            {
                throw ExpandedTreeLimits.TooLarge("de origem");
            }
        }
    }

    private static List<SourceShapeNode> ComplexChildren(XmlSchemaComplexType type, BuildContext context, int depth)
    {
        var children = new List<SourceShapeNode>();
        foreach (var attribute in type.AttributeUses.Values.OfType<XmlSchemaAttribute>())
        {
            Merge(children, new SourceShapeNode(SourceShapeNode.AttributePrefix + attribute.QualifiedName.Name, IsObject: false,
                ValueTypeOf(attribute.AttributeSchemaType?.Datatype), Repeated: false, Required: attribute.Use == XmlSchemaUse.Required, Nillable: false, []), context);
        }

        if (type.ContentType == XmlSchemaContentType.TextOnly)
        {
            // <valor moeda="BRL">10.00</valor> → valor.$value e valor.@moeda
            Merge(children, new SourceShapeNode(SourceShapeNode.TextField, IsObject: false, ValueTypeOf(type.Datatype),
                Repeated: false, Required: true, Nillable: false, []), context);
        }

        if (type.ContentTypeParticle is XmlSchemaGroupBase group)
        {
            AddParticle(children, group, enclosingRepeated: false, enclosingOptional: false, context, depth);
        }

        return children;
    }

    // Grupos (sequence/choice/all) não têm elemento próprio: os elementos ficam no
    // mesmo objeto; se o grupo repete, cada elemento dele repete.
    private static void AddParticle(List<SourceShapeNode> into, XmlSchemaGroupBase group, bool enclosingRepeated, bool enclosingOptional, BuildContext context, int depth)
    {
        if (depth > ExpandedTreeLimits.MaxDepth)
        {
            throw ExpandedTreeLimits.TooDeep("de origem");
        }

        var repeated = enclosingRepeated || group.MaxOccurs > 1;
        var optional = enclosingOptional || group.MinOccurs == 0 || group is XmlSchemaChoice;
        foreach (var item in group.Items)
        {
            switch (item)
            {
                case XmlSchemaElement element:
                    AddElement(into, element, repeated, optional, context, depth + 1);
                    break;
                case XmlSchemaGroupBase nested:
                    AddParticle(into, nested, repeated, optional, context, depth + 1);
                    break;
                // xs:any: sem declaração — o leitor converte pela estrutura do próprio XML.
            }
        }
    }

    private static void AddElement(List<SourceShapeNode> into, XmlSchemaElement element, bool enclosingRepeated, bool enclosingOptional, BuildContext context, int depth)
    {
        if (depth > ExpandedTreeLimits.MaxDepth)
        {
            throw ExpandedTreeLimits.TooDeep("de origem");
        }

        var name = element.QualifiedName.Name;
        var repeated = enclosingRepeated || element.MaxOccurs > 1;
        var required = !enclosingOptional && element.MinOccurs >= 1;
        if (element.ElementSchemaType is XmlSchemaComplexType complex)
        {
            // Tipo recursivo: para no segundo nível (o leitor segue pela estrutura do XML).
            var children = context.Visiting.Add(complex) ? ComplexChildren(complex, context, depth + 1) : [];
            context.Visiting.Remove(complex);
            Merge(into, new SourceShapeNode(name, IsObject: true, SourceValueType.Text, repeated, required, element.IsNillable, children, Namespace: element.QualifiedName.Namespace), context);
            return;
        }

        // Namespace real (qualified, unqualified ou ref de outro namespace): revisão da Fase 13, R1.
        Merge(into, new SourceShapeNode(name, IsObject: false, ValueTypeOf(element.ElementSchemaType?.Datatype), repeated, required, element.IsNillable, [], Namespace: element.QualifiedName.Namespace), context);
    }

    // O mesmo nome duas vezes no conteúdo (ramos de choice, sequência repetida) vira um campo só.
    private static void Merge(List<SourceShapeNode> into, SourceShapeNode node, BuildContext context)
    {
        context.Spend();
        var index = into.FindIndex(n => n.Name == node.Name);
        if (index < 0)
        {
            into.Add(node);
            return;
        }

        var existing = into[index];
        into[index] = existing with
        {
            Repeated = true,
            Required = existing.Required && node.Required,
            Nillable = existing.Nillable || node.Nillable,
            Children = existing.Children.Concat(node.Children.Where(c => existing.Children.All(e => e.Name != c.Name))).ToList(),
        };
    }

    private static SourceValueType ValueTypeOf(XmlSchemaDatatype? datatype) => datatype?.TypeCode switch
    {
        XmlTypeCode.Decimal or XmlTypeCode.Integer or XmlTypeCode.NonPositiveInteger or XmlTypeCode.NegativeInteger
            or XmlTypeCode.Long or XmlTypeCode.Int or XmlTypeCode.Short or XmlTypeCode.Byte
            or XmlTypeCode.NonNegativeInteger or XmlTypeCode.UnsignedLong or XmlTypeCode.UnsignedInt
            or XmlTypeCode.UnsignedShort or XmlTypeCode.UnsignedByte or XmlTypeCode.PositiveInteger
            or XmlTypeCode.Float or XmlTypeCode.Double => SourceValueType.Number,
        XmlTypeCode.Date or XmlTypeCode.DateTime or XmlTypeCode.GYearMonth => SourceValueType.Date,
        _ => SourceValueType.Text,
    };
}

/// <summary>XSD de origem importado: conjunto compilado (para validar instâncias), hash e forma.</summary>
public sealed record SourceSchema(XmlSchemaSet SchemaSet, string ContentHash, SourceShape Shape)
{
    /// <summary>Extensões do contrato de entrada (ADR-0016); vazio = só o XSD.</summary>
    public IReadOnlyList<SourceExtension> Extensions { get; init; } = [];

    /// <summary>Mesmo XSD com as extensões aplicadas à forma.</summary>
    /// <exception cref="SourceExtensionException">Extensão inválida.</exception>
    public SourceSchema WithExtensions(IReadOnlyList<SourceExtension>? extensions) =>
        extensions is not { Count: > 0 } ? this : this with { Shape = SourceExtensionMerger.Apply(Shape, extensions), Extensions = extensions };
}

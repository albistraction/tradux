using System.Xml;
using System.Xml.Schema;

namespace Tradux.Xsd.Security;

/// <summary>
/// Limita o tamanho dos modelos de conteúdo <b>antes</b> de compilar o XSD.
/// A compilação do .NET monta um autômato por tipo complexo cujo custo explode
/// com muitos elementos num bloco (10 mil opcionais numa sequência: ~21 s) e
/// soma por tipo (mil tipos reutilizando um grupo de mil: ~29 s).
///
/// Para cada tipo complexo (global ou anônimo), conta os elementos do modelo
/// de conteúdo expandido. Entram grupos internos, <c>xs:group ref</c> e herança
/// por extensão. A contagem é memoizada por grupo/tipo, então o custo é
/// linear. São recusados:
/// - um tipo acima de <see cref="XsdImportSecurityOptions.MaxElementsPerContentModel"/>;
/// - a soma de todos os tipos acima de
///   <see cref="XsdImportSecurityOptions.MaxContentModelParticlesTotal"/>;
/// - mais de <see cref="XsdImportSecurityOptions.MaxElementDeclarations"/>
///   elementos declarados;
/// - referência circular ou cadeia de referências acima de
///   <see cref="XsdImportSecurityOptions.MaxReferenceHeight"/> (fail-closed, nunca contar como 0);
/// - <c>xs:redefine</c> (não usado por XSD fiscal; contornaria a contagem);
/// - atributos expandidos (próprios, <c>attributeGroup</c> e herança) acima de
///   <see cref="XsdImportSecurityOptions.MaxAttributesPerType"/> por tipo ou de
///   <see cref="XsdImportSecurityOptions.MaxAttributeUsesTotal"/> no total.
///
/// Também limita o que a compilação ou a validação expandem fora dos modelos
/// de conteúdo:
/// - membros de <c>union</c>/<c>list</c> expandidos (uniões que reutilizam
///   uniões crescem exponencialmente);
/// - namespaces listados em curingas (<c>xs:any</c>/<c>xs:anyAttribute</c>),
///   que se acumulam pela herança;
/// - XPath de <c>xs:key</c>/<c>xs:unique</c>/<c>xs:keyref</c>, avaliado em cada
///   XML de entrada.
///
/// Por fim, limita as avaliações de pattern que o validador faz numa única
/// leitura: por valor (cadeia de restrições × membros de union) e por tag
/// (atributos da mesma tag de abertura). Cada avaliação pode custar até o
/// timeout de regex, e o prazo total da operação só é checado entre leituras.
///
/// Antes de tudo, <see cref="ReferenceGraphGuard"/> limita a altura do grafo de
/// referências; isso também limita a recursão desta contagem.
///
/// <c>maxOccurs</c> não multiplica o custo da compilação (medido), por isso não
/// entra na conta. O pacote nacional da NFS-e 1.01 inteiro declara 505 elementos.
/// </summary>
internal static class ContentModelSizeChecker
{
    /// <exception cref="InvalidOperationException">Algum limite excedido, referência circular ou <c>xs:redefine</c>.</exception>
    public static void Check(XmlSchemaSet schemaSet, XsdImportSecurityOptions options)
    {
        var schemas = new List<XmlSchema>();
        var seen = new HashSet<XmlSchema>(ReferenceEqualityComparer.Instance);
        foreach (XmlSchema schema in schemaSet.Schemas())
        {
            Collect(schema, schemas, seen);
        }

        ReferenceGraphGuard.Check(schemas, options);

        var groups = new Dictionary<XmlQualifiedName, XmlSchemaGroup>();
        var types = new Dictionary<XmlQualifiedName, XmlSchemaComplexType>();
        var attributeGroups = new Dictionary<XmlQualifiedName, XmlSchemaAttributeGroup>();
        var simpleTypes = new Dictionary<XmlQualifiedName, XmlSchemaSimpleType>();
        var globalAttributes = new Dictionary<XmlQualifiedName, XmlSchemaAttribute>();
        foreach (var schema in schemas)
        {
            var ns = schema.TargetNamespace ?? "";
            foreach (var item in schema.Items)
            {
                switch (item)
                {
                    case XmlSchemaGroup group when group.Name is not null:
                        groups.TryAdd(new XmlQualifiedName(group.Name, ns), group);
                        break;
                    case XmlSchemaComplexType type when type.Name is not null:
                        types.TryAdd(new XmlQualifiedName(type.Name, ns), type);
                        break;
                    case XmlSchemaAttributeGroup attributeGroup when attributeGroup.Name is not null:
                        attributeGroups.TryAdd(new XmlQualifiedName(attributeGroup.Name, ns), attributeGroup);
                        break;
                    case XmlSchemaSimpleType simple when simple.Name is not null:
                        simpleTypes.TryAdd(new XmlQualifiedName(simple.Name, ns), simple);
                        break;
                    case XmlSchemaAttribute attribute when attribute.Name is not null:
                        globalAttributes.TryAdd(new XmlQualifiedName(attribute.Name, ns), attribute);
                        break;
                }
            }
        }

        var counter = new Counter(groups, types, attributeGroups, simpleTypes, globalAttributes, options);
        foreach (var schema in schemas)
        {
            foreach (var item in schema.Items)
            {
                switch (item)
                {
                    case XmlSchemaComplexType type:
                        counter.CheckType(type, type.Name);
                        break;
                    case XmlSchemaElement element:
                        counter.Declared();
                        counter.CheckElement(element);
                        break;
                    case XmlSchemaGroup group:
                        counter.CheckGroup(group);
                        break;
                    case XmlSchemaSimpleType simple:
                        counter.CheckSimple(simple);
                        break;
                    case XmlSchemaAttributeGroup attributeGroup:
                        counter.Wildcard(attributeGroup.AnyAttribute?.Namespace);
                        counter.CheckAttributes(attributeGroup.Attributes);
                        break;
                    case XmlSchemaAttribute { SchemaType: { } globalAttributeType }:
                        counter.CheckSimple(globalAttributeType);
                        break;
                }
            }
        }
    }

    // Esquemas incluídos/importados ficam em Includes depois do Add (pré-processamento).
    private static void Collect(XmlSchema schema, List<XmlSchema> into, HashSet<XmlSchema> seen)
    {
        if (!seen.Add(schema))
        {
            return;
        }

        into.Add(schema);
        foreach (var external in schema.Includes.OfType<XmlSchemaExternal>())
        {
            if (external is XmlSchemaRedefine)
            {
                throw new InvalidOperationException(
                    "xs:redefine não é suportado na importação de XSD (nenhum leiaute fiscal usa; use xs:include/xs:import).");
            }

            if (external.Schema is { } nested)
            {
                Collect(nested, into, seen);
            }
        }
    }

    private sealed class Counter(
        Dictionary<XmlQualifiedName, XmlSchemaGroup> groups,
        Dictionary<XmlQualifiedName, XmlSchemaComplexType> types,
        Dictionary<XmlQualifiedName, XmlSchemaAttributeGroup> attributeGroups,
        Dictionary<XmlQualifiedName, XmlSchemaSimpleType> simpleTypes,
        Dictionary<XmlQualifiedName, XmlSchemaAttribute> globalAttributes,
        XsdImportSecurityOptions options)
    {
        private readonly Dictionary<object, long> _patternCosts = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<object> _patternVisiting = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<object, long> _tagPatternCosts = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<object> _tagPatternVisiting = new(ReferenceEqualityComparer.Instance);

        // Avaliações de pattern para validar um valor deste tipo: cada passo de restrição com pattern, somado pelos membros de union.
        private long PatternCost(XmlSchemaSimpleType simple, int depth) => Memo(_patternCosts, _patternVisiting, simple, depth, () => simple.Content switch
        {
            XmlSchemaSimpleTypeRestriction restriction =>
                (restriction.Facets.OfType<XmlSchemaPatternFacet>().Any() ? 1 : 0)
                + (restriction.BaseType is { } inline ? PatternCost(inline, depth + 1) : NamedPatternCost(restriction.BaseTypeName, depth)),
            XmlSchemaSimpleTypeList list => list.ItemType is { } inline ? PatternCost(inline, depth + 1) : NamedPatternCost(list.ItemTypeName, depth),
            XmlSchemaSimpleTypeUnion union => Math.Min(
                (union.MemberTypes ?? []).Sum(member => NamedPatternCost(member, depth))
                + union.BaseTypes.OfType<XmlSchemaSimpleType>().Sum(inline => PatternCost(inline, depth + 1)),
                _cap + options.MaxAttributeUsesTotal),
            _ => 0,
        });

        private long NamedPatternCost(XmlQualifiedName name, int depth) =>
            simpleTypes.TryGetValue(name, out var named) ? PatternCost(named, depth + 1) : 0;

        private long AttributePatternCost(XmlSchemaAttribute attribute, int depth) =>
            attribute.SchemaType is { } inline ? PatternCost(inline, depth + 1)
            : !attribute.RefName.IsEmpty && globalAttributes.TryGetValue(attribute.RefName, out var global) && !ReferenceEquals(global, attribute) ? AttributePatternCost(global, depth + 1)
            : NamedPatternCost(attribute.SchemaTypeName, depth);

        // Atributos da mesma tag de abertura são validados numa mesma leitura (próprios, attributeGroup e herança).
        private long TagPatternCost(XmlSchemaComplexType type, int depth) => Memo(_tagPatternCosts, _tagPatternVisiting, type, depth, () =>
        {
            var cost = AttributeListPatternCost(type.Attributes, depth);
            switch (type.ContentModel?.Content)
            {
                case XmlSchemaComplexContentExtension extension:
                    cost += AttributeListPatternCost(extension.Attributes, depth) + BaseTagPatternCost(extension.BaseTypeName, depth);
                    break;
                case XmlSchemaComplexContentRestriction restriction:
                    cost += AttributeListPatternCost(restriction.Attributes, depth) + BaseTagPatternCost(restriction.BaseTypeName, depth);
                    break;
                case XmlSchemaSimpleContentExtension extension:
                    cost += AttributeListPatternCost(extension.Attributes, depth) + BaseTagPatternCost(extension.BaseTypeName, depth);
                    break;
                case XmlSchemaSimpleContentRestriction restriction:
                    cost += AttributeListPatternCost(restriction.Attributes, depth) + BaseTagPatternCost(restriction.BaseTypeName, depth);
                    break;
            }

            return cost;
        });

        private long BaseTagPatternCost(XmlQualifiedName baseName, int depth) =>
            types.TryGetValue(baseName, out var baseType) ? TagPatternCost(baseType, depth + 1) : 0;

        private long AttributeListPatternCost(XmlSchemaObjectCollection attributes, int depth)
        {
            long cost = 0;
            foreach (var item in attributes)
            {
                cost += item switch
                {
                    XmlSchemaAttribute attribute => AttributePatternCost(attribute, depth),
                    XmlSchemaAttributeGroupRef reference when attributeGroups.TryGetValue(reference.RefName, out var group) =>
                        Memo(_tagPatternCosts, _tagPatternVisiting, group, depth + 1, () => AttributeListPatternCost(group.Attributes, depth + 1)),
                    _ => 0,
                };
                cost = Math.Min(cost, _cap + options.MaxAttributeUsesTotal);
            }

            return cost;
        }

        private readonly Dictionary<object, long> _memberSizes = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<object> _memberVisiting = new(ReferenceEqualityComparer.Instance);
        private int _wildcardNamespaces;
        private int _identityConstraints;
        private int _identityCost;
        private long _unionTotal;

        // Uniões que reutilizam uniões: membros expandidos memoizados, limite por tipo.
        public void CheckSimple(XmlSchemaSimpleType simple)
        {
            if (PatternCost(simple, depth: 0) > options.MaxPatternEvaluationsPerValue)
            {
                throw new InvalidOperationException(
                    $"O tipo simples '{simple.Name ?? "(anônimo)"}' do XSD exige mais de {options.MaxPatternEvaluationsPerValue} avaliações de pattern por valor (restrições encadeadas × membros de union).");
            }

            var size = SimpleSize(simple, depth: 0);
            _unionTotal = Math.Min(_unionTotal + size, _cap + options.MaxUnionMembersTotal);
            if (size > options.MaxUnionMembers || _unionTotal > options.MaxUnionMembersTotal)
            {
                throw new InvalidOperationException(
                    $"O tipo simples '{simple.Name ?? "(anônimo)"}' do XSD se expande em mais de {options.MaxUnionMembers} membros de union/list (ou o XSD soma mais de {options.MaxUnionMembersTotal}).");
            }
        }

        // Atributos com tipo simples anônimo (locais, globais, de attributeGroup, de extensão/restrição).
        public void CheckAttributes(XmlSchemaObjectCollection attributes)
        {
            foreach (var attribute in attributes.OfType<XmlSchemaAttribute>())
            {
                if (attribute.SchemaType is { } simple)
                {
                    CheckSimple(simple);
                }
            }
        }

        private long SimpleSize(XmlSchemaSimpleType simple, int depth) => Memo(_memberSizes, _memberVisiting, simple, depth, () => simple.Content switch
        {
            XmlSchemaSimpleTypeRestriction restriction => restriction.BaseType is { } inline ? SimpleSize(inline, depth + 1) : Named(restriction.BaseTypeName, depth),
            XmlSchemaSimpleTypeList list => list.ItemType is { } inline ? SimpleSize(inline, depth + 1) : Named(list.ItemTypeName, depth),
            XmlSchemaSimpleTypeUnion union => Math.Min(
                (union.MemberTypes ?? []).Sum(member => Named(member, depth))
                + union.BaseTypes.OfType<XmlSchemaSimpleType>().Sum(inline => SimpleSize(inline, depth + 1)),
                _cap + options.MaxAttributeUsesTotal),
            _ => 1,
        });

        private long Named(XmlQualifiedName name, int depth) =>
            simpleTypes.TryGetValue(name, out var named) ? SimpleSize(named, depth + 1) : 1;

        // Lista de namespaces do curinga ("##any", "##other" ou URIs): cada uma vira restrição na compilação/herança.
        public void Wildcard(string? namespaces)
        {
            if (string.IsNullOrWhiteSpace(namespaces))
            {
                return;
            }

            var count = namespaces!.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length; // não nulo: checado acima
            _wildcardNamespaces += count;
            if (count > options.MaxWildcardNamespaces || _wildcardNamespaces > options.MaxWildcardNamespacesTotal)
            {
                throw new InvalidOperationException(
                    $"Curinga xs:any/xs:anyAttribute do XSD com namespaces demais (até {options.MaxWildcardNamespaces} por curinga e {options.MaxWildcardNamespacesTotal} no total).");
            }
        }

        // XPath de chave/unicidade é avaliado em cada XML de entrada: tamanho e ramos limitados.
        private void CheckIdentity(XmlSchemaElement element)
        {
            foreach (var constraint in element.Constraints.OfType<XmlSchemaIdentityConstraint>())
            {
                // Cada restrição é avaliada em todo XML de entrada (memória e CPU por elemento): custo combinado limitado.
                _identityCost += constraint.Fields.Count + 1;
                if (++_identityConstraints > options.MaxIdentityConstraints || constraint.Fields.Count > options.MaxIdentityFields
                    || _identityCost > options.MaxIdentityCost)
                {
                    throw new InvalidOperationException(
                        $"xs:key/xs:unique/xs:keyref demais no XSD (até {options.MaxIdentityConstraints} restrições, {options.MaxIdentityFields} campos cada e {options.MaxIdentityCost} campos+seletores no total).");
                }

                foreach (var xpath in new[] { constraint.Selector }.Concat(constraint.Fields.OfType<XmlSchemaXPath>()))
                {
                    var text = xpath?.XPath ?? "";
                    if (text.Length > options.MaxIdentityXPathLength || text.Count(c => c == '|') + 1 > options.MaxIdentityXPathBranches)
                    {
                        throw new InvalidOperationException(
                            $"XPath de xs:key/xs:unique/xs:keyref acima do limite ({options.MaxIdentityXPathLength} caracteres, {options.MaxIdentityXPathBranches} alternativas).");
                    }
                }
            }
        }

        private readonly Dictionary<object, long> _attributeSizes = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<object> _attributeVisiting = new(ReferenceEqualityComparer.Instance);
        private long _attributeTotal;

        // Contagens saturam aqui: nada acima disso muda a decisão (e não há estouro).
        private readonly long _cap = (long)Math.Max(options.MaxElementsPerContentModel, options.MaxContentModelParticlesTotal) + 1;
        private readonly Dictionary<object, long> _sizes = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<object> _visiting = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<object> _checked = new(ReferenceEqualityComparer.Instance);
        private int _declarations;
        private long _total;

        public void Declared()
        {
            if (++_declarations > options.MaxElementDeclarations)
            {
                throw new InvalidOperationException(
                    $"O XSD declara mais de {options.MaxElementDeclarations} elementos. XSDs fiscais reais declaram centenas.");
            }
        }

        public void CheckElement(XmlSchemaElement element)
        {
            CheckIdentity(element);
            switch (element.SchemaType)
            {
                case XmlSchemaComplexType anonymous:
                    CheckType(anonymous, element.Name);
                    break;
                case XmlSchemaSimpleType simple:
                    CheckSimple(simple);
                    break;
            }
        }

        public void CheckGroup(XmlSchemaGroup group)
        {
            Limit(GroupSize(group, depth: 0), $"grupo '{group.Name}'");
            CheckLocals(group.Particle);
        }

        // Cada tipo complexo vira um autômato na compilação: limite por tipo e soma global.
        public void CheckType(XmlSchemaComplexType type, string? owner)
        {
            if (!_checked.Add(type))
            {
                return;
            }

            var size = TypeSize(type, depth: 0);
            Limit(size, $"bloco '{owner ?? "(tipo anônimo)"}'");
            _total = Math.Min(_total + size, _cap);
            if (_total > options.MaxContentModelParticlesTotal)
            {
                throw new InvalidOperationException(
                    $"Os blocos do XSD somam mais de {options.MaxContentModelParticlesTotal} elementos (contando grupos reutilizados em cada bloco). " +
                    "XSDs fiscais reais ficam muito abaixo.");
            }

            if (TagPatternCost(type, depth: 0) > options.MaxPatternEvaluationsPerTag)
            {
                throw new InvalidOperationException(
                    $"O bloco '{owner ?? "(tipo anônimo)"}' do XSD tem atributos que somam mais de {options.MaxPatternEvaluationsPerTag} avaliações de pattern por tag.");
            }

            var attributes = AttributeCount(type, depth: 0);
            if (attributes > options.MaxAttributesPerType)
            {
                throw new InvalidOperationException(
                    $"O bloco '{owner ?? "(tipo anônimo)"}' do XSD tem mais de {options.MaxAttributesPerType} atributos (contando attributeGroup e herança).");
            }

            _attributeTotal = Math.Min(_attributeTotal + attributes, _cap + options.MaxAttributeUsesTotal);
            if (_attributeTotal > options.MaxAttributeUsesTotal)
            {
                throw new InvalidOperationException(
                    $"Os blocos do XSD somam mais de {options.MaxAttributeUsesTotal} atributos (contando attributeGroup e herança em cada bloco).");
            }

            CheckAttributes(type.Attributes);
            switch (type.ContentModel?.Content)
            {
                case XmlSchemaComplexContentExtension extension:
                    CheckAttributes(extension.Attributes);
                    break;
                case XmlSchemaComplexContentRestriction restriction:
                    CheckAttributes(restriction.Attributes);
                    break;
                case XmlSchemaSimpleContentExtension extension:
                    CheckAttributes(extension.Attributes);
                    break;
                case XmlSchemaSimpleContentRestriction restriction:
                    CheckAttributes(restriction.Attributes);
                    if (restriction.BaseType is { } inline)
                    {
                        CheckSimple(inline);
                    }

                    break;
            }

            Wildcard(type.AnyAttribute?.Namespace);
            Wildcard((type.ContentModel?.Content as XmlSchemaComplexContentExtension)?.AnyAttribute?.Namespace);
            Wildcard((type.ContentModel?.Content as XmlSchemaComplexContentRestriction)?.AnyAttribute?.Namespace);
            Wildcard((type.ContentModel?.Content as XmlSchemaSimpleContentExtension)?.AnyAttribute?.Namespace);
            Wildcard((type.ContentModel?.Content as XmlSchemaSimpleContentRestriction)?.AnyAttribute?.Namespace);
            CheckLocals(ParticleOf(type));
        }

        private long AttributeCount(XmlSchemaComplexType type, int depth) => Memo(_attributeSizes, _attributeVisiting, type, depth, () =>
        {
            var count = CountAttributes(type.Attributes, depth);
            switch (type.ContentModel?.Content)
            {
                case XmlSchemaComplexContentExtension extension:
                    count += CountAttributes(extension.Attributes, depth) + BaseAttributes(extension.BaseTypeName, depth);
                    break;
                case XmlSchemaComplexContentRestriction restriction:
                    count += CountAttributes(restriction.Attributes, depth) + BaseAttributes(restriction.BaseTypeName, depth);
                    break;
                case XmlSchemaSimpleContentExtension extension:
                    count += CountAttributes(extension.Attributes, depth) + BaseAttributes(extension.BaseTypeName, depth);
                    break;
                case XmlSchemaSimpleContentRestriction restriction:
                    count += CountAttributes(restriction.Attributes, depth) + BaseAttributes(restriction.BaseTypeName, depth);
                    break;
            }

            return count;
        });

        private long BaseAttributes(XmlQualifiedName baseName, int depth) =>
            types.TryGetValue(baseName, out var baseType) ? AttributeCount(baseType, depth + 1) : 0;

        private long CountAttributes(XmlSchemaObjectCollection attributes, int depth)
        {
            long count = 0;
            foreach (var item in attributes)
            {
                count += item switch
                {
                    XmlSchemaAttribute => 1,
                    XmlSchemaAttributeGroupRef reference when attributeGroups.TryGetValue(reference.RefName, out var group) =>
                        Memo(_attributeSizes, _attributeVisiting, group, depth + 1, () => CountAttributes(group.Attributes, depth + 1)),
                    _ => 0,
                };
                count = Math.Min(count, _cap + options.MaxAttributeUsesTotal);
            }

            return count;
        }

        // Elementos locais com tipo anônimo formam modelos de conteúdo próprios.
        private void CheckLocals(XmlSchemaParticle? particle)
        {
            switch (particle)
            {
                case XmlSchemaElement element:
                    Declared();
                    CheckElement(element);
                    break;
                case XmlSchemaAny any:
                    Wildcard(any.Namespace);
                    break;
                case XmlSchemaGroupBase group:
                    foreach (var item in group.Items)
                    {
                        CheckLocals(item as XmlSchemaParticle);
                    }

                    break;
            }
        }

        private void Limit(long size, string what)
        {
            if (size > options.MaxElementsPerContentModel)
            {
                throw new InvalidOperationException(
                    $"O {what} do XSD tem mais de {options.MaxElementsPerContentModel} elementos num mesmo modelo de conteúdo. " +
                    "Divida em blocos menores (o limite protege o servidor; XSDs fiscais reais ficam muito abaixo).");
            }
        }

        private long TypeSize(XmlSchemaComplexType type, int depth) => Memo(_sizes, _visiting, type, depth, () =>
        {
            var size = Count(ParticleOf(type), depth);
            if (type.ContentModel?.Content is XmlSchemaComplexContentExtension extension && types.TryGetValue(extension.BaseTypeName, out var baseType))
            {
                size += TypeSize(baseType, depth + 1);
            }

            return size;
        });

        private long GroupSize(XmlSchemaGroup group, int depth) => Memo(_sizes, _visiting, group, depth, () => Count(group.Particle, depth));

        private long Memo(Dictionary<object, long> sizes, HashSet<object> visiting, object key, int depth, Func<long> compute)
        {
            if (sizes.TryGetValue(key, out var known))
            {
                return known;
            }

            if (depth > options.MaxReferenceHeight)
            {
                throw new InvalidOperationException($"Cadeia de referências (xs:group/extensão) com mais de {options.MaxReferenceHeight} níveis no XSD.");
            }

            if (!visiting.Add(key))
            {
                throw new InvalidOperationException("Referência circular de xs:group, attributeGroup ou de tipo base no XSD.");
            }

            var size = Math.Min(compute(), _cap + options.MaxAttributeUsesTotal);
            visiting.Remove(key);
            sizes[key] = size;
            return size;
        }

        private long Count(XmlSchemaParticle? particle, int depth)
        {
            switch (particle)
            {
                case XmlSchemaElement or XmlSchemaAny:
                    return 1;
                case XmlSchemaGroupRef reference:
                    // Grupo inexistente: o Compile recusa com a mensagem certa.
                    return groups.TryGetValue(reference.RefName, out var referenced) ? GroupSize(referenced, depth + 1) : 0;
                case XmlSchemaGroupBase group:
                    long total = 0;
                    foreach (var item in group.Items)
                    {
                        total = Math.Min(total + Count(item as XmlSchemaParticle, depth), _cap);
                    }

                    return total;
                default:
                    return 0;
            }
        }

        private static XmlSchemaParticle? ParticleOf(XmlSchemaComplexType type) => type.ContentModel?.Content switch
        {
            XmlSchemaComplexContentExtension extension => extension.Particle,
            XmlSchemaComplexContentRestriction restriction => restriction.Particle,
            _ => type.Particle,
        };
    }
}

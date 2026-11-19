using System.Text.Json;
using Tradux.Domain.Mapping;
using Tradux.Domain.Schema;
using Tradux.Engine.Operators;
using Tradux.Xsd;

namespace Tradux.Application.Quality;

/// <summary>Destino alimentado por referência pura ao campo de origem — é o que diz o que é entrada válida.</summary>
/// <param name="DestinationPath">Caminho lógico do destino (mesmo do editor, <c>/DPS/numero</c>).</param>
/// <param name="Element">Elemento do XSD (tipo, facetas, nillable, default).</param>
/// <param name="Required">Obrigatório de fato: ele e todos os ancestros com <c>minOccurs ≥ 1</c> no escopo.</param>
/// <param name="Conditional">A regra tem condição (pode não ser gerada).</param>
public sealed record DirectTarget(string DestinationPath, SchemaElement Element, bool Required, bool Conditional);

/// <summary>
/// xs:choice do destino com mais de um ramo (ADR-0020). A suíte escolhe ramos por
/// caso: um ramo por vez nos casos "ramo:", e um ramo padrão nos demais casos
/// quando o choice não repete (o motor recusa dois ramos num choice único).
/// </summary>
/// <param name="Path">Caminho do elemento que contém o choice, com o marcador do grupo.</param>
/// <param name="Repeatable">O choice repete (maxOccurs &gt; 1): todos os ramos cabem juntos.</param>
/// <param name="Branches">Nome de cada ramo, na ordem do schema.</param>
public sealed record ChoiceProfile(string Path, bool Repeatable, IReadOnlyList<string> Branches);

/// <summary>Um ramo de um <see cref="ChoiceProfile"/>.</summary>
public readonly record struct BranchRef(ChoiceProfile Choice, int Branch);

/// <summary>Um uso: os ramos que o envolvem e se depende de condição (aí não força o ramo a aparecer).</summary>
public readonly record struct BranchUse(IReadOnlyList<BranchRef> Branches, bool Conditional);

/// <summary>
/// Onde um campo ou uma coleção de origem é usado: os ramos de choice que
/// envolvem cada uso (vazio = fora de qualquer choice). Uso dentro de um ramo
/// não escolhido não precisa do dado.
/// </summary>
public abstract class SourceUsage
{
    public List<BranchUse> Usages { get; } = [];

    /// <summary>
    /// Fora do caso: todo uso está dentro de algum ramo não escolhido
    /// (<paramref name="selection"/>: choice → ramos escolhidos; ausente = todos).
    /// </summary>
    public bool ExcludedBy(IReadOnlyDictionary<ChoiceProfile, HashSet<int>> selection) =>
        Usages.Count > 0 && Usages.All(use => use.Branches.Any(b => selection.TryGetValue(b.Choice, out var chosen) && !chosen.Contains(b.Branch)));
}

/// <summary>Como o template usa um caminho de origem (ADR-0011 §2).</summary>
public sealed class SourceField(string path) : SourceUsage
{
    public string Path { get; } = path;

    public List<DirectTarget> Direct { get; } = [];

    /// <summary>Tipos esperados pelos operadores em que o campo entra como argumento.</summary>
    public HashSet<RuleArgumentKind> Hints { get; } = [];

    /// <summary>Tabelas DE-PARA em que o campo é a chave.</summary>
    public SortedSet<string> LookupTables { get; } = new(StringComparer.Ordinal);

    /// <summary>Entra no valor de algum destino por uma transformação (não referência pura nem chave de lookup).</summary>
    public bool Transformed { get; set; }

    /// <summary>Entra em condição, filtro ou ordenação.</summary>
    public bool InPredicate { get; set; }

    /// <summary>Alimenta (valor ou condição) algum destino obrigatório.</summary>
    public bool FeedsRequired { get; set; }

    /// <summary>
    /// Restrição do destino vale para a entrada: tem destino direto, todos os
    /// destinos diretos têm o mesmo tipo e as mesmas facetas, e o campo não
    /// passa por transformação que mude o valor gerado.
    /// </summary>
    public bool Constrained => Direct.Count > 0 && !Transformed && Direct.Select(d => ConstraintKey(d.Element)).Distinct().Count() == 1;

    /// <summary>Inválidos esperados só onde nada pode "absorver" o valor: referência pura, sem condição, filtro ou lookup.</summary>
    public bool InvalidEligible => Constrained && !InPredicate && LookupTables.Count == 0 && Direct.All(d => !d.Conditional);

    public SchemaElement? Element => Constrained ? Direct[0].Element : null;

    private static string ConstraintKey(SchemaElement element) =>
        $"{element.ScalarType}|{JsonSerializer.Serialize(element.Facets)}";
}

/// <summary>Coleção de origem percorrida por um bloco "for each".</summary>
public sealed class SourceCollection(string path, SourceScope root) : SourceUsage
{
    public string Path { get; } = path;

    public SourceScope Item { get; } = new() { Root = root };

    /// <summary>Menor quantidade de itens exigida pelo destino (0 se o destino ou um ancestro é opcional).</summary>
    public int MinOccurs { get; set; } = int.MaxValue;

    public int? MaxOccurs { get; set; }

    public bool FeedsRequired { get; set; }

    /// <summary>O mapeamento aceita objeto único no lugar da lista (<see cref="CollectionMapping.SingleObjectAsItem"/>).</summary>
    public bool SingleObjectAsItem { get; set; }
}

/// <summary>Campos e coleções de um escopo (raiz ou item de coleção).</summary>
public sealed class SourceScope
{
    /// <summary>Raiz da entrada (nulo na própria raiz): destino de <see cref="FieldScope.Root"/>.</summary>
    public SourceScope? Root { get; init; }

    public SortedDictionary<string, SourceField> Fields { get; } = new(StringComparer.Ordinal);

    public SortedDictionary<string, SourceCollection> Collections { get; } = new(StringComparer.Ordinal);

    public SourceField Field(string path, FieldScope scope = FieldScope.Current)
    {
        if (scope == FieldScope.Root && Root is { } root)
        {
            return root.Field(path);
        }

        if (!Fields.TryGetValue(path, out var field))
        {
            Fields[path] = field = new SourceField(path);
        }

        return field;
    }

    public SourceCollection Collection(string path)
    {
        if (!Collections.TryGetValue(path, out var collection))
        {
            Collections[path] = collection = new SourceCollection(path, Root ?? this);
        }

        return collection;
    }
}

/// <summary>Item de cobertura que a suíte não pode exercitar, e por quê (nunca contado como coberto).</summary>
public sealed record CoverageNote(string Area, string Path, string Reason);

/// <summary>Perfil da entrada que o template consome, inferido do mapeamento + XSD de destino.</summary>
/// <param name="Root">Escopo raiz (campos e coleções).</param>
/// <param name="NotCovered">O que a suíte não exercita, e por quê.</param>
/// <param name="Choices">Choices do destino com mais de um ramo, na ordem do schema.</param>
/// <param name="AlwaysPresent">
/// Ramos com conteúdo sem dado (constante, padrão, nil). Sem condição, aparecem em todo caso;
/// com condição (<see cref="BranchUse.Conditional"/>), podem aparecer conforme o dado.
/// </param>
public sealed record SourceProfile(SourceScope Root, IReadOnlyList<CoverageNote> NotCovered, IReadOnlyList<ChoiceProfile> Choices, IReadOnlyList<BranchUse> AlwaysPresent);

/// <summary>
/// Monta o <see cref="SourceProfile"/> percorrendo o XSD de destino junto com
/// o mapeamento (ADR-0011 §2). Não executa regra alguma.
/// </summary>
public static class SourceProfiler
{
    /// <param name="schema">Schema de destino.</param>
    /// <param name="mapping">Mapeamento do template.</param>
    /// <param name="operators">Metadados dos operadores (padrão: catálogo nativo).</param>
    /// <param name="contentWithoutData">
    /// Destinos que geram conteúdo sem dado (<c>RuleExecutor.ContentWithoutData</c> do template
    /// compilado). Sem ele, nenhum ramo é tido como sempre presente.
    /// </param>
    public static SourceProfile Build(DestinationSchema schema, MappingTemplate mapping, IReadOnlyDictionary<string, RuleOperatorMetadata>? operators = null,
        IReadOnlyCollection<string>? contentWithoutData = null)
    {
        var walker = new Walker(operators ?? RuleOperatorMetadata.BuiltIn, contentWithoutData ?? new HashSet<string>());
        var root = new SourceScope();
        var rootPath = "/" + schema.RootElementName;
        walker.NoteUnmappableContent(schema.RootContent, rootPath);
        walker.Walk(schema.RootContent, mapping, rootPath, required: true, root, []);
        return new SourceProfile(root, walker.Notes, walker.Choices, walker.AlwaysPresent);
    }

    private sealed class Walker(IReadOnlyDictionary<string, RuleOperatorMetadata> operators, IReadOnlyCollection<string> contentWithoutData)
    {
        public List<CoverageNote> Notes { get; } = [];

        public List<ChoiceProfile> Choices { get; } = [];

        public List<BranchUse> AlwaysPresent { get; } = [];

        private int _groups;

        // outer: quantos ramos do contexto ficam FORA do item de coleção atual. Constante
        // dentro de um item só aparece se houver item (dado): não força os ramos de fora.
        public void Walk(SchemaParticle particle, MappingTemplate mapping, string parentPath, bool required, SourceScope scope, IReadOnlyList<BranchRef> context, int outer = 0)
        {
            // Choice opcional: nada nele é exigido. Choice com vários ramos: cada uso
            // registra o ramo (ADR-0020), para a suíte montar casos com um ramo por vez.
            required &= particle.Kind != ParticleKind.Choice || particle.Cardinality.IsRequired;
            ChoiceProfile? choice = null;
            if (particle.Kind == ParticleKind.Choice && particle.Items.Count > 1)
            {
                choice = new ChoiceProfile($"{parentPath}/({ParticleRules.ChoiceModel}#{++_groups})", particle.Cardinality.IsRepeatable, particle.Items.Select(BranchName).ToList());
                Choices.Add(choice);
            }

            for (var i = 0; i < particle.Items.Count; i++)
            {
                var branch = choice is null ? context : [.. context, new BranchRef(choice, i)];
                switch (particle.Items[i])
                {
                    case ScalarElementItem scalar:
                    {
                        var element = scalar.Element;
                        var path = $"{parentPath}/{element.Name}";
                        if (branch.Count > outer && contentWithoutData.Contains(path))
                        {
                            // Com condição, só aparece quando a condição vale: não força o ramo,
                            // mas é alternativa que pode ocupar o choice (ver AbsenceAbsorbed).
                            var conditional = mapping.Fields.Where(f => f.DestinationElementName == element.Name).All(f => f.Condition is not null);
                            AlwaysPresent.Add(new BranchUse(branch.Skip(outer).ToList(), conditional));
                        }

                        var elementRequired = required && element.Cardinality.MinOccurs >= 1;
                        foreach (var field in mapping.Fields.Where(f => f.DestinationElementName == element.Name))
                        {
                            AnalyzeField(field, new DirectTarget(path, element, elementRequired, field.Condition is not null), scope, branch);
                        }

                        break;
                    }

                    case ComplexElementItem complex when complex.Cardinality.MaxOccurs is null or > 1:
                    {
                        var path = $"{parentPath}/{complex.Name}";
                        NoteUnmappableContent(complex.Content, path);
                        foreach (var collectionMapping in mapping.Collections.Where(c => c.DestinationElementName == complex.Name))
                        {
                            var collection = scope.Collection(collectionMapping.SourceCollectionPath);
                            var min = required ? complex.Cardinality.MinOccurs : 0;
                            collection.MinOccurs = Math.Min(collection.MinOccurs, min);
                            collection.MaxOccurs = collection.MaxOccurs is { } m && complex.Cardinality.MaxOccurs is { } n ? Math.Min(m, n) : complex.Cardinality.MaxOccurs ?? collection.MaxOccurs;
                            collection.FeedsRequired |= min > 0;
                            collection.SingleObjectAsItem |= collectionMapping.SingleObjectAsItem;
                            collection.Usages.Add(new BranchUse(branch, Conditional: false));
                            Visit(collectionMapping.Filter, collection.Item, predicate: true, feedsRequired: false, new BranchUse(branch, Conditional: true));
                            Visit(collectionMapping.OrderByKey, collection.Item, predicate: true, feedsRequired: false, new BranchUse(branch, Conditional: true));
                            Walk(complex.Content, collectionMapping.ItemContent, path, required: true, collection.Item, branch, outer: branch.Count);
                        }

                        break;
                    }

                    case ComplexElementItem complex:
                    {
                        var path = $"{parentPath}/{complex.Name}";
                        NoteUnmappableContent(complex.Content, path);
                        foreach (var complexMapping in mapping.Complex.Where(c => c.DestinationElementName == complex.Name))
                        {
                            Walk(complex.Content, complexMapping.Content, path, required && complex.Cardinality.MinOccurs >= 1, scope, branch, outer);
                        }

                        break;
                    }

                    case GroupItem group:
                        Walk(group.Group, mapping, parentPath, required && group.Group.Cardinality.MinOccurs >= 1, scope, branch, outer);
                        break;
                }
            }
        }

        private static string BranchName(SchemaParticleItem item) => item switch
        {
            ScalarElementItem scalar => scalar.Element.Name,
            ComplexElementItem complex => complex.Name,
            GroupItem group => $"({ParticleRules.ContentModel(group.Group.Kind)})",
            _ => "?",
        };

        // Conteúdo opaco (xs:any, conteúdo simples com atributos) não é gerado pelo motor.
        public void NoteUnmappableContent(SchemaParticle particle, string path)
        {
            if (ParticleRules.ContainsOpaque(particle))
            {
                Notes.Add(new CoverageNote("opaque", path, "Conteúdo opaco (xs:any ou conteúdo simples com atributos) não é gerado pelo motor; não exercitado."));
            }
        }

        private void AnalyzeField(FieldMapping mapping, DirectTarget target, SourceScope scope, IReadOnlyList<BranchRef> branch)
        {
            if (mapping.Value is FieldReferenceNode reference)
            {
                if (!IsSynthetic(reference))
                {
                    var field = scope.Field(reference.Path, reference.Scope);
                    field.Direct.Add(target);
                    field.FeedsRequired |= target.Required;
                    field.Usages.Add(new BranchUse(branch, target.Conditional));
                }
            }
            else
            {
                Visit(mapping.Value, scope, predicate: false, target.Required, new BranchUse(branch, target.Conditional));
            }

            // Campo de condição não preenche a tag: não força o ramo a aparecer.
            Visit(mapping.Condition, scope, predicate: true, target.Required, new BranchUse(branch, Conditional: true));
        }

        private void Visit(RuleNode? node, SourceScope scope, bool predicate, bool feedsRequired, BranchUse branch)
        {
            switch (node)
            {
                case FieldReferenceNode reference when !IsSynthetic(reference):
                {
                    var field = scope.Field(reference.Path, reference.Scope);
                    field.FeedsRequired |= feedsRequired;
                    field.Usages.Add(branch);
                    if (predicate)
                    {
                        field.InPredicate = true;
                    }
                    else
                    {
                        field.Transformed = true;
                    }

                    break;
                }

                case OperatorNode op:
                {
                    operators.TryGetValue(op.OperatorName, out var metadata);
                    var table = LookupTable(metadata, op);
                    for (var i = 0; i < op.Arguments.Count; i++)
                    {
                        var argument = op.Arguments[i];
                        if (argument is FieldReferenceNode reference && !IsSynthetic(reference))
                        {
                            var field = scope.Field(reference.Path, reference.Scope);
                            field.FeedsRequired |= feedsRequired;
                            field.Usages.Add(branch);
                            if (table is { } lookup && i == lookup.KeyIndex)
                            {
                                field.LookupTables.Add(lookup.Name);
                                field.InPredicate |= predicate;
                                continue; // chave de lookup: o valor gerado vem da tabela, não do campo
                            }

                            if (Kind(metadata, i) is { } kind and not RuleArgumentKind.Any and not RuleArgumentKind.LookupTable)
                            {
                                field.Hints.Add(kind);
                            }
                        }

                        Visit(argument, scope, predicate, feedsRequired, branch);
                    }

                    break;
                }
            }
        }

        /// <summary>Tabela DE-PARA literal do operador (parâmetro <see cref="RuleArgumentKind.LookupTable"/>); a chave é o argumento anterior a ela.</summary>
        private static (string Name, int KeyIndex)? LookupTable(RuleOperatorMetadata? metadata, OperatorNode op)
        {
            var index = metadata?.Parameters.ToList().FindIndex(p => p.Kind == RuleArgumentKind.LookupTable) ?? -1;
            return index > 0 && index < op.Arguments.Count && op.Arguments[index] is LiteralNode { Value: string name }
                ? (name, index - 1)
                : null;
        }

        private static RuleArgumentKind? Kind(RuleOperatorMetadata? metadata, int index)
        {
            if (metadata is null || metadata.Parameters.Count == 0)
            {
                return null;
            }

            if (index < metadata.Parameters.Count)
            {
                return metadata.Parameters[index].Kind;
            }

            return metadata.RepeatsLastParameter ? metadata.Parameters[^1].Kind : null;
        }

        // "$index" e afins são valores do motor, não da entrada.
        private static bool IsSynthetic(FieldReferenceNode reference) =>
            reference.Scope == FieldScope.Current && reference.Path.StartsWith('$');
    }
}

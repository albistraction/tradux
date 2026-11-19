using System.Linq.Expressions;
using System.Reflection;
using Tradux.Domain.Mapping;
using Tradux.Domain.SourceData;
using Tradux.Engine.Diagnostics;
using Tradux.Engine.Operators;
using Tradux.Xsd;

namespace Tradux.Engine.Compilation;

/// <summary>
/// Compilador de template (docs/architecture/COMPONENTS.md). Percorre a
/// árvore de schema (<see cref="SchemaParticle"/>) e o
/// <see cref="MappingTemplate"/> correspondente juntos, produzindo
/// uma <see cref="CompiledTemplate"/> executável — cada valor já é um
/// delegate compilado via <see cref="System.Linq.Expressions"/> (ADR-0001);
/// nada aqui é reinterpretado a cada geração.
///
/// Conteúdo: <c>xs:sequence</c>, <c>xs:all</c> e <c>xs:choice</c>, inclusive
/// grupos aninhados, que são transparentes no mapeamento (ADR-0018). Elementos
/// de um <c>xs:choice</c> não são obrigatórios um a um: o choice exige ramos
/// (ver <c>RuleExecutor</c>). Só o conteúdo opaco (<c>xs:any</c>, conteúdo
/// simples com atributos ou vazio) não é gerado; mapeá-lo é recusado
/// (<see cref="NotSupportedException"/>).
/// </summary>
public static class TemplateCompiler
{
    private static readonly MethodInfo TryGetValueMethod =
        typeof(SourceDataTree).GetMethod(nameof(SourceDataTree.TryGetValue))
        ?? throw new InvalidOperationException($"{nameof(SourceDataTree.TryGetValue)} não encontrado.");

    public static CompiledTemplate Compile(
        MappingTemplate template,
        ImportedXsd xsd,
        RuleOperatorCatalog? operatorCatalog = null,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? lookupTables = null)
    {
        var catalog = operatorCatalog ?? RuleOperatorCatalog.CreateDefault();
        var context = new OperatorBuildContext(lookupTables ?? new Dictionary<string, IReadOnlyDictionary<string, string>>());
        var diagnostics = new List<EngineDiagnostic>();

        var attemptsToMapSomething = template.Fields.Count > 0 || template.Complex.Count > 0 || template.Collections.Count > 0;
        if (attemptsToMapSomething)
        {
            // Um MappingTemplate vazio ainda cai no caminho normal de
            // "obrigatório sem mapeamento", item a item.
            RequireGenerableContent(xsd.Schema.RootElementName, xsd.Schema.RootContent);
        }

        var rootContent = CompileParticle(
            xsd.Schema.RootContent, template, catalog, context, diagnostics, "/" + xsd.Schema.RootElementName, optional: false);

        return new CompiledTemplate { Xsd = xsd, RootContent = rootContent, CompileTimeDiagnostics = diagnostics };
    }

    private static CompiledParticle CompileParticle(
        SchemaParticle particle,
        MappingTemplate mapping,
        RuleOperatorCatalog catalog,
        OperatorBuildContext context,
        List<EngineDiagnostic> diagnostics,
        string xpathPrefix,
        bool optional)
    {
        var fieldsByName = mapping.Fields.ToDictionary(f => f.DestinationElementName);
        var complexByName = mapping.Complex.ToDictionary(c => c.DestinationElementName);
        var collectionsByName = mapping.Collections.ToDictionary(c => c.DestinationElementName);
        var bindings = new List<CompiledItemBinding>();

        // Ramos de choice não são obrigatórios um a um; o choice exige ramos (execução).
        var choice = particle.Kind == ParticleKind.Choice;
        var itemsOptional = optional || choice;
        foreach (var item in particle.Items)
        {
            switch (item)
            {
                case ScalarElementItem scalar:
                    CompileScalarItem(scalar.Element, fieldsByName, catalog, context, diagnostics, xpathPrefix, bindings, itemsOptional);
                    break;

                case ComplexElementItem complex:
                    CompileComplexItem(
                        complex, complexByName, collectionsByName, catalog, context, diagnostics, xpathPrefix, bindings, itemsOptional);
                    break;

                case GroupItem { Group.Items.Count: 0 }:
                    break; // xs:any opcional: nunca gerado (obrigatório é recusado em RequireGenerableContent)

                case GroupItem group:
                    var inner = CompileParticle(group.Group, mapping, catalog, context, diagnostics, xpathPrefix,
                        itemsOptional || !group.Group.Cardinality.IsRequired);
                    if (inner.Items.Count > 0)
                    {
                        bindings.Add(new CompiledGroupBinding { Name = $"({group.Group.Kind.ToString().ToLowerInvariant()})", Group = inner });
                    }

                    break;

                default:
                    throw new NotSupportedException($"Item de schema '{item.GetType().Name}' não suportado.");
            }
        }

        if (choice && !optional && particle.Cardinality.IsRequired && bindings.Count == 0)
        {
            var branches = string.Join(", ", particle.Items.Select(BranchName).Where(n => n is not null));
            diagnostics.Add(EngineDiagnostic.Critical(
                "MAPPING_MISSING_REQUIRED_CHOICE",
                $"O xs:choice obrigatório em '{xpathPrefix}' não tem nenhum ramo mapeado ({branches}).",
                destinationXPath: xpathPrefix,
                suggestion: "Mapeie ao menos um dos ramos do choice."));
        }

        return new CompiledParticle { Items = bindings, Kind = particle.Kind, Cardinality = particle.Cardinality };
    }

    private static string? BranchName(SchemaParticleItem item) => item switch
    {
        ScalarElementItem scalar => scalar.Element.Name,
        ComplexElementItem complex => complex.Name,
        GroupItem { Group.Items.Count: > 0 } group => $"({group.Group.Kind.ToString().ToLowerInvariant()})",
        _ => null,
    };

    private static void CompileScalarItem(
        SchemaElement element,
        IReadOnlyDictionary<string, FieldMapping> fieldsByName,
        RuleOperatorCatalog catalog,
        OperatorBuildContext context,
        List<EngineDiagnostic> diagnostics,
        string xpathPrefix,
        List<CompiledItemBinding> bindings,
        bool optional)
    {
        var xpath = $"{xpathPrefix}/{element.Name}";

        if (!fieldsByName.TryGetValue(element.Name, out var mapping))
        {
            if (optional)
            {
                return; // ramo de choice ou grupo opcional sem mapeamento: não é gerado
            }

            if (element.DefaultValue is not null)
            {
                // Sem mapeamento, mas o schema declara um default: ainda
                // assim precisa de um binding, para que RuleExecutor aplique
                // o valor padrão em vez de o elemento desaparecer da saída.
                bindings.Add(new CompiledScalarBinding
                {
                    Name = element.Name,
                    Element = element,
                    SourceDescription = "(sem mapeamento — usa valor padrão do schema)",
                    Resolve = static _ => null,
                });
            }
            else if (element.Cardinality.IsRequired)
            {
                diagnostics.Add(EngineDiagnostic.Critical(
                    EngineDiagnosticCodes.MappingMissingRequired,
                    $"Elemento obrigatório '{element.Name}' não tem mapeamento.",
                    destinationXPath: xpath,
                    suggestion: $"Mapeie o elemento '{element.Name}'."));
            }

            return;
        }

        var resolve = CompileValueExpression(mapping.Value, catalog, context);
        if (mapping.Condition is not null)
        {
            var evaluateCondition = CompileValueExpression(mapping.Condition, catalog, context);
            var valueWhenTrue = resolve;
            resolve = tree => ScalarValues.ToBool(evaluateCondition(tree)) ? valueWhenTrue(tree) : null;
        }

        bindings.Add(new CompiledScalarBinding
        {
            Name = element.Name,
            Element = element,
            SourceDescription = Describe(mapping.Value),
            Resolve = resolve,
        });
    }

    private static void CompileComplexItem(
        ComplexElementItem complex,
        IReadOnlyDictionary<string, ComplexMapping> complexByName,
        IReadOnlyDictionary<string, CollectionMapping> collectionsByName,
        RuleOperatorCatalog catalog,
        OperatorBuildContext context,
        List<EngineDiagnostic> diagnostics,
        string xpathPrefix,
        List<CompiledItemBinding> bindings,
        bool optional)
    {
        var xpath = $"{xpathPrefix}/{complex.Name}";

        if (complex.Cardinality.IsRepeatable)
        {
            CompileCollectionItem(complex, collectionsByName, catalog, context, diagnostics, xpath, bindings, optional);
            return;
        }

        if (!complexByName.TryGetValue(complex.Name, out var complexMapping))
        {
            if (complex.Cardinality.IsRequired && !optional)
            {
                diagnostics.Add(EngineDiagnostic.Critical(
                    EngineDiagnosticCodes.MappingMissingRequired,
                    $"Elemento complexo obrigatório '{complex.Name}' não tem mapeamento.",
                    destinationXPath: xpath,
                    suggestion: $"Mapeie os elementos dentro de '{complex.Name}'."));
            }

            return;
        }

        RequireGenerableContent(complex.Name, complex.Content);
        var content = CompileParticle(complex.Content, complexMapping.Content, catalog, context, diagnostics, xpath, optional: false);
        bindings.Add(new CompiledComplexBinding { Name = complex.Name, SchemaItem = complex, Content = content });
    }

    private static void CompileCollectionItem(
        ComplexElementItem complex,
        IReadOnlyDictionary<string, CollectionMapping> collectionsByName,
        RuleOperatorCatalog catalog,
        OperatorBuildContext context,
        List<EngineDiagnostic> diagnostics,
        string xpath,
        List<CompiledItemBinding> bindings,
        bool optional)
    {
        if (!collectionsByName.TryGetValue(complex.Name, out var collectionMapping))
        {
            if (complex.Cardinality.MinOccurs > 0 && !optional)
            {
                diagnostics.Add(EngineDiagnostic.Critical(
                    "MAPPING_MISSING_REQUIRED_COLLECTION",
                    $"Coleção obrigatória '{complex.Name}' (minOccurs={complex.Cardinality.MinOccurs}) não tem mapeamento.",
                    destinationXPath: xpath,
                    suggestion: $"Crie o bloco de repetição de '{complex.Name}' e mapeie os elementos de cada item."));
            }

            return;
        }

        RequireGenerableContent(complex.Name, complex.Content);
        var itemContent = CompileParticle(complex.Content, collectionMapping.ItemContent, catalog, context, diagnostics, xpath, optional: false);
        var resolveCollection = CompileValueExpression(new FieldReferenceNode(collectionMapping.SourceCollectionPath), catalog, context);
        var filter = collectionMapping.Filter is null ? null : CompileValueExpression(collectionMapping.Filter, catalog, context);
        var orderByKey = collectionMapping.OrderByKey is null ? null : CompileValueExpression(collectionMapping.OrderByKey, catalog, context);

        bindings.Add(new CompiledCollectionBinding
        {
            Name = complex.Name,
            SchemaItem = complex,
            ResolveCollection = resolveCollection,
            SingleObjectAsItem = collectionMapping.SingleObjectAsItem,
            Filter = filter,
            OrderByKey = orderByKey,
            ItemContent = itemContent,
        });
    }

    private static void RequireGenerableContent(string elementName, SchemaParticle content)
    {
        if (ParticleRules.ContainsOpaque(content))
        {
            throw new NotSupportedException(
                $"Elemento '{elementName}' tem conteúdo opaco (xs:any, conteúdo simples com atributos ou vazio) que o motor " +
                "não gera. Não mapeie este elemento; deixá-lo sem mapeamento produz um diagnóstico claro em vez de um XML incorreto.");
        }
    }

    private static string Describe(RuleNode node) => node switch
    {
        FieldReferenceNode { Scope: FieldScope.Root } field => $"(raiz) {field.Path}",
        FieldReferenceNode field => field.Path,
        LiteralNode literal => $"(constante: {literal.Value ?? "null"})",
        OperatorNode op => $"({op.OperatorName}(...))",
        _ => node.GetType().Name,
    };

    private static Func<SourceDataTree, object?> CompileValueExpression(
        RuleNode node, RuleOperatorCatalog catalog, OperatorBuildContext context)
    {
        var treeParam = Expression.Parameter(typeof(SourceDataTree), "tree");
        var body = BuildExpression(node, treeParam, catalog, context);
        var compiled = Expression.Lambda<Func<SourceDataTree, object?>>(body, treeParam).Compile();

        return Guarded(compiled);
    }

    // Rede de segurança: qualquer falha de .NET dentro de um operador
    // (formato inválido em formatDate/formatNumber, estouro, conversão)
    // vira RuleOperatorException — o executor transforma em diagnóstico
    // crítico em vez de derrubar a geração (HTTP 500 na API).
    private static Func<SourceDataTree, object?> Guarded(Func<SourceDataTree, object?> rule) => tree =>
    {
        try
        {
            return rule(tree);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or InvalidCastException
                                       or ArgumentException or IndexOutOfRangeException or InvalidOperationException)
        {
            throw new RuleOperatorException("RULE_EXECUTION_FAILED", $"Falha ao avaliar a regra: {ex.Message}");
        }
    };

    private static Expression BuildExpression(
        RuleNode node, ParameterExpression treeParam, RuleOperatorCatalog catalog, OperatorBuildContext context) => node switch
    {
        FieldReferenceNode fieldRef => BuildFieldReference(fieldRef, treeParam),
        LiteralNode literal => Expression.Constant(literal.Value, typeof(object)),
        OperatorNode op => BuildOperatorCall(op, treeParam, catalog, context),
        _ => throw new NotSupportedException($"Tipo de nó de regra '{node.GetType().Name}' não suportado."),
    };

    private static Expression BuildOperatorCall(
        OperatorNode op, ParameterExpression treeParam, RuleOperatorCatalog catalog, OperatorBuildContext context)
    {
        var definition = catalog.Resolve(op.OperatorName);
        if (op.Arguments.Count < definition.MinArguments || op.Arguments.Count > definition.MaxArguments)
        {
            throw new NotSupportedException(
                $"Operador '{op.OperatorName}' espera entre {definition.MinArguments} e {definition.MaxArguments} " +
                $"argumento(s), recebeu {op.Arguments.Count}.");
        }

        var compiledArguments = op.Arguments.Select(a => BuildExpression(a, treeParam, catalog, context)).ToList();
        return definition.Build(op.Arguments, compiledArguments, context);
    }

    private static Expression BuildFieldReference(FieldReferenceNode reference, ParameterExpression treeParam)
    {
        // Prefixo $root removido (ADR-0020): nunca vira "campo ausente" em silêncio.
        if (reference.Scope == FieldScope.Current && (reference.Path == "$root" || reference.Path.StartsWith("$root.", StringComparison.Ordinal)))
        {
            throw new NotSupportedException(
                $"Caminho '{reference.Path}': o prefixo $root não existe mais. Use a referência com escopo raiz " +
                "(no JSON, \"scope\": \"root\") e o caminho sem o prefixo.");
        }

        var pathConst = Expression.Constant(reference.Path);
        var valueVar = Expression.Variable(typeof(object), "value");
        // Escopo raiz (ADR-0020): lê de tree.Root, a raiz da entrada, mesmo dentro de um "for each".
        Expression tree = reference.Scope switch
        {
            FieldScope.Current => treeParam,
            FieldScope.Root => Expression.Property(treeParam, nameof(SourceDataTree.Root)),
            _ => throw new NotSupportedException($"Escopo de campo '{reference.Scope}' não suportado."),
        };
        var tryGetCall = Expression.Call(tree, TryGetValueMethod, pathConst, valueVar);

        // Caminho ausente e caminho presente-e-nulo colapsam no mesmo "sem
        // valor"; ver RuleExecutor para obrigatório/opcional/nillable/default.
        return Expression.Block(variables: [valueVar], expressions: [tryGetCall, valueVar]);
    }
}

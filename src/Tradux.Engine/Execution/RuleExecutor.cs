using System.Collections;
using Tradux.Domain.Schema;
using Tradux.Domain.SourceData;
using Tradux.Engine.Compilation;
using Tradux.Engine.Diagnostics;
using Tradux.Engine.Operators;

namespace Tradux.Engine.Execution;

/// <summary>
/// Executor de regras (docs/architecture/COMPONENTS.md): aplica, para um
/// dado de entrada específico, um <see cref="CompiledTemplate"/> já
/// compilado — percorrendo a árvore compilada (não a árvore de configuração
/// original) e chamando os delegates já prontos. Erros de operador
/// (<see cref="RuleOperatorException"/>) viram diagnóstico crítico com
/// caminho de destino/origem, em vez de derrubar a geração inteira.
/// </summary>
public static class RuleExecutor
{
    public static RuleExecutionResult Execute(CompiledTemplate template, SourceDataTree sourceData)
    {
        var diagnostics = new List<EngineDiagnostic>(template.CompileTimeDiagnostics);
        var items = ExecuteParticle(template.RootContent, sourceData, diagnostics, "/" + template.Xsd.Schema.RootElementName);
        return new RuleExecutionResult { Items = items, Diagnostics = diagnostics };
    }

    /// <summary>
    /// Destinos escalares (caminho do template, sem índices) que geram conteúdo
    /// sem dado algum de entrada: constante, <c>coalesce</c>/padrão com literal,
    /// valor padrão do schema, <c>xsi:nil</c> obrigatório. Usa a mesma regra da
    /// geração (ADR-0020): a suíte estrutural sabe, sem estimar, que o ramo de
    /// choice que os contém sempre aparece.
    /// </summary>
    public static IReadOnlyCollection<string> ContentWithoutData(CompiledTemplate template)
    {
        var empty = new SourceDataTree(new Dictionary<string, object?>(StringComparer.Ordinal));
        var paths = new HashSet<string>(StringComparer.Ordinal);
        Walk(template.RootContent, "/" + template.Xsd.Schema.RootElementName);
        return paths;

        void Walk(CompiledParticle particle, string prefix)
        {
            foreach (var binding in particle.Items)
            {
                switch (binding)
                {
                    case CompiledScalarBinding scalar:
                        try
                        {
                            if (HasContent(ExecuteScalar(scalar, empty, [], prefix)))
                            {
                                paths.Add($"{prefix}/{scalar.Name}");
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            // Regra que falha sem dado não produz conteúdo sem dado.
                        }

                        break;
                    case CompiledComplexBinding complex:
                        Walk(complex.Content, $"{prefix}/{complex.Name}");
                        break;
                    case CompiledCollectionBinding collection:
                        Walk(collection.ItemContent, $"{prefix}/{collection.Name}");
                        break;
                    case CompiledGroupBinding group:
                        Walk(group.Group, prefix);
                        break;
                }
            }
        }
    }

    private static List<ResolvedItem> ExecuteParticle(
        CompiledParticle particle, SourceDataTree scope, List<EngineDiagnostic> diagnostics, string xpathPrefix)
    {
        if (particle.Kind == Xsd.ParticleKind.Choice)
        {
            return ExecuteChoice(particle, scope, diagnostics, xpathPrefix);
        }

        var result = new List<ResolvedItem>(particle.Items.Count);
        foreach (var binding in particle.Items)
        {
            result.Add(ExecuteBinding(binding, scope, diagnostics, xpathPrefix));
        }

        return result;
    }

    private static ResolvedItem ExecuteBinding(CompiledItemBinding binding, SourceDataTree scope, List<EngineDiagnostic> diagnostics, string xpathPrefix) => binding switch
    {
        CompiledScalarBinding scalar => ExecuteScalar(scalar, scope, diagnostics, xpathPrefix),
        CompiledComplexBinding { SchemaItem.Cardinality.IsRequired: false } optional => ExecuteOptionalComplex(optional, scope, diagnostics, xpathPrefix),
        CompiledComplexBinding complex => ExecuteComplex(complex, scope, diagnostics, xpathPrefix),
        CompiledCollectionBinding collection => ExecuteCollection(collection, scope, diagnostics, xpathPrefix),
        CompiledGroupBinding group => new ResolvedGroup { Name = group.Name, Items = ExecuteParticle(group.Group, scope, diagnostics, xpathPrefix) },
        _ => throw new NotSupportedException($"Binding compilado '{binding.GetType().Name}' não suportado."),
    };

    /// <summary>
    /// xs:choice (ADR-0018): vale o ramo que tem conteúdo para este dado.
    /// - Choice único: exatamente um ramo com conteúdo; mais de um vira erro
    ///   (use condição no mapeamento para escolher).
    /// - Choice repetível: todos os ramos com conteúdo vão para o XML, cada
    ///   um contando como uma ocorrência, dentro de minOccurs/maxOccurs.
    /// Ramo sem conteúdo é descartado com os diagnósticos dele: dado ausente
    /// num ramo não escolhido não é erro.
    /// </summary>
    private static List<ResolvedItem> ExecuteChoice(
        CompiledParticle particle, SourceDataTree scope, List<EngineDiagnostic> diagnostics, string xpathPrefix)
    {
        var result = new List<ResolvedItem>(particle.Items.Count);
        var chosen = new List<string>();
        var occurrences = 0;
        foreach (var binding in particle.Items)
        {
            var local = new List<EngineDiagnostic>();
            var resolved = ExecuteBinding(binding, scope, local, xpathPrefix);
            if (HasContent(resolved))
            {
                diagnostics.AddRange(local);
                result.Add(resolved);
                chosen.Add(binding.Name);
                occurrences += resolved is ResolvedCollection collection ? collection.Items.Count : 1;
            }
            else
            {
                // Erro de regra (operador) continua valendo mesmo em ramo vazio.
                diagnostics.AddRange(local.Where(d => !EngineDiagnosticCodes.IsMissingData(d.Code)));
            }
        }

        var cardinality = particle.Cardinality;
        if (!cardinality.IsRepeatable && chosen.Count > 1)
        {
            diagnostics.Add(EngineDiagnostic.Critical(
                "CHOICE_MULTIPLE_BRANCHES",
                $"O xs:choice em '{xpathPrefix}' aceita um ramo só, mas {chosen.Count} têm conteúdo: {string.Join(", ", chosen)}.",
                xpathPrefix,
                suggestion: "Use condição no mapeamento para que apenas um ramo produza conteúdo."));
        }

        if (occurrences < cardinality.MinOccurs)
        {
            diagnostics.Add(EngineDiagnostic.Critical(
                EngineDiagnosticCodes.ChoiceNoBranch,
                $"O xs:choice em '{xpathPrefix}' exige {cardinality.MinOccurs} ocorrência(s) e só {occurrences} ramo(s) têm conteúdo para este dado.",
                xpathPrefix,
                suggestion: "Verifique o dado de entrada dos ramos mapeados."));
        }

        if (cardinality.MaxOccurs is { } max && cardinality.IsRepeatable && occurrences > max)
        {
            diagnostics.Add(EngineDiagnostic.Critical(
                "CHOICE_TOO_MANY",
                $"O xs:choice em '{xpathPrefix}' aceita até {max} ocorrência(s); o dado produziu {occurrences}.",
                xpathPrefix));
        }

        return result;
    }

    private static bool HasContent(ResolvedItem item) => item switch
    {
        ResolvedScalar scalar => scalar.Kind != ResolvedValueKind.Omitted,
        ResolvedComplex complex => complex.Kind != ResolvedValueKind.Omitted && (complex.Children ?? []).Any(HasContent),
        ResolvedCollection collection => collection.Items.Count > 0,
        ResolvedGroup group => group.Items.Any(HasContent),
        _ => false,
    };

    private static ResolvedScalar ExecuteScalar(
        CompiledScalarBinding binding, SourceDataTree scope, List<EngineDiagnostic> diagnostics, string xpathPrefix)
    {
        var element = binding.Element;
        var xpath = $"{xpathPrefix}/{element.Name}";

        object? rawValue;
        try
        {
            rawValue = binding.Resolve(scope);
        }
        catch (RuleOperatorException ex)
        {
            diagnostics.Add(EngineDiagnostic.Critical(ex.Code, ex.Message, xpath, binding.SourceDescription));
            return new ResolvedScalar { Name = element.Name, Element = element, Kind = ResolvedValueKind.Omitted };
        }

        if (ReferenceEquals(rawValue, OmitMarker.Instance))
        {
            return new ResolvedScalar { Name = element.Name, Element = element, Kind = ResolvedValueKind.Omitted };
        }

        if (rawValue is not null)
        {
            return new ResolvedScalar { Name = element.Name, Element = element, Kind = ResolvedValueKind.Present, Value = rawValue };
        }

        // Sem valor (caminho ausente, valor nulo, ou operador setNull).
        if (!element.Cardinality.IsRequired)
        {
            return new ResolvedScalar { Name = element.Name, Element = element, Kind = ResolvedValueKind.Omitted };
        }

        if (element.DefaultValue is not null)
        {
            diagnostics.Add(EngineDiagnostic.Info(
                "DEFAULT_APPLIED",
                $"Elemento '{element.Name}' sem valor de origem; valor padrão do schema aplicado.",
                xpath,
                binding.SourceDescription));
            return new ResolvedScalar { Name = element.Name, Element = element, Kind = ResolvedValueKind.Present, Value = element.DefaultValue };
        }

        if (element.Nillability == Nillability.Nillable)
        {
            return new ResolvedScalar { Name = element.Name, Element = element, Kind = ResolvedValueKind.ExplicitNil };
        }

        diagnostics.Add(EngineDiagnostic.Critical(
            EngineDiagnosticCodes.RequiredValueMissing,
            $"Elemento obrigatório '{element.Name}' não recebeu valor e não aceita nulo (nillable=false).",
            xpath,
            binding.SourceDescription,
            "Forneça um valor não nulo no dado de origem."));
        return new ResolvedScalar { Name = element.Name, Element = element, Kind = ResolvedValueKind.Omitted };
    }

    /// <summary>
    /// Elemento complexo opcional (<c>minOccurs="0"</c>): sai só com conteúdo, como um ramo
    /// de xs:choice. Sem conteúdo, não sai, e os diagnósticos de dado ausente dele são descartados
    /// (ex.: ImovelIBSCBS só quando o indicador de operação exige); erro de regra continua valendo.
    /// </summary>
    private static ResolvedComplex ExecuteOptionalComplex(
        CompiledComplexBinding binding, SourceDataTree scope, List<EngineDiagnostic> diagnostics, string xpathPrefix)
    {
        var local = new List<EngineDiagnostic>();
        var resolved = ExecuteComplex(binding, scope, local, xpathPrefix);
        if (HasContent(resolved))
        {
            diagnostics.AddRange(local);
            return resolved;
        }

        diagnostics.AddRange(local.Where(d => !EngineDiagnosticCodes.IsMissingData(d.Code)));
        return new ResolvedComplex { Name = binding.Name, Kind = ResolvedValueKind.Omitted };
    }

    private static ResolvedComplex ExecuteComplex(
        CompiledComplexBinding binding, SourceDataTree scope, List<EngineDiagnostic> diagnostics, string xpathPrefix)
    {
        var xpath = $"{xpathPrefix}/{binding.Name}";
        var children = ExecuteParticle(binding.Content, scope, diagnostics, xpath);
        return new ResolvedComplex { Name = binding.Name, Kind = ResolvedValueKind.Present, Children = children };
    }

    private static ResolvedCollection ExecuteCollection(
        CompiledCollectionBinding binding, SourceDataTree scope, List<EngineDiagnostic> diagnostics, string xpathPrefix)
    {
        var xpath = $"{xpathPrefix}/{binding.Name}";
        object? rawCollection;
        try
        {
            rawCollection = binding.ResolveCollection(scope);
        }
        catch (RuleOperatorException ex)
        {
            diagnostics.Add(EngineDiagnostic.Critical(ex.Code, ex.Message, xpath));
            return new ResolvedCollection { Name = binding.Name, Items = [] };
        }

        var sourceItems = MaterializeCollection(rawCollection, binding, xpath, diagnostics);

        // A raiz da entrada continua alcançável dentro do item (FieldScope.Root, ADR-0020).
        var itemTrees = sourceItems
            .Select((item, index) => SourceDataTree.FromItem(item, new Dictionary<string, object?> { ["$index"] = (decimal)index }, scope.Root))
            .ToList();

        if (binding.Filter is { } filter)
        {
            itemTrees = itemTrees.Where(tree => EvaluateFilter(filter, tree, xpath, diagnostics)).ToList();
        }

        if (binding.OrderByKey is { } orderByKey)
        {
            try
            {
                itemTrees = itemTrees.OrderBy(tree => orderByKey(tree) as IComparable).ToList();
            }
            catch (Exception ex) when (ex is RuleOperatorException or InvalidOperationException or ArgumentException)
            {
                // Chave de ordenação com erro ou tipos não comparáveis entre si.
                var code = ex is RuleOperatorException rule ? rule.Code : "COLLECTION_ORDER_FAILED";
                diagnostics.Add(EngineDiagnostic.Critical(code, $"Não foi possível ordenar a coleção '{binding.Name}': {ex.Message}", xpath));
            }
        }

        ValidateCardinality(binding, itemTrees.Count, xpath, diagnostics);

        var resolvedItems = itemTrees
            .Select((itemTree, index) => (IReadOnlyList<ResolvedItem>)ExecuteParticle(binding.ItemContent, itemTree, diagnostics, $"{xpath}[{index}]"))
            .ToList();

        return new ResolvedCollection { Name = binding.Name, Items = resolvedItems };
    }

    private static List<object> MaterializeCollection(
        object? rawCollection, CompiledCollectionBinding binding, string xpath, List<EngineDiagnostic> diagnostics)
    {
        var name = binding.Name;
        var items = new List<object>();

        switch (rawCollection)
        {
            case null:
                break;
            case string:
                diagnostics.Add(EngineDiagnostic.Critical(
                    EngineDiagnosticCodes.CollectionSourceNotEnumerable,
                    $"O caminho de origem da coleção '{name}' resolveu para texto, não para uma coleção.",
                    xpath));
                break;
            case IReadOnlyDictionary<string, object?> single when binding.SingleObjectAsItem:
                // Opção explícita do mapeamento (ADR-0020): o objeto é 1 item.
                items.Add(single);
                break;
            case IReadOnlyDictionary<string, object?>:
                // Nunca percorre os pares chave/valor, e nunca vira 1 item em silêncio.
                diagnostics.Add(EngineDiagnostic.Critical(
                    EngineDiagnosticCodes.CollectionSourceNotEnumerable,
                    $"O caminho de origem da coleção '{name}' resolveu para um objeto, não para uma lista.",
                    xpath,
                    suggestion: "Se a entrada traz um objeto único (ex.: um documento), marque no bloco de repetição que o objeto vale como 1 item; senão, corrija o caminho."));
                break;
            case IEnumerable enumerable:
                foreach (var item in enumerable)
                {
                    if (item is not null)
                    {
                        items.Add(item);
                    }
                }

                break;
            default:
                diagnostics.Add(EngineDiagnostic.Critical(
                    EngineDiagnosticCodes.CollectionSourceNotEnumerable,
                    $"O caminho de origem da coleção '{name}' não é uma lista.",
                    xpath));
                break;
        }

        return items;
    }

    private static bool EvaluateFilter(
        Func<SourceDataTree, object?> filter, SourceDataTree itemTree, string xpath, List<EngineDiagnostic> diagnostics)
    {
        try
        {
            return ScalarValues.ToBool(filter(itemTree));
        }
        catch (RuleOperatorException ex)
        {
            diagnostics.Add(EngineDiagnostic.Critical(ex.Code, ex.Message, xpath));
            return false;
        }
    }

    private static void ValidateCardinality(
        CompiledCollectionBinding binding, int count, string xpath, List<EngineDiagnostic> diagnostics)
    {
        var cardinality = binding.SchemaItem.Cardinality;
        if (count < cardinality.MinOccurs)
        {
            diagnostics.Add(EngineDiagnostic.Critical(
                EngineDiagnosticCodes.CollectionTooFewItems,
                $"Coleção '{binding.Name}' tem {count} item(ns), abaixo do mínimo exigido pelo schema ({cardinality.MinOccurs}).",
                xpath));
        }

        if (cardinality.MaxOccurs is { } max && count > max)
        {
            diagnostics.Add(EngineDiagnostic.Critical(
                "COLLECTION_TOO_MANY_ITEMS",
                $"Coleção '{binding.Name}' tem {count} item(ns), acima do máximo permitido pelo schema ({max}).",
                xpath));
        }
    }
}

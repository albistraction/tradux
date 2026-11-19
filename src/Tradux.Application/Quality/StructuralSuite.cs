using System.Text.Json;
using System.Text.Json.Nodes;
using Tradux.Contracts.Quality;
using Tradux.Domain.SourceData;
using Tradux.Engine;
using Tradux.Engine.Compilation;
using Tradux.Engine.Diagnostics;

namespace Tradux.Application.Quality;

/// <summary>Um caso da suíte estrutural. <see cref="Data"/> nunca sai da suíte (só vai para o motor).</summary>
public sealed record StructuralCase(string Name, StructuralCategory Category, bool ExpectValid, string? Target, JsonElement Data);

/// <summary>
/// Suíte planejada: casos a executar, lacunas de cobertura e campos que
/// alimentam destino obrigatório sem valor válido gerável — cada um vira
/// caso reprovado, nunca aprovação silenciosa (revisão da Fase 8, B2).
/// </summary>
/// <param name="Cases">Casos a executar.</param>
/// <param name="NotCovered">Lacunas de cobertura.</param>
/// <param name="UnavailableRequired">Campos que alimentam obrigatório sem valor gerável.</param>
/// <param name="UnreachableBranches">
/// Ramos de choice único que nunca saem sozinhos: outro ramo do mesmo choice sempre
/// aparece (campo usado também fora do choice, constante). Toda entrada com eles
/// reprova no motor — cada um vira caso reprovado (ADR-0020).
/// </param>
/// <param name="UnavailableDestinations">
/// Destinos alimentados pelos campos sem valor: erro ali já está reportado
/// por <paramref name="UnavailableRequired"/> e não mascara os demais
/// defeitos dos casos positivos.
/// </param>
public sealed record SuitePlan(
    IReadOnlyList<StructuralCase> Cases,
    IReadOnlyList<CoverageGap> NotCovered,
    IReadOnlyList<string> UnavailableRequired,
    IReadOnlyCollection<string>? UnavailableDestinations = null,
    IReadOnlyList<string>? UnreachableBranches = null);

/// <summary>
/// Gera a suíte estrutural determinística a partir do perfil (ADR-0011 §3):
/// conjunto fixo de casos, cada valor de enumeração/limite coberto ao menos
/// uma vez, um inválido por restrição crítica — sem produto cartesiano.
/// </summary>
public static class StructuralSuiteBuilder
{
    public const int MaxEnumerationCases = 16;
    public const int MaxInvalidCases = 200;
    private const int ManyItems = 3;

    /// <summary>
    /// Orçamento de objetos sintéticos por caso (revisão pós-Fase 14, rodada 4):
    /// coleções aninhadas com 3 itens cada crescem como 3^profundidade. Depois do
    /// orçamento brando, cada coleção recebe só o mínimo exigido; depois do
    /// rígido, nenhum item a mais. O que deixou de ser exercitado vira lacuna.
    /// </summary>
    public const int SoftNodesPerCase = 20_000;

    public const int HardNodesPerCase = 100_000;

    /// <summary>Nós somados de todos os casos: acima disso, os casos seguintes saem só com o mínimo.</summary>
    public const int NodesPerSuite = 1_000_000;

    /// <summary>Tempo máximo de execução da suíte; o que não rodar conta como reprovado.</summary>
    public static readonly TimeSpan MaxRunTime = TimeSpan.FromSeconds(15);

    public const string SuiteTimeLimitCode = "STRUCTURAL_SUITE_TIME_LIMIT";

    private static readonly JsonSerializerOptions DeepJson = new() { MaxDepth = 1_024 };

    /// <param name="profile">Perfil de origem inferido do template.</param>
    /// <param name="lookupTables">Tabelas DE-PARA do template.</param>
    /// <param name="seeds">Dados dos cenários funcionais do autor: fonte de valor quando o gerador não consegue um válido.</param>
    public static SuitePlan Build(
        SourceProfile profile,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> lookupTables,
        IReadOnlyList<JsonElement>? seeds = null)
    {
        var gaps = profile.NotCovered.Select(n => new CoverageGap(n.Area, n.Path, n.Reason)).ToList();
        var values = new Dictionary<SourceField, FieldValues>(ReferenceEqualityComparer.Instance);
        var unavailable = new List<string>();
        var unavailableDestinations = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (field, scopePath, chain) in AllFields(profile.Root, "", []))
        {
            var fieldValues = SyntheticValues.For(field, lookupTables, Seed(seeds, chain, field.Path));
            values[field] = fieldValues;
            foreach (var note in fieldValues.Notes)
            {
                gaps.Add(new CoverageGap("valor", scopePath + field.Path, note));
            }

            // Campo sem valor fica fora dos casos; se alimenta obrigatório, a
            // suíte não pode passar sem ele.
            if (fieldValues.Typical is null && field.FeedsRequired)
            {
                unavailable.Add(scopePath + field.Path);
                unavailableDestinations.UnionWith(field.Direct.Select(d => d.DestinationPath));
            }
        }

        var builder = new Builder(values);
        var branches = new BranchPlan(profile);
        var selection = branches.Default;
        gaps.AddRange(branches.Gaps);
        var cases = new List<StructuralCase>
        {
            builder.Case("obrigatorios", StructuralCategory.Required, profile.Root, new Mode(ValueMode.Typical, selection, MinimalOnly: true)),
            builder.Case("completo", StructuralCategory.Complete, profile.Root, new Mode(ValueMode.Typical, selection)),
            builder.Case("limite-minimo", StructuralCategory.LowerBound, profile.Root, new Mode(ValueMode.Lower, selection)),
            builder.Case("limite-maximo", StructuralCategory.UpperBound, profile.Root, new Mode(ValueMode.Upper, selection)),
        };

        // Cada ramo mapeado de cada choice, sozinho (ADR-0020): sem produto cartesiano entre choices.
        var unreachable = new List<string>();
        foreach (var (choice, branch) in branches.All)
        {
            var target = $"{choice.Path}/{choice.Branches[branch]}";
            var branchSelection = branches.With(choice, branch);
            if (BranchPlan.Unreachable(branchSelection))
            {
                unreachable.Add(target); // reprovado com motivo próprio, sem rodar um caso que só repetiria o erro
                continue;
            }

            cases.Add(builder.Case($"ramo:{target}", StructuralCategory.Choice, profile.Root, new Mode(ValueMode.Typical, branchSelection), target: target));
        }

        var enumerationCount = Math.Min(values.Values.Select(v => v.Enumeration.Count).DefaultIfEmpty(0).Max(), MaxEnumerationCases);
        for (var k = 1; k < enumerationCount; k++)
        {
            cases.Add(builder.Case($"enumeracao-{k + 1}", StructuralCategory.Enumeration, profile.Root, new Mode(ValueMode.Enumeration, selection, EnumerationIndex: k)));
        }

        if (enumerationCount < values.Values.Select(v => v.Enumeration.Count).DefaultIfEmpty(0).Max())
        {
            gaps.Add(new CoverageGap("enumeration", "/", $"Enumerações com mais de {MaxEnumerationCases} valores: só os {MaxEnumerationCases} primeiros são exercitados."));
        }

        var collections = AllCollections(profile.Root).ToList();
        if (collections.Any(c => c.MinOccurs == 0))
        {
            cases.Add(builder.Case("repeticao-vazia", StructuralCategory.Repetition, profile.Root, new Mode(ValueMode.Typical, selection, Items: 0)));
        }

        if (collections.Count > 0)
        {
            cases.Add(builder.Case("repeticao-varios", StructuralCategory.Repetition, profile.Root, new Mode(ValueMode.Typical, selection, Items: ManyItems)));
        }

        if (values.Keys.Any(IsNullable))
        {
            cases.Add(builder.Case("nulos", StructuralCategory.Null, profile.Root, new Mode(ValueMode.Typical, selection, NullsWherePermitted: true)));
        }

        var invalid = 0;
        foreach (var (field, scopePath, _) in AllFields(profile.Root, "", []))
        {
            // Campo de ramo inalcançável: o caso "ramo:" já reprova com o motivo.
            if (BranchPlan.Unreachable(branches.For(field)))
            {
                continue;
            }

            foreach (var variant in values[field].Invalid)
            {
                if (variant.Value.Remove || variant.Value.Value is null)
                {
                    // Ausente/nulo: só é inválido se não houver outro ramo do choice no caso.
                    if (branches.AbsenceAbsorbed(field))
                    {
                        continue;
                    }
                }

                if (++invalid > MaxInvalidCases)
                {
                    gaps.Add(new CoverageGap("invalid", "/", $"Mais de {MaxInvalidCases} restrições: casos inválidos excedentes não gerados."));
                    return new SuitePlan(cases, WithTruncation(gaps, builder), unavailable, unavailableDestinations, unreachable);
                }

                // Só o ramo do campo em cada choice que o envolve: sem ele o choice fica vazio,
                // em vez de outro ramo "absorver" a falta.
                var target = $"{scopePath}{field.Path}";
                cases.Add(builder.Case($"invalido:{variant.Restriction}:{target}", StructuralCategory.Invalid, profile.Root,
                    new Mode(ValueMode.Typical, branches.For(field), Mutation: (field, variant.Value)), expectValid: false, target: $"{target} ({variant.Restriction})"));
            }
        }

        return new SuitePlan(cases, WithTruncation(gaps, builder), unavailable, unavailableDestinations, unreachable);
    }

    private static List<CoverageGap> WithTruncation(List<CoverageGap> gaps, Builder builder)
    {
        if (builder.Truncated)
        {
            gaps.Add(new CoverageGap("repetition", "/",
                $"Dados sintéticos grandes demais para casos completos (mais de {SoftNodesPerCase} objetos e valores por caso, ou {NodesPerSuite} na suíte): coleções foram exercitadas só com o mínimo, ou cortadas."));
        }

        return gaps;
    }

    // Valor do campo no primeiro cenário que o tenha (itens: primeiro item da coleção).
    private static JsonNode? Seed(IReadOnlyList<JsonElement>? seeds, IReadOnlyList<string> chain, string path)
    {
        foreach (var seed in seeds ?? [])
        {
            JsonNode? current = JsonNode.Parse(seed.GetRawText());
            foreach (var collection in chain)
            {
                current = Navigate(current, collection) is JsonArray { Count: > 0 } items ? items[0] : null;
            }

            if (Navigate(current, path) is { } value)
            {
                return value;
            }
        }

        return null;
    }

    private static JsonNode? Navigate(JsonNode? node, string path)
    {
        if (node is JsonObject direct && direct.TryGetPropertyValue(path, out var exact))
        {
            return exact;
        }

        foreach (var segment in path.Split('.'))
        {
            node = node is JsonObject obj && obj.TryGetPropertyValue(segment, out var next) ? next : null;
        }

        return node;
    }

    private static bool IsNullable(SourceField field) =>
        field.Constrained && field.Direct.All(d => d.Element.Nillability == Domain.Schema.Nillability.Nillable && !d.Conditional);

    private static IEnumerable<(SourceField Field, string ScopePath, IReadOnlyList<string> Chain)> AllFields(SourceScope scope, string prefix, IReadOnlyList<string> chain)
    {
        foreach (var field in scope.Fields.Values)
        {
            yield return (field, prefix, chain);
        }

        foreach (var collection in scope.Collections.Values)
        {
            foreach (var nested in AllFields(collection.Item, $"{prefix}{collection.Path}[].", [.. chain, collection.Path]))
            {
                yield return nested;
            }
        }
    }

    private static IEnumerable<SourceCollection> AllCollections(SourceScope scope) =>
        scope.Collections.Values.SelectMany(c => AllCollections(c.Item).Prepend(c));

    private enum ValueMode
    {
        Typical,
        Lower,
        Upper,
        Enumeration,
    }

    /// <summary>
    /// Ramos de choice por caso (ADR-0020). Choice que não repete: um ramo padrão
    /// (o primeiro mapeado com obrigatório, senão o primeiro mapeado), porque o motor
    /// recusa dois ramos num choice único. Choice que repete: todos os ramos juntos.
    /// </summary>
    private sealed class BranchPlan
    {
        private readonly List<SourceUsage> _usages = [];
        private readonly IReadOnlyList<BranchUse> _always;
        private readonly Dictionary<ChoiceProfile, SortedSet<int>> _mapped = [];
        private readonly Dictionary<ChoiceProfile, SortedSet<int>> _unconditional = [];
        private readonly Dictionary<ChoiceProfile, SortedSet<int>> _conditional = [];

        public BranchPlan(SourceProfile profile)
        {
            var required = new Dictionary<ChoiceProfile, SortedSet<int>>();
            _always = profile.AlwaysPresent.Where(use => !use.Conditional).ToList();
            foreach (var use in profile.AlwaysPresent)
            {
                foreach (var branch in use.Branches)
                {
                    Add(_mapped, branch);
                    Add(use.Conditional ? _conditional : _unconditional, branch);
                }
            }

            foreach (var (usage, feedsRequired) in AllUsages(profile.Root))
            {
                _usages.Add(usage);
                foreach (var use in usage.Usages)
                {
                    foreach (var branch in use.Branches)
                    {
                        Add(_mapped, branch);
                        if (use.Conditional)
                        {
                            Add(_conditional, branch);
                        }
                        else
                        {
                            Add(_unconditional, branch);
                            if (feedsRequired)
                            {
                                Add(required, branch);
                            }
                        }
                    }
                }
            }

            // Primeiro os ramos que aparecem de qualquer jeito (campo usado também fora do
            // choice, constante): eles SÃO o ramo do choice único. Só sem nenhum forçado,
            // escolhe o preferido (com obrigatório e sem condição), um choice por vez.
            var selection = profile.Choices.Where(c => !c.Repeatable && _mapped.ContainsKey(c)).ToDictionary(c => c, _ => new HashSet<int>());
            Close(selection);
            while (profile.Choices.FirstOrDefault(c => selection.TryGetValue(c, out var chosen) && chosen.Count == 0) is { } open)
            {
                var preferred = required.GetValueOrDefault(open) ?? _unconditional.GetValueOrDefault(open) ?? _mapped[open];
                selection[open].Add(preferred.Min);
                Close(selection);
            }

            Default = selection;

            var all = new List<(ChoiceProfile, int)>();
            foreach (var choice in profile.Choices.Where(c => _mapped.TryGetValue(c, out var b) && b.Count > 1))
            {
                foreach (var branch in _mapped[choice])
                {
                    if (_unconditional.GetValueOrDefault(choice)?.Contains(branch) == true)
                    {
                        all.Add((choice, branch));
                    }
                    else
                    {
                        Gaps.Add(new CoverageGap("choice", $"{choice.Path}/{choice.Branches[branch]}",
                            "Ramo só com conteúdo condicional: os dados sintéticos não escolhem o ramo; coberto pelos cenários funcionais."));
                    }
                }
            }

            All = all;
        }

        /// <summary>Seleção padrão: um ramo por choice que não repete, todos nos que repetem.</summary>
        public IReadOnlyDictionary<ChoiceProfile, HashSet<int>> Default { get; }

        /// <summary>Cada ramo, com conteúdo sem condição, de choice com mais de um ramo mapeado.</summary>
        public IReadOnlyList<(ChoiceProfile Choice, int Branch)> All { get; }

        public List<CoverageGap> Gaps { get; } = [];

        /// <summary>
        /// Choice único com mais de um ramo na seleção: algum ramo aparece de qualquer
        /// jeito junto com o pedido — o pedido é inalcançável (o motor recusaria dois ramos).
        /// </summary>
        public static bool Unreachable(IReadOnlyDictionary<ChoiceProfile, HashSet<int>> selection) =>
            selection.Any(p => !p.Key.Repeatable && p.Value.Count > 1);

        public IReadOnlyDictionary<ChoiceProfile, HashSet<int>> With(ChoiceProfile choice, int branch)
        {
            var selection = Copy(Default);
            selection[choice] = [branch];
            return Close(selection);
        }

        /// <summary>Seleção em que o campo entra: os ramos do primeiro uso dele.</summary>
        public IReadOnlyDictionary<ChoiceProfile, HashSet<int>> For(SourceUsage usage)
        {
            if (usage.Usages.FirstOrDefault().Branches is not { Count: > 0 } branches)
            {
                return Default;
            }

            var selection = Copy(Default);
            foreach (var branch in branches)
            {
                selection[branch.Choice] = [branch.Branch];
            }

            return Close(selection);
        }

        /// <summary>
        /// Sem o campo, o ramo dele fica vazio e some, mas outro ramo do mesmo choice
        /// continua no caso: ausente/nulo é entrada válida, não "inválido esperado".
        /// </summary>
        public bool AbsenceAbsorbed(SourceField field)
        {
            var selection = For(field);
            foreach (var branch in field.Usages.FirstOrDefault().Branches ?? [])
            {
                var othersInBranch = _always.Any(use => use.Branches.Contains(branch))
                    || _usages.Any(u => !ReferenceEquals(u, field) && !u.ExcludedBy(selection)
                        && u.Usages.Any(use => !use.Conditional && use.Branches.Contains(branch)));
                if (!othersInBranch && selection.TryGetValue(branch.Choice, out var chosen) && chosen.Count > 1)
                {
                    return true;
                }

                // Outro ramo com conteúdo condicional (ex.: cNaoNIF quando falta o CPF) pode ocupar
                // o choice sem este campo: ausente pode ser válido — não é inválido esperado.
                if (!othersInBranch && _conditional.GetValueOrDefault(branch.Choice)?.Any(b => b != branch.Branch) == true)
                {
                    return true;
                }
            }

            return false;
        }

        // Fechamento: um campo presente no caso preenche TODOS os ramos em que é usado sem
        // condição (ex.: campo da raiz usado fora do choice e dentro de um ramo) — esses ramos
        // aparecem de qualquer jeito, então entram na seleção com os demais campos deles.
        private Dictionary<ChoiceProfile, HashSet<int>> Close(Dictionary<ChoiceProfile, HashSet<int>> selection)
        {
            bool changed;
            do
            {
                changed = false;
                // Conteúdo sem dado (constante, padrão): o ramo aparece em todo caso.
                var present = _usages.Where(u => !u.ExcludedBy(selection)).SelectMany(u => u.Usages.Where(use => !use.Conditional)).Concat(_always).ToList();
                foreach (var branch in present.SelectMany(use => use.Branches))
                {
                    if (selection.TryGetValue(branch.Choice, out var chosen) && chosen.Add(branch.Branch))
                    {
                        changed = true;
                    }
                }
            }
            while (changed);

            return selection;
        }

        private static Dictionary<ChoiceProfile, HashSet<int>> Copy(IReadOnlyDictionary<ChoiceProfile, HashSet<int>> selection) =>
            selection.ToDictionary(p => p.Key, p => new HashSet<int>(p.Value));

        private static void Add(Dictionary<ChoiceProfile, SortedSet<int>> target, BranchRef branch)
        {
            if (!target.TryGetValue(branch.Choice, out var set))
            {
                target[branch.Choice] = set = [];
            }

            set.Add(branch.Branch);
        }

        private static IEnumerable<(SourceUsage Usage, bool FeedsRequired)> AllUsages(SourceScope scope)
        {
            foreach (var field in scope.Fields.Values)
            {
                yield return (field, field.FeedsRequired);
            }

            foreach (var collection in scope.Collections.Values)
            {
                yield return (collection, collection.FeedsRequired);
                foreach (var nested in AllUsages(collection.Item))
                {
                    yield return nested;
                }
            }
        }
    }

    private sealed record Mode(
        ValueMode Values,
        IReadOnlyDictionary<ChoiceProfile, HashSet<int>> Selection,
        bool MinimalOnly = false,
        int? Items = null,
        int EnumerationIndex = 0,
        bool NullsWherePermitted = false,
        (SourceField Field, SyntheticValue Value)? Mutation = null);

    private sealed class Builder(Dictionary<SourceField, FieldValues> values)
    {
        private int _nodes;
        private long _suiteNodes;

        public bool Truncated { get; private set; }

        public StructuralCase Case(string name, StructuralCategory category, SourceScope root, Mode mode, bool expectValid = true, string? target = null)
        {
            // Suíte já grande demais: os casos seguintes saem vazios (e reprovam, nunca aprovação silenciosa).
            _nodes = _suiteNodes > NodesPerSuite ? HardNodesPerCase : 0;
            var data = JsonSerializer.SerializeToElement(Object(root, mode), DeepJson);
            _suiteNodes += _nodes;
            return new(name, category, expectValid, target, data);
        }

        private JsonObject Object(SourceScope scope, Mode mode)
        {
            _nodes++;
            var result = new JsonObject();
            if (_nodes >= HardNodesPerCase)
            {
                Truncated = true;
                return result; // orçamento rígido: nada mais neste caso
            }

            foreach (var field in scope.Fields.Values)
            {
                if ((mode.MinimalOnly && !field.FeedsRequired) || field.ExcludedBy(mode.Selection))
                {
                    continue;
                }

                var value = Value(field, mode);
                if (mode.Mutation is { } mutation && ReferenceEquals(mutation.Field, field))
                {
                    value = mutation.Value;
                }

                if (value is { Remove: false })
                {
                    _nodes++;
                    Set(result, field.Path, value.Value?.DeepClone());
                }
            }

            foreach (var collection in scope.Collections.Values)
            {
                if (collection.ExcludedBy(mode.Selection))
                {
                    continue; // só usada em ramo de choice fora deste caso
                }

                var count = mode.MinimalOnly
                    ? Math.Min(collection.MinOccurs, ManyItems)
                    : mode.Items is { } items ? Math.Max(items, collection.MinOccurs == int.MaxValue ? 0 : Math.Min(collection.MinOccurs, ManyItems)) : 1;
                if (collection.MaxOccurs is { } max)
                {
                    count = Math.Min(count, max);
                }

                if (_nodes >= SoftNodesPerCase && count > 1)
                {
                    // Orçamento brando esgotado: só o mínimo exigido (ao menos 1, para seguir exercitando o bloco).
                    count = Math.Max(1, Math.Min(collection.MinOccurs, count));
                    Truncated = true;
                }

                if (_nodes >= HardNodesPerCase)
                {
                    count = 0;
                    Truncated = true;
                }

                if (mode.MinimalOnly && count == 0)
                {
                    continue; // coleção opcional: ausente no caso mínimo
                }

                // Coleção que aceita objeto único (ADR-0020) sobre um objeto que também tem
                // campos: exercita como objeto único, juntando os campos do item, sem apagar os irmãos.
                if (collection.SingleObjectAsItem && Get(result, collection.Path) is JsonObject existing)
                {
                    if (count > 0)
                    {
                        // Campos já presentes prevalecem (inclusive mutação do escopo de fora);
                        // o campo que o caso altera dentro do item prevalece sobre eles.
                        var mutated = mode.Mutation is { } m && collection.Item.Fields.Values.Contains(m.Field) ? m.Field.Path : null;
                        // Campo do escopo de fora que o caso alterou dentro deste objeto (ex.: removido): o item não o devolve.
                        var outer = mode.Mutation is { } o && mutated is null && o.Field.Path.StartsWith(collection.Path + ".", StringComparison.Ordinal)
                            ? o.Field.Path[(collection.Path.Length + 1)..].Split('.')[0]
                            : null;
                        var item = Object(collection.Item, mode);
                        foreach (var (key, value) in item.ToList())
                        {
                            if (key == outer)
                            {
                                continue;
                            }

                            if (!existing.ContainsKey(key) || (mutated is not null && mutated.Split('.')[0] == key))
                            {
                                existing[key] = value?.DeepClone();
                            }
                        }

                        if (mutated is not null && Get(item, mutated) is null)
                        {
                            Remove(existing, mutated);
                        }
                    }

                    continue;
                }

                // Aceita objeto único: um item sai como objeto (a forma de um XML de origem),
                // vários como lista — as duas formas são exercitadas.
                if (collection.SingleObjectAsItem && count == 1)
                {
                    Set(result, collection.Path, Object(collection.Item, mode));
                    continue;
                }

                var array = new JsonArray();
                for (var i = 0; i < count; i++)
                {
                    array.Add(Object(collection.Item, mode));
                }

                Set(result, collection.Path, array);
            }

            return result;
        }

        private SyntheticValue? Value(SourceField field, Mode mode)
        {
            var fieldValues = values[field];
            if (mode.NullsWherePermitted && IsNullable(field))
            {
                return SyntheticValue.Null;
            }

            return mode.Values switch
            {
                ValueMode.Lower => fieldValues.Lower,
                ValueMode.Upper => fieldValues.Upper,
                ValueMode.Enumeration when fieldValues.Enumeration.Count > 0 => fieldValues.Enumeration[mode.EnumerationIndex % fieldValues.Enumeration.Count],
                _ => fieldValues.Typical,
            };
        }

        private static void Remove(JsonObject target, string path)
        {
            var segments = path.Split('.');
            var parent = segments.Length == 1 ? target : Get(target, string.Join('.', segments.Take(segments.Length - 1))) as JsonObject;
            parent?.Remove(segments[^1]);
        }

        private static JsonNode? Get(JsonObject target, string path)
        {
            JsonNode? current = target;
            foreach (var segment in path.Split('.'))
            {
                current = current is JsonObject obj && obj.TryGetPropertyValue(segment, out var next) ? next : null;
            }

            return current;
        }

        // Caminho com ponto vira objeto aninhado (mesma navegação do SourceDataTree).
        private static void Set(JsonObject target, string path, JsonNode? value)
        {
            var segments = path.Split('.');
            var current = target;
            foreach (var segment in segments.Take(segments.Length - 1))
            {
                if (current[segment] is not JsonObject next)
                {
                    current[segment] = next = new JsonObject();
                }

                current = next;
            }

            current[segments[^1]] = value;
        }
    }
}

/// <summary>Executa a suíte contra o template compilado e produz o relatório (sem dados nem XML).</summary>
public static class StructuralTestRunner
{
    public const string InvalidAcceptedCode = "INVALID_CASE_ACCEPTED";
    public const string ValueUnavailableCode = "SYNTHETIC_VALUE_UNAVAILABLE";
    public const string ChoiceBranchUnreachableCode = "CHOICE_BRANCH_UNREACHABLE";

    public static QualityReport Run(CompiledTemplate template, SuitePlan plan)
    {
        var results = new List<StructuralCaseResult>(plan.Cases.Count + plan.UnavailableRequired.Count);
        foreach (var field in plan.UnavailableRequired)
        {
            results.Add(new StructuralCaseResult($"valor-indisponivel:{field}", StructuralCategory.Required, true, false, field, ValueUnavailableCode));
        }

        foreach (var branch in plan.UnreachableBranches ?? [])
        {
            results.Add(new StructuralCaseResult($"ramo:{branch}", StructuralCategory.Choice, true, false, branch, ChoiceBranchUnreachableCode));
        }

        using var deadline = Engine.Validation.GenerationDeadline.Start(StructuralSuiteBuilder.MaxRunTime);
        foreach (var testCase in plan.Cases)
        {
            if (Engine.Validation.GenerationDeadline.Expired)
            {
                // Orçamento de tempo da suíte: o que não rodou não passa (nunca aprovação silenciosa).
                results.Add(new StructuralCaseResult(testCase.Name, testCase.Category, testCase.ExpectValid, false, testCase.Target, StructuralSuiteBuilder.SuiteTimeLimitCode));
                continue;
            }

            var outcome = XmlGenerationEngine.Generate(template, SourceDataTree.FromJson(testCase.Data));
            if (testCase.ExpectValid)
            {
                var criticals = outcome.Success ? [] : outcome.Diagnostics.Where(d => d.Severity == EngineDiagnosticSeverity.Critical).ToList();
                var failure = criticals.FirstOrDefault(d => !IsUnavailable(d.DestinationXPath, plan.UnavailableDestinations));
                var passed = outcome.Success || (criticals.Count > 0 && failure is null);
                results.Add(new StructuralCaseResult(testCase.Name, testCase.Category, true, passed, testCase.Target,
                    passed ? null : failure?.Code ?? "GENERATION_FAILED", passed ? null : failure?.DestinationXPath));
            }
            else
            {
                results.Add(new StructuralCaseResult(testCase.Name, testCase.Category, false, !outcome.Success, testCase.Target,
                    outcome.Success ? InvalidAcceptedCode : null));
            }
        }

        return new QualityReport(results, plan.NotCovered);
    }

    // "/R/itens/item[2]/x" (execução) corresponde a "/R/itens/item/x" (perfil).
    private static bool IsUnavailable(string? xpath, IReadOnlyCollection<string>? destinations) =>
        xpath is not null && destinations is { Count: > 0 }
        && destinations.Contains(System.Text.RegularExpressions.Regex.Replace(xpath, @"\[\d+\]", ""));

    /// <summary>Perfil → suíte → execução, para um template já compilado.</summary>
    public static QualityReport Run(CompiledTemplate template, Domain.Mapping.MappingTemplate mapping,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> lookupTables, IReadOnlyList<JsonElement>? seeds = null)
    {
        // Um prazo para tudo (valores sintéticos com pattern caro também): o que não couber reprova.
        using var deadline = Engine.Validation.GenerationDeadline.Start(StructuralSuiteBuilder.MaxRunTime);
        var profile = SourceProfiler.Build(template.Xsd.Schema, mapping, contentWithoutData: Engine.Execution.RuleExecutor.ContentWithoutData(template));
        return Run(template, StructuralSuiteBuilder.Build(profile, lookupTables, seeds));
    }
}

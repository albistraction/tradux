using System.Globalization;
using Tradux.Contracts.Design;
using Tradux.Domain.Schema;
using Tradux.Xsd;

namespace Tradux.Application.Design;

/// <summary>
/// Compara duas versões do XSD de destino e diz, elemento a elemento, o que
/// mudou e o que isso exige do mapeamento (ADR-0017): remapear, mapear,
/// revisar ou nada. Puro e determinístico; não grava nada.
///
/// Os elementos são casados pelo caminho lógico relativo à raiz (grupos sem
/// elemento próprio não entram no caminho), então uma raiz renomeada não
/// derruba o resto. Elemento que sumiu de um lugar e apareceu, com o mesmo nome
/// e tipo de nó, em outro é tratado como movido, com os filhos junto.
/// </summary>
public static class SchemaChangeAnalyzer
{
    /// <summary>Teto de mudanças reportadas: um XSD totalmente diferente não gera um relatório gigante.</summary>
    public const int MaxChanges = 5_000;

    /// <summary>Teto de caminhos removidos para os quais se procuram sugestões de remapeamento.</summary>
    public const int MaxSuggestionSearches = 500;

    private const int MaxSuggestions = 3;

    /// <summary>Orçamento de células de Levenshtein por relatório (custo de CPU limitado por requisição).</summary>
    public const long MaxSimilarityCells = 5_000_000;

    /// <param name="before">XSD atual do template (o mapeamento foi feito sobre ele).</param>
    /// <param name="after">XSD novo.</param>
    /// <param name="mappedPaths">Caminhos mapeados no XSD antigo (convenção da árvore de destino, ex.: <c>/DPS/numero</c>).</param>
    public static SchemaChangeReport Analyze(ImportedXsd before, ImportedXsd after, IReadOnlyCollection<string> mappedPaths)
    {
        var beforeRoot = before.Schema.RootElementName;
        var afterRoot = after.Schema.RootElementName;
        if (before.ContentHash == after.ContentHash)
        {
            return new SchemaChangeReport(beforeRoot, afterRoot, before.ContentHash, after.ContentHash, Identical: true, new SchemaChangeSummary(0, 0, 0, 0), []);
        }

        var analysis = new Analysis(Collect(before.Schema), Collect(after.Schema), beforeRoot, afterRoot, Relative(mappedPaths, beforeRoot));
        var changes = analysis.Run(before.Schema, after.Schema);
        var ordered = changes
            .OrderBy(c => c.Impact)
            .ThenBy(c => c.Path, StringComparer.Ordinal)
            .ThenBy(c => c.Kind)
            .ToList();
        if (ordered.Count > MaxChanges)
        {
            ordered = [.. ordered.Take(MaxChanges - 1), new SchemaChange("/" + afterRoot, SchemaChangeKind.ContentModelChanged, SchemaChangeImpact.Review, false,
                $"Mais de {MaxChanges} mudanças: o XSD novo é quase todo diferente; confira o mapeamento inteiro.")];
        }

        return new SchemaChangeReport(beforeRoot, afterRoot, before.ContentHash, after.ContentHash, Identical: false,
            new SchemaChangeSummary(
                ordered.Count(c => c.Impact == SchemaChangeImpact.Remap),
                ordered.Count(c => c.Impact == SchemaChangeImpact.Map),
                ordered.Count(c => c.Impact == SchemaChangeImpact.Review),
                ordered.Count(c => c.Impact == SchemaChangeImpact.Info)),
            ordered);
    }

    private static HashSet<string> Relative(IReadOnlyCollection<string> paths, string root)
    {
        var prefix = "/" + root;
        return paths
            .Where(p => p == prefix || p.StartsWith(prefix + "/", StringComparison.Ordinal))
            .Select(p => p[prefix.Length..])
            .ToHashSet(StringComparer.Ordinal);
    }

    // ---------------------------------------------------------------- modelo plano

    /// <summary>Elemento do XSD num caminho lógico relativo à raiz ("" = raiz).</summary>
    private sealed record Node(
        string Rel, string Name, string Parent, bool Complex, int Min, bool Repeated, bool Nillable,
        string? ScalarType, ScalarFacets? Facets, string? Default, string? ContentModel, bool Mappable);

    private sealed class Flat
    {
        public Dictionary<string, Node> Nodes { get; } = new(StringComparer.Ordinal);

        /// <summary>Filhos de cada bloco na ordem do XSD.</summary>
        public Dictionary<string, List<string>> Order { get; } = new(StringComparer.Ordinal);
    }

    private static Flat Collect(DestinationSchema schema)
    {
        var flat = new Flat();
        var mappable = ParticleRules.ContentMappable(true, schema.RootContent);
        flat.Nodes[""] = new Node("", schema.RootElementName, "", Complex: true, Min: 1, Repeated: false, Nillable: false,
            null, null, null, ParticleRules.ContentModel(schema.RootContent.Kind), mappable);
        CollectParticle(flat, schema.RootContent, "", mappable, optional: false, repeated: false);
        return flat;
    }

    private static void CollectParticle(Flat flat, SchemaParticle particle, string parent, bool mappable, bool optional, bool repeated)
    {
        foreach (var item in particle.Items)
        {
            switch (item)
            {
                case ScalarElementItem scalar:
                {
                    var e = scalar.Element;
                    Add(flat, new Node(parent + "/" + e.Name, e.Name, parent, Complex: false,
                        optional ? 0 : e.Cardinality.MinOccurs, repeated || e.Cardinality.IsRepeatable, e.Nillability == Nillability.Nillable,
                        e.ScalarType.ToString(), e.Facets, e.DefaultValue, null, mappable));
                    break;
                }

                case ComplexElementItem complex:
                {
                    var rel = parent + "/" + complex.Name;
                    var contentMappable = ParticleRules.ContentMappable(mappable, complex.Content);
                    if (Add(flat, new Node(rel, complex.Name, parent, Complex: true,
                        optional ? 0 : complex.Cardinality.MinOccurs, repeated || complex.Cardinality.IsRepeatable, complex.Nillability == Nillability.Nillable,
                        null, null, null, ParticleRules.ContentModel(complex.Content.Kind), contentMappable)))
                    {
                        CollectParticle(flat, complex.Content, rel, contentMappable, optional: false, repeated: false);
                    }

                    break;
                }

                case GroupItem group:
                    // Grupo sem elemento próprio: os elementos ficam no mesmo bloco; choice ou grupo opcional torna-os opcionais.
                    CollectParticle(flat, group.Group, parent, mappable: ParticleRules.GroupMappable(mappable, group.Group),
                        optional || group.Group.Kind == ParticleKind.Choice || !group.Group.Cardinality.IsRequired,
                        repeated || group.Group.Cardinality.IsRepeatable);
                    break;
            }
        }
    }

    // O mesmo nome duas vezes no mesmo bloco (ramos de choice): vale o primeiro.
    private static bool Add(Flat flat, Node node)
    {
        if (!flat.Nodes.TryAdd(node.Rel, node))
        {
            return false;
        }

        if (!flat.Order.TryGetValue(node.Parent, out var siblings))
        {
            flat.Order[node.Parent] = siblings = [];
        }

        siblings.Add(node.Rel);
        return true;
    }

    // ---------------------------------------------------------------- comparação

    private sealed class Analysis(Flat before, Flat after, string beforeRoot, string afterRoot, HashSet<string> mapped)
    {
        // Caminho antigo → caminho novo do mesmo elemento.
        private readonly Dictionary<string, string> _aligned = new(StringComparer.Ordinal);
        private readonly HashSet<string> _alignedTargets = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _alignedBack = new(StringComparer.Ordinal);
        private readonly List<SchemaChange> _changes = [];

        // Campo novo → nome do campo mapeado que saiu e provavelmente foi substituído por ele.
        private readonly Dictionary<string, string> _replaces = new(StringComparer.Ordinal);

        // Cada caminho mapeado e todos os seus ancestrais: "tem mapeamento dentro" em O(1).
        private readonly HashSet<string> _mappedPrefixes = Prefixes(mapped);

        private static HashSet<string> Prefixes(HashSet<string> paths)
        {
            var prefixes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var path in paths)
            {
                var current = path;
                while (prefixes.Add(current) && current.Length > 0)
                {
                    current = current[..current.LastIndexOf('/')];
                }
            }

            return prefixes;
        }

        private string B(string rel) => "/" + beforeRoot + rel;

        private string A(string rel) => "/" + afterRoot + rel;

        private bool MappedExactly(string rel) => mapped.Contains(rel);

        private bool MappedUnder(string rel) => _mappedPrefixes.Contains(rel);

        // Ordenados: os caminhos dentro de um bloco ficam contíguos (busca binária, sem varrer todos por bloco removido).
        private readonly List<string> _mappedSorted = mapped.Order(StringComparer.Ordinal).ToList();

        /// <summary>O próprio caminho e os mapeados dentro dele, em ordem.</summary>
        private List<string> MappedWithin(string rel)
        {
            var result = new List<string>();
            if (_mappedSorted.BinarySearch(rel, StringComparer.Ordinal) >= 0)
            {
                result.Add(rel);
            }

            // Caminhos com o mesmo prefixo ficam contíguos na ordem ordinal.
            var prefix = rel + "/";
            var start = _mappedSorted.BinarySearch(prefix, StringComparer.Ordinal);
            for (var i = start >= 0 ? start : ~start; i < _mappedSorted.Count && _mappedSorted[i].StartsWith(prefix, StringComparison.Ordinal); i++)
            {
                result.Add(_mappedSorted[i]);
            }

            return result;
        }

        public List<SchemaChange> Run(DestinationSchema beforeSchema, DestinationSchema afterSchema)
        {
            if (beforeRoot != afterRoot)
            {
                Change(A(""), SchemaChangeKind.RootChanged, SchemaChangeImpact.Info, false,
                    $"Elemento raiz mudou de <{beforeRoot}> para <{afterRoot}>; os mapeamentos acompanham.", beforeRoot, afterRoot, B(""));
            }

            if ((beforeSchema.TargetNamespace ?? "") != (afterSchema.TargetNamespace ?? ""))
            {
                Change(A(""), SchemaChangeKind.NamespaceChanged, SchemaChangeImpact.Info, false,
                    "Namespace do XSD mudou; o XML gerado já sai no namespace novo.", beforeSchema.TargetNamespace, afterSchema.TargetNamespace);
            }

            Align();
            foreach (var (b, a) in _aligned.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                Compare(before.Nodes[b], after.Nodes[a]);
            }

            foreach (var (b, a) in _aligned.Where(p => before.Nodes[p.Key].Complex && after.Nodes[p.Value].Complex))
            {
                CompareOrder(b, a);
            }

            ReportRemoved();
            ReportAdded();
            return _changes;
        }

        private void Align()
        {
            foreach (var rel in before.Nodes.Keys.Where(after.Nodes.ContainsKey))
            {
                Pair(rel, rel);
            }

            // Movidos: o topo de uma subárvore que saiu casa com o topo de uma que entrou (mesmo nome e tipo de nó, sem ambiguidade).
            bool progress;
            do
            {
                progress = false;
                var removedTops = before.Nodes.Values.Where(n => !_aligned.ContainsKey(n.Rel) && _aligned.ContainsKey(n.Parent))
                    .GroupBy(n => (n.Name, n.Complex)).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single());
                var addedTops = after.Nodes.Values.Where(n => !_alignedTargets.Contains(n.Rel) && _alignedTargets.Contains(n.Parent))
                    .GroupBy(n => (n.Name, n.Complex)).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single());
                foreach (var (key, removed) in removedTops.OrderBy(p => p.Value.Rel, StringComparer.Ordinal))
                {
                    if (!addedTops.TryGetValue(key, out var target) || _alignedTargets.Contains(target.Rel) || _aligned.ContainsKey(removed.Rel)
                        || removed.ScalarType != target.ScalarType || LikelyRenamedInPlace(removed))
                    {
                        continue;
                    }

                    var mappedInside = MappedUnder(removed.Rel);
                    // Revisão da Fase 14 (A3): o destino do movido vem primeiro, mas as alternativas no bloco original também aparecem.
                    IReadOnlyList<string> suggestions = mappedInside
                        ? [A(target.Rel), .. Suggest(removed.Rel).Where(x => x != A(target.Rel)).Take(MaxSuggestions - 1)]
                        : [A(target.Rel)];
                    Change(A(target.Rel), SchemaChangeKind.Moved, mappedInside ? SchemaChangeImpact.Remap : SchemaChangeImpact.Info, mappedInside,
                        mappedInside
                            ? $"'{removed.Name}' mudou de lugar ({B(removed.Rel)} → {A(target.Rel)}): mova o mapeamento."
                            : $"'{removed.Name}' mudou de lugar ({B(removed.Rel)} → {A(target.Rel)}).",
                        B(removed.Rel), A(target.Rel), B(removed.Rel), suggestions);
                    PairSubtree(removed.Rel, target.Rel);
                    progress = true;
                }
            }
            while (progress);
        }

        // No bloco de origem entrou um irmão novo de mesmo tipo e nome parecido: mais provável renome do que mudança de lugar.
        private bool LikelyRenamedInPlace(Node removed)
        {
            if (!_aligned.TryGetValue(removed.Parent, out var parentAfter))
            {
                return false;
            }

            return (after.Order.GetValueOrDefault(parentAfter) ?? [])
                .Select(r => after.Nodes[r])
                .Any(c => !_alignedTargets.Contains(c.Rel) && c.Complex == removed.Complex && c.ScalarType == removed.ScalarType
                    && c.Name != removed.Name && Similar(removed.Name, c.Name) >= 0.5);
        }

        // Revisão da Fase 14 (A2): Levenshtein com orçamento por relatório e pré-filtro pelo tamanho.
        private long _similarityBudget = MaxSimilarityCells;

        private double Similar(string x, string y)
        {
            var longest = Math.Max(x.Length, y.Length);
            if (longest == 0 || Math.Abs(x.Length - y.Length) > 0.6 * longest)
            {
                return 0; // não chega a 0,4
            }

            var cost = (long)x.Length * y.Length;
            if (cost > _similarityBudget)
            {
                return 0;
            }

            _similarityBudget -= cost;
            return Similarity(x, y);
        }

        private void Pair(string b, string a)
        {
            _aligned[b] = a;
            _alignedTargets.Add(a);
            _alignedBack[a] = b;
        }

        private void PairSubtree(string b, string a)
        {
            Pair(b, a);
            foreach (var child in before.Order.GetValueOrDefault(b) ?? [])
            {
                var target = a + child[b.Length..];
                if (after.Nodes.ContainsKey(target) && !_alignedTargets.Contains(target) && !_aligned.ContainsKey(child))
                {
                    PairSubtree(child, target);
                }
            }
        }

        private void Compare(Node b, Node a)
        {
            var path = A(a.Rel);
            var beforePath = B(b.Rel) == path ? null : B(b.Rel);
            var mappedHere = MappedExactly(b.Rel);
            var mappedInside = MappedUnder(b.Rel);

            if (b.Complex != a.Complex)
            {
                Change(path, SchemaChangeKind.KindChanged, mappedInside ? SchemaChangeImpact.Remap : SchemaChangeImpact.Info, mappedInside,
                    a.Complex
                        ? $"'{a.Name}' era um valor e virou bloco com campos dentro" + (mappedInside ? ": refaça o mapeamento nos campos do bloco." : ".")
                        : $"'{a.Name}' era um bloco e virou um valor simples" + (mappedInside ? ": refaça o mapeamento." : "."),
                    b.Complex ? "bloco" : "valor", a.Complex ? "bloco" : "valor", beforePath);
                return;
            }

            if (!a.Complex && b.ScalarType != a.ScalarType)
            {
                Change(path, SchemaChangeKind.TypeChanged, mappedHere ? SchemaChangeImpact.Review : SchemaChangeImpact.Info, mappedHere,
                    $"Tipo de '{a.Name}' mudou de {TypeName(b.ScalarType)} para {TypeName(a.ScalarType)}" + (mappedHere ? ": confira se o valor mapeado serve." : "."),
                    TypeName(b.ScalarType), TypeName(a.ScalarType), beforePath);
            }

            CompareCardinality(b, a, path, beforePath, mappedHere, mappedInside);

            if (b.Nillable != a.Nillable)
            {
                Change(path, SchemaChangeKind.NillableChanged, SchemaChangeImpact.Info, mappedHere,
                    a.Nillable ? $"'{a.Name}' passou a aceitar xsi:nil." : $"'{a.Name}' deixou de aceitar xsi:nil.",
                    b.Nillable ? "nillable" : "-", a.Nillable ? "nillable" : "-", beforePath);
            }

            var beforeFacets = DescribeFacets(b.Facets);
            var afterFacets = DescribeFacets(a.Facets);
            if (!a.Complex && !FacetsEqual(b.Facets ?? ScalarFacets.None, a.Facets ?? ScalarFacets.None))
            {
                var narrowed = Narrowed(b.Facets ?? ScalarFacets.None, a.Facets ?? ScalarFacets.None);
                var removedValues = (b.Facets ?? ScalarFacets.None).Enumeration.Except((a.Facets ?? ScalarFacets.None).Enumeration, StringComparer.Ordinal).ToList();
                var addedValues = (a.Facets ?? ScalarFacets.None).Enumeration.Except((b.Facets ?? ScalarFacets.None).Enumeration, StringComparer.Ordinal).ToList();
                var enumDetail = removedValues.Count + addedValues.Count == 0 ? ""
                    : $" Valores removidos: [{string.Join(", ", removedValues.Take(20))}]; incluídos: [{string.Join(", ", addedValues.Take(20))}].";
                Change(path, SchemaChangeKind.RestrictionChanged, mappedHere && narrowed ? SchemaChangeImpact.Review : SchemaChangeImpact.Info, mappedHere,
                    (narrowed
                        ? $"Restrições de '{a.Name}' ficaram mais rígidas" + (mappedHere ? ": teste com valores reais." : ".")
                        : $"Restrições de '{a.Name}' mudaram (mais permissivas).") + enumDetail,
                    beforeFacets ?? "sem restrições", afterFacets ?? "sem restrições", beforePath);
            }

            if (!a.Complex && b.Default != a.Default)
            {
                var needsMapping = a.Default is null && a.Min >= 1 && !mappedHere && a.Mappable && ParentActive(b);
                Change(path, SchemaChangeKind.DefaultChanged, needsMapping ? SchemaChangeImpact.Map : SchemaChangeImpact.Info, mappedHere,
                    needsMapping
                        ? $"'{a.Name}' perdeu o valor padrão do XSD e é obrigatório: mapeie."
                        : $"Valor padrão de '{a.Name}' mudou.",
                    b.Default ?? "-", a.Default ?? "-", beforePath);
            }

            if (a.Complex && (b.ContentModel != a.ContentModel || b.Mappable != a.Mappable))
            {
                var lost = b.Mappable && !a.Mappable;
                var impact = lost && mappedInside ? SchemaChangeImpact.Remap : SchemaChangeImpact.Info;
                Change(path, SchemaChangeKind.ContentModelChanged, impact, mappedInside,
                    lost
                        ? $"Conteúdo de '{a.Name}' passou a ser {a.ContentModel} com grupos que o motor ainda não gera" + (mappedInside ? ": o mapeamento dentro dele deixa de valer." : ".")
                        : !b.Mappable && a.Mappable
                            ? $"Conteúdo de '{a.Name}' agora pode ser mapeado."
                            : $"Modelo de conteúdo de '{a.Name}' mudou de {b.ContentModel} para {a.ContentModel}.",
                    b.ContentModel, a.ContentModel, beforePath);
            }
        }

        private void CompareCardinality(Node b, Node a, string path, string? beforePath, bool mappedHere, bool mappedInside)
        {
            if (b.Min == 0 && a.Min >= 1)
            {
                SchemaChangeImpact impact;
                string message;
                if (a.Complex)
                {
                    impact = SchemaChangeImpact.Review;
                    message = $"Bloco '{a.Name}' passou a ser obrigatório: ele sempre será gerado; confira os campos obrigatórios dentro dele.";
                }
                else if (mappedHere || a.Default is not null || !a.Mappable)
                {
                    impact = SchemaChangeImpact.Info;
                    message = $"'{a.Name}' passou a ser obrigatório" + (mappedHere ? " (já está mapeado)." : a.Default is not null ? " (coberto pelo valor padrão)." : ".");
                }
                else
                {
                    impact = ParentActive(b) ? SchemaChangeImpact.Map : SchemaChangeImpact.Info;
                    message = impact == SchemaChangeImpact.Map
                        ? $"'{a.Name}' passou a ser obrigatório e não está mapeado: mapeie."
                        : $"'{a.Name}' passou a ser obrigatório dentro de um bloco opcional: mapeie se esse bloco for usado.";
                }

                Change(path, SchemaChangeKind.BecameRequired, impact, mappedHere, message, "opcional", "obrigatório", beforePath);
            }
            else if (b.Min >= 1 && a.Min == 0)
            {
                Change(path, SchemaChangeKind.BecameOptional, SchemaChangeImpact.Info, mappedHere,
                    $"'{a.Name}' deixou de ser obrigatório.", "obrigatório", "opcional", beforePath);
            }

            if (!b.Repeated && a.Repeated)
            {
                var impact = a.Complex
                    ? (mappedInside ? SchemaChangeImpact.Remap : SchemaChangeImpact.Info)
                    : (mappedHere ? SchemaChangeImpact.Review : SchemaChangeImpact.Info);
                Change(path, SchemaChangeKind.BecameRepeatable, impact, mappedInside,
                    a.Complex
                        ? $"Bloco '{a.Name}' passou a repetir" + (mappedInside ? ": defina a lista de origem (for each) e confira os campos dentro dele." : ".")
                        : $"'{a.Name}' passou a repetir" + (mappedHere ? ": confira se o valor mapeado deve virar uma lista." : "."),
                    "único", "repetível", beforePath);
            }
            else if (b.Repeated && !a.Repeated)
            {
                var impact = a.Complex
                    ? (MappedExactly(b.Rel) ? SchemaChangeImpact.Remap : mappedInside ? SchemaChangeImpact.Review : SchemaChangeImpact.Info)
                    : (mappedHere ? SchemaChangeImpact.Review : SchemaChangeImpact.Info);
                Change(path, SchemaChangeKind.StoppedRepeating, impact, mappedInside,
                    a.Complex
                        ? $"Bloco '{a.Name}' deixou de repetir" + (MappedExactly(b.Rel) ? ": o for each deixa de valer; mapeie os campos direto." : ".")
                        : $"'{a.Name}' deixou de repetir.",
                    "repetível", "único", beforePath);
            }
        }

        // O bloco que contém o elemento é sempre gerado: obrigatório até a raiz, ou com algo mapeado dentro.
        private bool ParentActive(Node node)
        {
            var parent = node.Parent;
            while (parent.Length > 0)
            {
                var p = before.Nodes[parent];
                if (p.Min == 0 && !MappedUnder(parent))
                {
                    return false;
                }

                parent = p.Parent;
            }

            return true;
        }

        private void CompareOrder(string b, string a)
        {
            var beforeOrder = (before.Order.GetValueOrDefault(b) ?? [])
                .Where(c => _aligned.TryGetValue(c, out var t) && after.Nodes[t].Parent == a)
                .Select(c => _aligned[c])
                .ToList();
            var common = beforeOrder.ToHashSet(StringComparer.Ordinal);
            var afterOrder = (after.Order.GetValueOrDefault(a) ?? []).Where(common.Contains).ToList();
            if (beforeOrder.SequenceEqual(afterOrder, StringComparer.Ordinal))
            {
                return;
            }

            string Names(IEnumerable<string> rels) => string.Join(", ", rels.Select(r => after.Nodes[r].Name));
            Change(A(a), SchemaChangeKind.OrderChanged, SchemaChangeImpact.Info, MappedUnder(b),
                $"Sequência das tags em '{after.Nodes[a].Name}' mudou; o XML já sai na ordem do XSD novo — nada a remapear.",
                Names(beforeOrder), Names(afterOrder), B(b) == A(a) ? null : B(b));
        }

        private void ReportRemoved()
        {
            var searches = 0;
            foreach (var top in before.Nodes.Values
                .Where(n => !_aligned.ContainsKey(n.Rel) && _aligned.ContainsKey(n.Parent))
                .OrderBy(n => n.Rel, StringComparer.Ordinal))
            {
                var mappedInside = MappedUnder(top.Rel)
                    ? MappedWithin(top.Rel)
                    : [];
                if (!top.Complex || mappedInside is [var only] && only == top.Rel)
                {
                    Change(B(top.Rel), SchemaChangeKind.Removed, mappedInside.Count > 0 ? SchemaChangeImpact.Remap : SchemaChangeImpact.Info, mappedInside.Count > 0,
                        mappedInside.Count > 0 ? $"'{top.Name}' saiu do XSD e estava mapeado: mova o mapeamento ou descarte." : $"'{top.Name}' saiu do XSD.",
                        Describe(top), null, null, mappedInside.Count > 0 && searches++ < MaxSuggestionSearches ? Remember(top.Name, Suggest(top.Rel)) : null);
                    continue;
                }

                Change(B(top.Rel), SchemaChangeKind.Removed, mappedInside.Count > 0 ? SchemaChangeImpact.Remap : SchemaChangeImpact.Info, mappedInside.Count > 0,
                    mappedInside.Count > 0
                        ? $"Bloco '{top.Name}' saiu do XSD com {mappedInside.Count.ToString(CultureInfo.InvariantCulture)} mapeamento(s) dentro."
                        : $"Bloco '{top.Name}' saiu do XSD.",
                    Describe(top));
                foreach (var rel in mappedInside)
                {
                    var name = before.Nodes.TryGetValue(rel, out var node) ? node.Name : rel[(rel.LastIndexOf('/') + 1)..];
                    Change(B(rel), SchemaChangeKind.Removed, SchemaChangeImpact.Remap, true,
                        $"'{name}' (dentro de '{top.Name}') saiu do XSD e estava mapeado: mova o mapeamento ou descarte.",
                        node is null ? null : Describe(node), null, null, searches++ < MaxSuggestionSearches ? Remember(name, Suggest(rel)) : null);
                }
            }
        }

        private void ReportAdded()
        {
            foreach (var top in after.Nodes.Values
                .Where(n => !_alignedTargets.Contains(n.Rel) && _alignedTargets.Contains(n.Parent))
                .OrderBy(n => n.Rel, StringComparer.Ordinal))
            {
                var parentBefore = _alignedBack.GetValueOrDefault(top.Parent);
                var parentActive = parentBefore is not null && (parentBefore.Length == 0 || ParentActive(before.Nodes[parentBefore]) && (before.Nodes[parentBefore].Min >= 1 || MappedUnder(parentBefore)));
                ReportAddedNode(top, parentActive, top.Name);
            }
        }

        private void ReportAddedNode(Node node, bool parentActive, string topName)
        {
            if (node.Complex)
            {
                var required = node.Min >= 1;
                Change(A(node.Rel), SchemaChangeKind.Added, SchemaChangeImpact.Info, false,
                    required ? $"Bloco novo obrigatório '{node.Name}'." : $"Bloco novo opcional '{node.Name}': mapeie se for usar.",
                    null, Describe(node));
                if (required && node.Mappable)
                {
                    foreach (var child in after.Order.GetValueOrDefault(node.Rel) ?? [])
                    {
                        ReportAddedRequired(after.Nodes[child], parentActive, topName);
                    }
                }

                return;
            }

            var needs = node.Min >= 1 && node.Default is null && node.Mappable && parentActive;
            Change(A(node.Rel), SchemaChangeKind.Added, needs ? SchemaChangeImpact.Map : SchemaChangeImpact.Info, false,
                needs ? $"Campo novo obrigatório '{node.Name}'{Replacing(node)}: mapeie."
                    : node.Min >= 1 && node.Default is not null ? $"Campo novo obrigatório '{node.Name}' (coberto pelo valor padrão)."
                    : node.Min >= 1 && !node.Mappable ? $"Campo novo obrigatório '{node.Name}' dentro de conteúdo que o motor ainda não gera."
                    : node.Min >= 1 ? $"Campo novo obrigatório '{node.Name}' dentro de bloco opcional: mapeie se esse bloco for usado."
                    : $"Campo novo opcional '{node.Name}'.",
                null, Describe(node));
        }

        // Dentro de um bloco novo obrigatório: só o que também é obrigatório precisa de ação.
        private void ReportAddedRequired(Node node, bool parentActive, string topName)
        {
            if (node.Min < 1)
            {
                return;
            }

            if (node.Complex)
            {
                if (node.Mappable)
                {
                    foreach (var child in after.Order.GetValueOrDefault(node.Rel) ?? [])
                    {
                        ReportAddedRequired(after.Nodes[child], parentActive, topName);
                    }
                }

                return;
            }

            var needs = node.Default is null && node.Mappable && parentActive;
            Change(A(node.Rel), SchemaChangeKind.Added, needs ? SchemaChangeImpact.Map : SchemaChangeImpact.Info, false,
                needs ? $"Campo novo obrigatório '{node.Name}' (no bloco novo '{topName}'): mapeie."
                    : $"Campo novo obrigatório '{node.Name}' (no bloco novo '{topName}').",
                null, Describe(node));
        }

        /// <summary>Para onde um mapeamento que saiu provavelmente deve ir: mesmo nome em outro lugar, ou nome parecido no mesmo bloco.</summary>
        private IReadOnlyList<string> Suggest(string rel)
        {
            var name = rel[(rel.LastIndexOf('/') + 1)..];
            var node = before.Nodes.GetValueOrDefault(rel);
            var complex = node?.Complex ?? false;
            var parentBefore = rel[..rel.LastIndexOf('/')];
            var parentAfter = AlignedAncestor(parentBefore);
            // Candidatos: mesmo nome em qualquer lugar ou irmãos no bloco correspondente (nunca a árvore toda com Levenshtein).
            var candidates = (ByName.GetValueOrDefault(name.ToLowerInvariant()) ?? [])
                .Concat((parentAfter is null ? null : after.Order.GetValueOrDefault(parentAfter))?.Select(r => after.Nodes[r]) ?? [])
                .DistinctBy(c => c.Rel);

            return candidates
                .Where(c => c.Rel.Length > 0 && c.Complex == complex && c.Mappable && !Occupied(c.Rel))
                .Select(c =>
                {
                    var sameName = string.Equals(name, c.Name, StringComparison.OrdinalIgnoreCase);
                    var sameParent = parentAfter is not null && c.Parent == parentAfter;
                    var similarity = sameName ? 1 : sameParent ? Similar(name, c.Name) : 0;
                    var eligible = sameName || (sameParent && similarity >= 0.4);
                    var score = (sameName ? 3 : 0) + (sameParent ? 2 : 0) + (c.ScalarType == node?.ScalarType ? 1 : -2)
                        + (_alignedTargets.Contains(c.Rel) ? 0 : 1) + similarity * 2;
                    return (Node: c, Eligible: eligible, Score: score);
                })
                .Where(x => x.Eligible)
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.Node.Rel, StringComparer.Ordinal)
                .Take(MaxSuggestions)
                .Select(x => A(x.Node.Rel))
                .ToList();
        }

        private Dictionary<string, List<Node>>? _byName;

        private Dictionary<string, List<Node>> ByName => _byName ??= after.Nodes.Values
            .GroupBy(n => n.Name.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        // Já recebe mapeamento: o mesmo elemento continuou e estava mapeado.
        private bool Occupied(string afterRel) => _alignedBack.TryGetValue(afterRel, out var b) && mapped.Contains(b);

        private string? AlignedAncestor(string rel)
        {
            while (true)
            {
                if (_aligned.TryGetValue(rel, out var target))
                {
                    return target;
                }

                if (rel.Length == 0)
                {
                    return null;
                }

                rel = rel[..rel.LastIndexOf('/')];
            }
        }

        private IReadOnlyList<string>? Remember(string removedName, IReadOnlyList<string>? suggestions)
        {
            if (suggestions is [var first, ..])
            {
                _replaces.TryAdd(first, removedName);
            }

            return suggestions;
        }

        private string Replacing(Node node) =>
            _replaces.TryGetValue(A(node.Rel), out var old) ? $" (provável substituto de '{old}')" : "";

        private void Change(string path, SchemaChangeKind kind, SchemaChangeImpact impact, bool mapped, string message,
            string? beforeValue = null, string? afterValue = null, string? beforePath = null, IReadOnlyList<string>? suggestions = null) =>
            _changes.Add(new SchemaChange(path, kind, impact, mapped, message, beforeValue, afterValue, beforePath, suggestions is { Count: > 0 } ? suggestions : null));
    }

    // ---------------------------------------------------------------- descrições

    private static string Describe(Node node)
    {
        var occurs = (node.Min >= 1 ? "obrigatório" : "opcional") + (node.Repeated ? ", repetível" : "");
        return node.Complex ? $"bloco ({occurs})" : $"{TypeName(node.ScalarType)} ({occurs})" + (DescribeFacets(node.Facets) is { } f ? $"; {f}" : "");
    }

    private static string TypeName(string? type) => type switch
    {
        "String" => "texto",
        "Decimal" => "decimal",
        "Date" => "data",
        "YearMonth" => "ano-mês",
        null => "-",
        _ => type,
    };

    internal static string? DescribeFacets(ScalarFacets? facets)
    {
        if (facets is null || !facets.HasAny)
        {
            return null;
        }

        var parts = new List<string>();
        if (facets.Length is { } length)
        {
            parts.Add($"tamanho {length.ToString(CultureInfo.InvariantCulture)}");
        }

        if (facets.MinLength is not null || facets.MaxLength is not null)
        {
            parts.Add($"tamanho {facets.MinLength?.ToString(CultureInfo.InvariantCulture) ?? "0"}–{facets.MaxLength?.ToString(CultureInfo.InvariantCulture) ?? "∞"}");
        }

        if (facets.Enumeration.Count > 0)
        {
            parts.Add($"valores [{string.Join(", ", facets.Enumeration.Take(20))}{(facets.Enumeration.Count > 20 ? ", …" : "")}]");
        }

        if (facets.PatternSteps.Count > 0)
        {
            parts.Add("padrão " + string.Join(" e ", facets.PatternSteps.Select(step => string.Join(" | ", step))));
        }

        if (facets.MinInclusive is not null || facets.MinExclusive is not null || facets.MaxInclusive is not null || facets.MaxExclusive is not null)
        {
            parts.Add("limites " + (facets.MinInclusive is { } mi ? $"≥ {mi}" : facets.MinExclusive is { } me ? $"> {me}" : "")
                + (facets.MaxInclusive is { } ma ? $" ≤ {ma}" : facets.MaxExclusive is { } mx ? $" < {mx}" : ""));
        }

        if (facets.TotalDigits is not null || facets.FractionDigits is not null)
        {
            parts.Add($"dígitos {facets.TotalDigits?.ToString(CultureInfo.InvariantCulture) ?? "-"},{facets.FractionDigits?.ToString(CultureInfo.InvariantCulture) ?? "-"}");
        }

        return string.Join("; ", parts);
    }

    // Mais rígida = algum valor antes aceito pode ser recusado agora. Limites numéricos e patterns: qualquer mudança conta.
    private static bool Narrowed(ScalarFacets b, ScalarFacets a) =>
        Less(a.MaxLength, b.MaxLength)
        || (a.MinLength ?? 0) > (b.MinLength ?? 0)
        || (a.Length is not null && a.Length != b.Length)
        || (a.Enumeration.Count > 0 && (b.Enumeration.Count == 0 || b.Enumeration.Except(a.Enumeration, StringComparer.Ordinal).Any()))
        || (a.PatternSteps.Count > 0 && Patterns(a) != Patterns(b))
        || Less(a.TotalDigits, b.TotalDigits)
        || Less(a.FractionDigits, b.FractionDigits)
        || (a.MinInclusive is not null && a.MinInclusive != b.MinInclusive)
        || (a.MinExclusive is not null && a.MinExclusive != b.MinExclusive)
        || (a.MaxInclusive is not null && a.MaxInclusive != b.MaxInclusive)
        || (a.MaxExclusive is not null && a.MaxExclusive != b.MaxExclusive);

    // Estrutural (revisão da Fase 14, A1): a descrição trunca enumerações longas e não serve para comparar.
    private static bool FacetsEqual(ScalarFacets b, ScalarFacets a) =>
        b.Length == a.Length && b.MinLength == a.MinLength && b.MaxLength == a.MaxLength
        && b.Enumeration.Order(StringComparer.Ordinal).SequenceEqual(a.Enumeration.Order(StringComparer.Ordinal), StringComparer.Ordinal)
        && Patterns(b) == Patterns(a)
        && b.MinInclusive == a.MinInclusive && b.MaxInclusive == a.MaxInclusive
        && b.MinExclusive == a.MinExclusive && b.MaxExclusive == a.MaxExclusive
        && b.TotalDigits == a.TotalDigits && b.FractionDigits == a.FractionDigits;

    // "Novo limite menor que o antigo" (sem limite antes = infinito).
    private static bool Less(int? after, int? before) => after is { } a && (before is not { } b || a < b);

    private static string Patterns(ScalarFacets facets) => string.Join("\n", facets.PatternSteps.Select(step => string.Join("|", step)));

    /// <summary>1 − distância de Levenshtein normalizada, sem diferenciar maiúsculas (nomes até 64 caracteres).</summary>
    internal static double Similarity(string x, string y)
    {
        if (x.Length > 64 || y.Length > 64)
        {
            return string.Equals(x, y, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        }

        x = x.ToLowerInvariant();
        y = y.ToLowerInvariant();
        var previous = new int[y.Length + 1];
        var current = new int[y.Length + 1];
        for (var j = 0; j <= y.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= x.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= y.Length; j++)
            {
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (x[i - 1] == y[j - 1] ? 0 : 1));
            }

            (previous, current) = (current, previous);
        }

        var longest = Math.Max(x.Length, y.Length);
        return longest == 0 ? 1 : 1 - (double)previous[y.Length] / longest;
    }
}

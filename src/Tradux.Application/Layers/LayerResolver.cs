using Tradux.Application.Design;
using Tradux.Application.Snapshots;
using Tradux.Contracts;
using Tradux.Contracts.Design;
using Tradux.Domain.Layers;
using Tradux.Xsd;

namespace Tradux.Application.Layers;

/// <summary>Cadeia de camadas malformada (ordem de tipos, pai que não bate, base sem XSD).</summary>
public sealed class InvalidLayerChainException(string message) : InvalidOperationException(message);

/// <summary>
/// Resolve base → produto → cliente (seção 16, ADR-0006): aplica as
/// diferenças em ordem, valida cada uma contra a árvore de destino do XSD
/// da base e registra a história de cada caminho — é isso que permite
/// explicar a origem de toda regra publicada e detectar conflitos quando
/// uma camada de baixo muda.
/// </summary>
public sealed class LayerResolver
{
    public const string PathNotInSchema = "LAYER_PATH_NOT_IN_SCHEMA";
    public const string PathNotMappable = "LAYER_PATH_NOT_MAPPABLE";
    public const string EntryKindMismatch = "LAYER_ENTRY_KIND_MISMATCH";
    public const string RemoveOfMissing = "LAYER_REMOVE_MISSING";
    public const string EntryInactive = "LAYER_ENTRY_INACTIVE";
    public const string SchemaInvalid = "LAYER_SCHEMA_INVALID";

    private readonly XsdImportSecurityOptions _importOptions;

    public LayerResolver(XsdImportSecurityOptions? importOptions = null)
    {
        _importOptions = importOptions ?? XsdImportSecurityOptions.Default;
    }

    /// <summary>Resolve a cadeia (da base ao topo).</summary>
    /// <exception cref="InvalidLayerChainException">Cadeia fora da forma base → [produto] → [cliente].</exception>
    public ResolvedTemplate Resolve(IReadOnlyList<TemplateLayer> chain)
    {
        ValidateChain(chain);
        var baseLayer = chain[0];
        var schema = baseLayer.Schema!;

        ImportedXsd xsd;
        try
        {
            xsd = XsdSchemaImporter.ImportInMemory(schema.Files, schema.EntryPoint, schema.RootElementName, _importOptions);
        }
        catch (Exception ex) when (XsdSchemaImporter.IsImportFailure(ex))
        {
            var conflict = new ResolutionConflict("/", baseLayer.Ref, SchemaInvalid, DiagnosticSeverity.Critical, $"XSD da base não importa: {ex.Message}");
            return new ResolvedTemplate(
                EmptyContent(schema), new SnapshotManifest(Manifest(chain), new Dictionary<string, RuleTrace>(), new Dictionary<string, RuleTrace>()),
                new Dictionary<string, MappingEntry>(), [conflict]);
        }

        var tree = DestinationTreeBuilder.Build(xsd).Root;
        var nodes = DestinationTreeBuilder.Flatten(tree)
            .Where(n => n.Kind != DestinationNodeKind.Group)
            .ToDictionary(n => n.Path, StringComparer.Ordinal);

        var entries = new Dictionary<string, MappingEntry>(StringComparer.Ordinal);
        var histories = new Dictionary<string, List<RuleTraceStep>>(StringComparer.Ordinal);
        var lookups = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        var lookupHistories = new Dictionary<string, List<RuleTraceStep>>(StringComparer.Ordinal);
        var conflicts = new List<ResolutionConflict>();

        foreach (var layer in chain)
        {
            foreach (var change in layer.Overrides)
            {
                ApplyOverride(layer, change, nodes, entries, histories, conflicts);
            }

            foreach (var change in layer.LookupOverrides)
            {
                var step = new RuleTraceStep(layer.Ref, layer.Kind, change.Operation);
                if (change.Operation == OverrideOperation.Set)
                {
                    lookups[change.TableName] = change.Rows ?? new Dictionary<string, string>();
                    History(lookupHistories, change.TableName).Add(step);
                }
                else if (lookups.Remove(change.TableName))
                {
                    History(lookupHistories, change.TableName).Add(step);
                }
                else
                {
                    conflicts.Add(new ResolutionConflict($"lookup:{change.TableName}", layer.Ref, RemoveOfMissing, DiagnosticSeverity.Warning,
                        $"Tabela DE-PARA '{change.TableName}' removida pela camada, mas não existe mais nas camadas abaixo."));
                }
            }
        }

        ReportInactiveEntries(tree, entries, histories, conflicts);

        var mapping = MappingPaths.Build(tree, entries);
        var content = new SnapshotContent
        {
            SchemaFiles = schema.Files,
            SchemaEntryPoint = schema.EntryPoint,
            RootElementName = schema.RootElementName,
            Mapping = mapping,
            LookupTables = lookups,
            SourceSchema = baseLayer.SourceSchema is { } source ? new SourceSchemaContent(source.Files, source.EntryPoint, source.RootElementName) : null,
        };

        // A explicação descreve o que foi PUBLICADO: regra que existe numa
        // camada mas não entra no template (ex.: dentro de bloco sem coleção)
        // fica sem fonte final e com a razão (revisão da Fase 7, C4).
        var published = MappingPaths.Flatten(mapping, tree.Name);
        var manifest = new SnapshotManifest(
            Manifest(chain),
            histories.ToDictionary(
                h => h.Key,
                h => published.ContainsKey(h.Key)
                    ? new RuleTrace(LastSet(h.Value), h.Value)
                    : new RuleTrace(null, h.Value, entries.ContainsKey(h.Key)
                        ? "Regra definida numa camada, mas não gerada: o bloco repetível acima não tem coleção de origem."
                        : null),
                StringComparer.Ordinal),
            lookupHistories.ToDictionary(
                h => h.Key,
                h => new RuleTrace(lookups.ContainsKey(h.Key) ? LastSet(h.Value) : null, h.Value),
                StringComparer.Ordinal),
            conflicts.Where(c => c.Severity != DiagnosticSeverity.Critical).ToList());

        return new ResolvedTemplate(content, manifest, published, conflicts);
    }

    private static LayerRef LastSet(List<RuleTraceStep> history) => history.Last(s => s.Operation == OverrideOperation.Set).Layer;

    private static void ApplyOverride(
        TemplateLayer layer,
        LayerOverride change,
        Dictionary<string, DestinationNode> nodes,
        Dictionary<string, MappingEntry> entries,
        Dictionary<string, List<RuleTraceStep>> histories,
        List<ResolutionConflict> conflicts)
    {
        var step = new RuleTraceStep(layer.Ref, layer.Kind, change.Operation, change.Comment);

        if (change.Operation == OverrideOperation.Remove)
        {
            if (entries.Remove(change.Path))
            {
                History(histories, change.Path).Add(step);
            }
            else
            {
                conflicts.Add(new ResolutionConflict(change.Path, layer.Ref, RemoveOfMissing, DiagnosticSeverity.Warning,
                    $"A camada remove a regra de '{change.Path}', mas ela não existe nas camadas abaixo (foi removida ou renomeada)."));
            }

            return;
        }

        if (!nodes.TryGetValue(change.Path, out var node))
        {
            conflicts.Add(new ResolutionConflict(change.Path, layer.Ref, PathNotInSchema, DiagnosticSeverity.Critical,
                $"'{change.Path}' não existe no XSD de destino da base atual — a regra desta camada não tem onde ser aplicada."));
            return;
        }

        if (!node.Mappable)
        {
            conflicts.Add(new ResolutionConflict(change.Path, layer.Ref, PathNotMappable, DiagnosticSeverity.Critical,
                $"'{change.Path}' está em conteúdo que o motor ainda não mapeia (xs:choice/xs:all ou grupo)."));
            return;
        }

        var entry = change.Entry;
        var fits = node.Kind switch
        {
            DestinationNodeKind.Scalar => entry is { Value: not null, Collection: null },
            DestinationNodeKind.Complex when node.IsRepeatable => entry is { Collection: not null, Value: null, Condition: null },
            _ => false,
        };

        if (!fits)
        {
            var expected = node.Kind == DestinationNodeKind.Scalar
                ? "um valor (campo simples)"
                : node.IsRepeatable ? "um bloco for each (elemento repetível)" : "nenhuma regra própria (elemento que só agrupa)";
            conflicts.Add(new ResolutionConflict(change.Path, layer.Ref, EntryKindMismatch, DiagnosticSeverity.Critical,
                $"'{change.Path}' espera {expected}; a regra desta camada não combina com o tipo do nó no XSD atual."));
            return;
        }

        entries[change.Path] = entry!;
        History(histories, change.Path).Add(step);
    }

    // Regra dentro de um bloco repetível que ficou sem "for each" não é gerada: avisa.
    private static void ReportInactiveEntries(
        DestinationNode root,
        Dictionary<string, MappingEntry> entries,
        Dictionary<string, List<RuleTraceStep>> histories,
        List<ResolutionConflict> conflicts)
    {
        void Visit(DestinationNode node, bool insideInactiveBlock)
        {
            foreach (var child in node.Children)
            {
                var inactive = insideInactiveBlock;
                if (child.Kind == DestinationNodeKind.Complex && child.IsRepeatable)
                {
                    inactive = insideInactiveBlock || !(entries.TryGetValue(child.Path, out var e) && e.Collection is not null);
                }

                if (insideInactiveBlock && entries.ContainsKey(child.Path) && histories.TryGetValue(child.Path, out var history))
                {
                    conflicts.Add(new ResolutionConflict(child.Path, history[^1].Layer, EntryInactive, DiagnosticSeverity.Warning,
                        $"'{child.Path}' tem regra, mas o bloco repetível acima não tem coleção de origem — a regra não é gerada."));
                }

                Visit(child, inactive);
            }
        }

        Visit(root, false);
    }

    private static void ValidateChain(IReadOnlyList<TemplateLayer> chain)
    {
        if (chain.Count is 0 or > 3)
        {
            throw new InvalidLayerChainException("A cadeia precisa ter de 1 a 3 camadas: base, [produto], [cliente].");
        }

        if (chain[0].Kind != LayerKind.Base || chain[0].Parent is not null || chain[0].Schema is null)
        {
            throw new InvalidLayerChainException("A primeira camada precisa ser uma base com XSD de destino e sem pai.");
        }

        // Formas válidas: [Base], [Base, Product], [Base, Client], [Base, Product, Client].
        var kinds = string.Join('>', chain.Select(l => l.Kind));
        if (kinds is not ("Base" or "Base>Product" or "Base>Client" or "Base>Product>Client"))
        {
            throw new InvalidLayerChainException($"Cadeia '{kinds}' fora de ordem: base → [produto] → [cliente].");
        }

        for (var i = 1; i < chain.Count; i++)
        {
            var layer = chain[i];
            if (layer.Parent != chain[i - 1].Ref)
            {
                throw new InvalidLayerChainException($"Camada '{layer.Ref}' aponta para '{layer.Parent}', não para '{chain[i - 1].Ref}'.");
            }

            if (layer.Schema is not null)
            {
                throw new InvalidLayerChainException($"Só a base define XSD de destino; '{layer.Ref}' traz um.");
            }
        }
    }

    private static List<RuleTraceStep> History(Dictionary<string, List<RuleTraceStep>> histories, string key)
    {
        if (!histories.TryGetValue(key, out var list))
        {
            histories[key] = list = [];
        }

        return list;
    }

    private static List<ManifestLayer> Manifest(IReadOnlyList<TemplateLayer> chain) =>
        chain.Select(l => new ManifestLayer(l.Ref, l.Kind, l.ContentHash)).ToList();

    private static SnapshotContent EmptyContent(LayerSchema schema) => new()
    {
        SchemaFiles = schema.Files,
        SchemaEntryPoint = schema.EntryPoint,
        RootElementName = schema.RootElementName,
        Mapping = new Domain.Mapping.MappingTemplate(),
    };
}

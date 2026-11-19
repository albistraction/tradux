using System.Text.Json;
using Tradux.Application.Snapshots;
using Tradux.Domain.Layers;

namespace Tradux.Application.Layers;

/// <summary>
/// Diferenças rastreáveis entre duas resoluções (seção 16: a atualização
/// manual "mostra diferenças"): caminho a caminho, o que entrou, saiu ou
/// mudou, e de qual camada vinha antes e depois.
/// </summary>
public static class ResolutionDiff
{
    public static IReadOnlyList<ResolutionChange> Compute(
        IReadOnlyDictionary<string, MappingEntry> before,
        SnapshotManifest? beforeManifest,
        IReadOnlyDictionary<string, MappingEntry> after,
        SnapshotManifest afterManifest)
    {
        var changes = new List<ResolutionChange>();
        foreach (var path in before.Keys.Union(after.Keys).OrderBy(p => p, StringComparer.Ordinal))
        {
            var had = before.TryGetValue(path, out var old);
            var has = after.TryGetValue(path, out var now);
            var previousSource = beforeManifest?.Paths.GetValueOrDefault(path)?.FinalSource;
            var newSource = afterManifest.Paths.GetValueOrDefault(path)?.FinalSource;

            if (had && !has)
            {
                changes.Add(new ResolutionChange(path, ChangeKind.Removed, previousSource, null));
            }
            else if (!had && has)
            {
                changes.Add(new ResolutionChange(path, ChangeKind.Added, null, newSource));
            }
            // Mesma regra vinda da mesma camada (só com outra versão, ex.: cliente
            // rebaseado) não é mudança; conteúdo diferente ou outra camada é.
            else if (Serialize(old!) != Serialize(now!) || Identity(previousSource) != Identity(newSource))
            {
                changes.Add(new ResolutionChange(path, ChangeKind.Changed, previousSource, newSource));
            }
        }

        return changes;
    }

    private static LayerIdentity? Identity(LayerRef? reference) => reference is null ? null : LayerIdentity.Of(reference);

    private static string Serialize(MappingEntry entry) => JsonSerializer.Serialize(entry, SnapshotJson.Options);
}

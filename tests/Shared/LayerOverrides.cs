using Tradux.Application.Layers;
using Tradux.Domain.Layers;
using Tradux.Domain.Mapping;

namespace Tradux.Tests.Shared;

/// <summary>Camada base de teste montada de um conteúdo de fixture: uma diferença <c>Set</c> por caminho.</summary>
public static class LayerOverrides
{
    public static IReadOnlyList<LayerOverride> FromMappingTemplate(MappingTemplate template, string rootElementName) =>
        MappingPaths.Flatten(template, rootElementName)
            .OrderBy(e => e.Key, StringComparer.Ordinal)
            .Select(e => new LayerOverride(e.Key, OverrideOperation.Set, e.Value))
            .ToList();

    public static IReadOnlyList<LookupOverride> FromLookupTables(IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> tables) =>
        tables.OrderBy(t => t.Key, StringComparer.Ordinal)
            .Select(t => new LookupOverride(t.Key, OverrideOperation.Set, t.Value))
            .ToList();
}

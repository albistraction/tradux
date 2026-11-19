using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tradux.Application.Snapshots;
using Tradux.Domain.Layers;

namespace Tradux.Application.Layers;

/// <summary>Hash do conteúdo de uma camada (tipo, pai, XSDs, diferenças): mesmo conteúdo, mesmo hash.</summary>
internal static class LayerHashing
{
    public static string Compute(
        LayerKind kind, LayerRef? parent, bool shared, LayerSchema? schema,
        IReadOnlyList<LayerOverride> overrides, IReadOnlyList<LookupOverride> lookupOverrides, LayerSchema? sourceSchema = null)
    {
        var payload = JsonSerializer.Serialize(new
        {
            Kind = kind,
            Parent = parent?.ToString(),
            Shared = shared,
            schemaHash = schema is null ? "" : SchemaFilesHash.Canonical(schema.Files, schema.EntryPoint, schema.RootElementName),
            Overrides = overrides,
            LookupOverrides = lookupOverrides.Select(l => new { l.TableName, l.Operation, Rows = l.Rows?.OrderBy(r => r.Key, StringComparer.Ordinal).ToList() }),
        }, SnapshotJson.Options);
        if (sourceSchema is not null)
        {
            // Só com XSD de origem: hashes de camadas anteriores à Fase 12 não mudam.
            payload += "\nsource:" + SchemaFilesHash.Of(sourceSchema.Files, sourceSchema.EntryPoint, sourceSchema.RootElementName);
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    /// <summary>Hash recalculado da camada como está (integridade na importação de pacote).</summary>
    public static string Compute(TemplateLayer layer) =>
        Compute(layer.Kind, layer.Parent, layer.Shared, layer.Schema, layer.Overrides, layer.LookupOverrides, layer.SourceSchema);
}

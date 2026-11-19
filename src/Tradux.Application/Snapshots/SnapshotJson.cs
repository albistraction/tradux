using System.Text.Json;
using Tradux.Domain.Mapping;

namespace Tradux.Application.Snapshots;

/// <summary>
/// Opções únicas de serialização JSON do conteúdo de snapshot (mapeamento e
/// tabelas de lookup). Usadas tanto para o hash de conteúdo quanto pelos
/// adaptadores de persistência — mesma entrada, mesmos bytes.
/// </summary>
public static class SnapshotJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.General) { WriteIndented = false };

    /// <summary>
    /// Devolve o conteúdo exatamente como ele volta da persistência (mapeamento
    /// e lookups serializados e desserializados). Ex.: um literal
    /// <see cref="DateOnly"/> volta como texto ISO, um <c>int</c> volta como
    /// <c>decimal</c>. A publicação valida, calcula o hash e grava esta forma —
    /// o que foi validado é o que será servido (revisão da Fase 5).
    /// </summary>
    public static SnapshotContent Rehydrate(SnapshotContent content)
    {
        var mapping = JsonSerializer.Deserialize<MappingTemplate>(JsonSerializer.Serialize(content.Mapping, Options), Options)
            ?? throw new InvalidOperationException("Mapeamento vazio após serialização.");
        var lookups = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(
                JsonSerializer.Serialize(content.LookupTables, Options), Options)!
            .ToDictionary(t => t.Key, t => (IReadOnlyDictionary<string, string>)t.Value);

        return content with { Mapping = mapping, LookupTables = lookups };
    }
}

using System.Text.Json.Serialization;
using Tradux.Domain.Mapping;

namespace Tradux.Domain.Layers;

// Camadas de template (seção 16 do requisito de origem, ADR-0006/ADR-0010):
// template-base da prefeitura → camada de produto → camada de cliente →
// snapshot publicado. Tudo aqui é dado imutável; resolver e publicar é papel
// de Tradux.Application.

[JsonConverter(typeof(JsonStringEnumConverter<LayerKind>))]
public enum LayerKind
{
    /// <summary>Template-base (layout da prefeitura): traz o XSD de destino e o conjunto completo de regras.</summary>
    Base,

    /// <summary>Camada de produto: só diferenças sobre a base.</summary>
    Product,

    /// <summary>Camada de cliente: só diferenças e extensões sobre o produto (ou direto sobre a base).</summary>
    Client,
}

[JsonConverter(typeof(JsonStringEnumConverter<OverrideOperation>))]
public enum OverrideOperation
{
    /// <summary>Define (ou substitui) a regra do caminho.</summary>
    Set,

    /// <summary>Retira a regra herdada do caminho.</summary>
    Remove,
}

/// <summary>
/// Referência a uma versão exata de camada. Inclui a organização dona: uma
/// camada de cliente pode apontar para a camada de produto de outra
/// organização (o dono do produto), desde que ela seja compartilhada.
/// </summary>
public sealed record LayerRef(string OrganizationId, string LayerId, int Version)
{
    public override string ToString() => $"{OrganizationId}/{LayerId}@{Version}";
}

/// <summary>
/// Bloco "for each" de um elemento repetível (seção 14). Mesmos campos de
/// <see cref="CollectionMapping"/>, sem destino nem conteúdo do item (a camada
/// endereça cada nó por caminho): é o formato gravado das camadas.
/// </summary>
/// <param name="SingleObjectAsItem">Objeto único no caminho conta como 1 item (ADR-0020); o padrão não é serializado.</param>
public sealed record CollectionEntry(
    string SourceCollectionPath,
    RuleNode? Filter = null,
    RuleNode? OrderByKey = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool SingleObjectAsItem = false);

/// <summary>
/// Regra de um nó de destino, endereçado por caminho lógico: escalar
/// (<see cref="Value"/> + <see cref="Condition"/>) ou repetível
/// (<see cref="Collection"/>). Exatamente um dos dois lados é preenchido.
/// </summary>
public sealed record MappingEntry
{
    public RuleNode? Value { get; init; }
    public RuleNode? Condition { get; init; }
    public CollectionEntry? Collection { get; init; }

    [JsonIgnore]
    public bool IsCollection => Collection is not null;

    public static MappingEntry Scalar(RuleNode value, RuleNode? condition = null) => new() { Value = value, Condition = condition };

    public static MappingEntry ForEach(CollectionEntry collection) => new() { Collection = collection };
}

/// <summary>
/// Diferença de uma camada sobre a anterior, endereçada pelo caminho lógico
/// do nó de destino (ex.: <c>/DPS/numero</c>, <c>/Pedido/itens/item</c> —
/// sem índice), estável entre versões compatíveis do XSD (ADR-0006).
/// </summary>
public sealed record LayerOverride(string Path, OverrideOperation Operation, MappingEntry? Entry = null, string? Comment = null);

/// <summary>Diferença em tabela DE-PARA: define a tabela inteira ou a remove.</summary>
public sealed record LookupOverride(
    string TableName,
    OverrideOperation Operation,
    IReadOnlyDictionary<string, string>? Rows = null);

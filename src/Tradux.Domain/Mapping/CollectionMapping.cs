using System.Text.Json.Serialization;

namespace Tradux.Domain.Mapping;

/// <summary>
/// Bloco de iteração (seção 14 do requisito de origem): mapeia uma coleção
/// de origem para um elemento de destino repetível. <see cref="ItemContent"/>
/// é aplicado a cada item, com sua própria árvore de dados local (seção 14:
/// "contexto local do item") — permitindo listas aninhadas (um
/// <see cref="CollectionMapping"/> dentro do <see cref="ItemContent"/> de
/// outro). O índice de cada item (base zero) fica disponível dentro do
/// contexto local em <c>"$index"</c>.
/// </summary>
public sealed class CollectionMapping
{
    /// <summary>Nome do elemento de destino repetível (ex.: "item").</summary>
    public required string DestinationElementName { get; init; }

    /// <summary>Caminho, na árvore de dados do escopo em volta, até uma coleção (<see cref="System.Collections.IEnumerable"/> de objetos).</summary>
    public required string SourceCollectionPath { get; init; }

    /// <summary>
    /// Aceita um objeto único no lugar da coleção, como lista de 1 item (ADR-0020):
    /// ex.: um documento de entrada (uma DPS) vira uma ocorrência do bloco repetível
    /// de destino (um Rps). Desligado, objeto no caminho é erro
    /// (<c>COLLECTION_SOURCE_NOT_ENUMERABLE</c>): um caminho errado nunca vira 1 item em silêncio.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool SingleObjectAsItem { get; init; }

    /// <summary>Filtro opcional avaliado por item (seção 14) — item excluído quando o resultado não é verdadeiro.</summary>
    public RuleNode? Filter { get; init; }

    /// <summary>Ordenação opcional (seção 14) — itens ordenados de forma crescente pelo valor desta expressão.</summary>
    public RuleNode? OrderByKey { get; init; }

    public required MappingTemplate ItemContent { get; init; }
}

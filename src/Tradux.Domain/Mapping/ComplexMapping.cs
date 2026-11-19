namespace Tradux.Domain.Mapping;

/// <summary>
/// Mapeamento de um elemento de destino complexo não repetível (seção 9).
/// <see cref="Content"/> mapeia os filhos desse elemento, com aninhamento
/// arbitrário (sequence/choice/all, ADR-0018).
/// </summary>
public sealed class ComplexMapping
{
    public required string DestinationElementName { get; init; }
    public required MappingTemplate Content { get; init; }
}

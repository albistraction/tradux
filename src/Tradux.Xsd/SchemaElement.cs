using Tradux.Domain.Schema;

namespace Tradux.Xsd;

/// <summary>
/// Um elemento escalar do schema de destino. O gerador emite os elementos na
/// ordem do schema (seção 15 do requisito de origem), nunca na ordem em que os
/// mapeamentos foram declarados.
/// </summary>
public sealed class SchemaElement
{
    public required string Name { get; init; }
    public required Cardinality Cardinality { get; init; }
    public required Nillability Nillability { get; init; }
    public required SchemaScalarType ScalarType { get; init; }
    public string? DefaultValue { get; init; }

    /// <summary>Restrições do tipo simples (tamanho, enumeração, pattern, limites, dígitos) — ADR-0011.</summary>
    public ScalarFacets Facets { get; init; } = ScalarFacets.None;
}

using Tradux.Domain.Schema;

namespace Tradux.Xsd;

/// <summary>
/// Grupo de partícula (<c>xs:sequence</c>, <c>xs:choice</c> ou <c>xs:all</c>),
/// possivelmente aninhado: o conteúdo de um tipo complexo.
/// </summary>
public sealed class SchemaParticle
{
    public required ParticleKind Kind { get; init; }
    public required Cardinality Cardinality { get; init; }
    public required IReadOnlyList<SchemaParticleItem> Items { get; init; }
}

/// <summary>Um item dentro de um <see cref="SchemaParticle"/>: elemento escalar, elemento complexo, ou grupo aninhado.</summary>
public abstract class SchemaParticleItem
{
}

public sealed class ScalarElementItem : SchemaParticleItem
{
    public required SchemaElement Element { get; init; }
}

public sealed class ComplexElementItem : SchemaParticleItem
{
    public required string Name { get; init; }
    public required Cardinality Cardinality { get; init; }
    public required Nillability Nillability { get; init; }
    public required SchemaParticle Content { get; init; }

    /// <summary>
    /// <c>ds:Signature</c>: quem chama o serviço assina o XML convertido (ADR-0025).
    /// Sempre opcional e opaca: nunca mapeada nem gerada.
    /// </summary>
    public bool SignedByCaller { get; init; }
}

/// <summary>Grupo aninhado (ex.: um <c>xs:choice</c> dentro de um <c>xs:sequence</c>).</summary>
public sealed class GroupItem : SchemaParticleItem
{
    public required SchemaParticle Group { get; init; }
}

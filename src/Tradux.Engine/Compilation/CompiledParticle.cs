using Tradux.Domain.Schema;
using Tradux.Xsd;

namespace Tradux.Engine.Compilation;

/// <summary>
/// Versão compilada e executável de um <see cref="Tradux.Xsd.SchemaParticle"/>:
/// itens já ligados a mapeamentos, na ordem do schema. <see cref="Kind"/> e
/// <see cref="Cardinality"/> decidem, na execução, quais ramos de um
/// <c>xs:choice</c> vão para o XML (ADR-0018).
/// </summary>
public sealed class CompiledParticle
{
    public required IReadOnlyList<CompiledItemBinding> Items { get; init; }

    public required ParticleKind Kind { get; init; }

    public required Cardinality Cardinality { get; init; }
}

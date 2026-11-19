using Tradux.Xsd;

namespace Tradux.Engine.Execution;

/// <summary>Resultado, para um dado de entrada específico, de um item de destino (escalar, complexo ou coleção).</summary>
public abstract class ResolvedItem
{
    public required string Name { get; init; }
}

public sealed class ResolvedScalar : ResolvedItem
{
    public required SchemaElement Element { get; init; }
    public required ResolvedValueKind Kind { get; init; }
    public object? Value { get; init; }
}

/// <summary>Elemento complexo não repetível já resolvido.</summary>
public sealed class ResolvedComplex : ResolvedItem
{
    public required ResolvedValueKind Kind { get; init; }

    /// <summary>Filhos já resolvidos, em ordem de schema; <c>null</c> quando <see cref="Kind"/> é <see cref="ResolvedValueKind.Omitted"/>.</summary>
    public IReadOnlyList<ResolvedItem>? Children { get; init; }
}

/// <summary>Grupo resolvido: seus itens entram direto no elemento pai.</summary>
public sealed class ResolvedGroup : ResolvedItem
{
    public required IReadOnlyList<ResolvedItem> Items { get; init; }
}

/// <summary>Bloco de iteração já resolvido: um conjunto de filhos por item que sobreviveu ao filtro.</summary>
public sealed class ResolvedCollection : ResolvedItem
{
    public required IReadOnlyList<IReadOnlyList<ResolvedItem>> Items { get; init; }
}

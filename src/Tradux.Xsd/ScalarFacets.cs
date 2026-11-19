namespace Tradux.Xsd;

/// <summary>
/// Restrições de um tipo simples do XSD (ADR-0011), já combinadas ao longo
/// da cadeia de derivação: o que um valor precisa satisfazer para o
/// elemento validar. Limites numéricos ficam como texto canônico do XSD
/// (invariante), para não perder precisão nem depender de cultura.
/// </summary>
public sealed record ScalarFacets
{
    public static ScalarFacets None { get; } = new();

    public int? Length { get; init; }

    public int? MinLength { get; init; }

    public int? MaxLength { get; init; }

    /// <summary>Valores permitidos (vazio = sem enumeração). Vem do passo de derivação mais específico que declara enumeração.</summary>
    public IReadOnlyList<string> Enumeration { get; init; } = [];

    /// <summary>
    /// Patterns por passo de derivação: dentro de um passo, basta casar com
    /// um deles (OU); o valor precisa satisfazer todos os passos (E).
    /// </summary>
    public IReadOnlyList<IReadOnlyList<string>> PatternSteps { get; init; } = [];

    public string? MinInclusive { get; init; }

    public string? MaxInclusive { get; init; }

    public string? MinExclusive { get; init; }

    public string? MaxExclusive { get; init; }

    public int? TotalDigits { get; init; }

    public int? FractionDigits { get; init; }

    /// <summary>Tem alguma restrição além do tipo base.</summary>
    public bool HasAny =>
        Length is not null || MinLength is not null || MaxLength is not null || Enumeration.Count > 0 || PatternSteps.Count > 0
        || MinInclusive is not null || MaxInclusive is not null || MinExclusive is not null || MaxExclusive is not null
        || TotalDigits is not null || FractionDigits is not null;
}

using System.Text.Json.Serialization;

namespace Tradux.Contracts.Quality;

/// <summary>Categoria de caso da suíte estrutural (seção 19, ADR-0011 §3).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<StructuralCategory>))]
public enum StructuralCategory
{
    /// <summary>Só os campos que alimentam destinos obrigatórios.</summary>
    Required,

    /// <summary>Obrigatórios e opcionais, valores típicos.</summary>
    Complete,

    LowerBound,

    UpperBound,

    /// <summary>Um valor de cada enumeração por caso, até cobrir todos.</summary>
    Enumeration,

    /// <summary>Coleções vazias ou com vários itens.</summary>
    Repetition,

    /// <summary>Nulo em destino que aceita <c>xsi:nil</c> (separado de ausente).</summary>
    Null,

    /// <summary>Viola uma restrição crítica; esperado: reprovado.</summary>
    Invalid,

    /// <summary>Um ramo de xs:choice por vez (ADR-0020): cada ramo mapeado gera XML válido sozinho.</summary>
    Choice,
}

/// <summary>
/// Resultado de um caso — sem os dados do caso nem o XML gerado (seção 20):
/// só nome, categoria, alvo (campo de origem/restrição), expectativa e, em
/// caso de falha, código e XPath.
/// </summary>
public sealed record StructuralCaseResult(
    string Name,
    StructuralCategory Category,
    bool ExpectedValid,
    bool Passed,
    string? Target = null,
    string? FailureCode = null,
    string? DestinationXPath = null);

/// <summary>Item que a suíte não pôde exercitar e por quê — nunca contado como coberto.</summary>
public sealed record CoverageGap(string Area, string Path, string Reason);

/// <summary>Relatório de qualidade de um template (testes estruturais automáticos).</summary>
public sealed record QualityReport(IReadOnlyList<StructuralCaseResult> Cases, IReadOnlyList<CoverageGap> NotCovered)
{
    public static QualityReport Empty { get; } = new([], []);

    /// <summary>Aprovado só com ao menos um caso e todos aprovados — zero casos nunca é aprovação (revisão da Fase 8, B2).</summary>
    public bool Passed => Cases.Count > 0 && Cases.All(c => c.Passed);
}

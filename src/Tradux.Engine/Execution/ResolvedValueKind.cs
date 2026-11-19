namespace Tradux.Engine.Execution;

/// <summary>
/// Como um campo resolvido deve ser representado no XML de saída.
/// </summary>
public enum ResolvedValueKind
{
    /// <summary>Elemento omitido (era opcional e não recebeu valor).</summary>
    Omitted,

    /// <summary>Elemento presente com conteúdo textual.</summary>
    Present,

    /// <summary>Elemento presente com <c>xsi:nil="true"</c>.</summary>
    ExplicitNil,
}

namespace Tradux.Xsd;

/// <summary>
/// Modelo canônico e compilado de um schema de destino (seção 15 do
/// requisito de origem: o XML é construído a partir deste modelo, nunca de
/// XML-base fixo ou concatenação de string).
/// </summary>
public sealed class DestinationSchema
{
    public required string RootElementName { get; init; }
    public string? TargetNamespace { get; init; }

    /// <summary>Conteúdo do elemento raiz (sequence/choice/all, possivelmente aninhados).</summary>
    public required SchemaParticle RootContent { get; init; }

    public required IReadOnlyList<SchemaAttribute> Attributes { get; init; }
}

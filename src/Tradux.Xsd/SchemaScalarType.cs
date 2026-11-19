namespace Tradux.Xsd;

/// <summary>
/// Tipo escalar primitivo de um campo de destino, derivado do
/// <c>XmlTypeCode</c> do datatype XSD efetivo — independente de quantos
/// níveis de tipo nomeado/restrição levam até ele (ex.: um tipo nomeado que
/// restringe <c>xs:decimal</c> ainda resolve para <see cref="Decimal"/>).
/// Só os tipos com formatação própria; os demais tipos simples são
/// <see cref="String"/> e a validação XSD aplica o tipo real.
/// </summary>
public enum SchemaScalarType
{
    String,
    Decimal,
    Date,
    YearMonth,

    /// <summary>xs:dateTime: data e hora ISO 8601, com fuso quando o valor tiver.</summary>
    DateTime,
}

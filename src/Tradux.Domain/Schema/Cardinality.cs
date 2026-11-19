namespace Tradux.Domain.Schema;

/// <summary>
/// Cardinalidade de um campo de schema. Representa apenas "quantas vezes o
/// elemento pode aparecer" (minOccurs/maxOccurs). Nulabilidade (nillable) é
/// um eixo independente — ver <see cref="Nillability"/> — nunca colapse os
/// dois conceitos (docs/GLOSSARY.md, docs/domain/DOMAIN_MODEL.md).
/// </summary>
/// <param name="MaxOccurs">
/// Número máximo de ocorrências, ou <c>null</c> quando o XSD declara
/// <c>maxOccurs="unbounded"</c>.
/// </param>
public readonly record struct Cardinality(int MinOccurs, int? MaxOccurs)
{
    public bool IsRequired => MinOccurs >= 1;

    public bool IsRepeatable => MaxOccurs is null or > 1;

    public static Cardinality Single => new(MinOccurs: 1, MaxOccurs: 1);

    public static Cardinality Optional => new(MinOccurs: 0, MaxOccurs: 1);
}

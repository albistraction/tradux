using System.Xml.Schema;

namespace Tradux.Xsd;

/// <summary>
/// Lê as facetas de um tipo simples compilado, do tipo mais derivado até o
/// tipo embutido do XSD (ADR-0011). Regras de combinação da especificação
/// XSD 1.0: tamanhos e dígitos ficam com o valor mais restritivo; limites e
/// enumeração ficam com o passo mais derivado que os declara (o XSD já exige
/// que ele seja mais restritivo que o base); patterns de passos diferentes
/// se somam (o valor precisa casar com todos os passos).
/// </summary>
public static class XsdFacetReader
{
    public static ScalarFacets Read(XmlSchemaSimpleType? type)
    {
        int? length = null, minLength = null, maxLength = null, totalDigits = null, fractionDigits = null;
        string? minInclusive = null, maxInclusive = null, minExclusive = null, maxExclusive = null;
        IReadOnlyList<string> enumeration = [];
        var patternSteps = new List<IReadOnlyList<string>>();

        for (var current = type; current is not null; current = current.BaseXmlSchemaType as XmlSchemaSimpleType)
        {
            if (current.Content is not XmlSchemaSimpleTypeRestriction restriction)
            {
                continue;
            }

            var stepEnumeration = new List<string>();
            var stepPatterns = new List<string>();
            foreach (var facet in restriction.Facets.OfType<XmlSchemaFacet>())
            {
                var value = facet.Value;
                if (value is null)
                {
                    continue;
                }

                switch (facet)
                {
                    case XmlSchemaEnumerationFacet:
                        stepEnumeration.Add(value);
                        break;
                    case XmlSchemaPatternFacet:
                        stepPatterns.Add(value);
                        break;
                    case XmlSchemaLengthFacet:
                        length ??= Int(value);
                        break;
                    case XmlSchemaMinLengthFacet:
                        minLength = Max(minLength, Int(value));
                        break;
                    case XmlSchemaMaxLengthFacet:
                        maxLength = Min(maxLength, Int(value));
                        break;
                    case XmlSchemaTotalDigitsFacet:
                        totalDigits = Min(totalDigits, Int(value));
                        break;
                    case XmlSchemaFractionDigitsFacet:
                        fractionDigits = Min(fractionDigits, Int(value));
                        break;
                    case XmlSchemaMinInclusiveFacet:
                        minInclusive ??= value.Trim();
                        break;
                    case XmlSchemaMaxInclusiveFacet:
                        maxInclusive ??= value.Trim();
                        break;
                    case XmlSchemaMinExclusiveFacet:
                        minExclusive ??= value.Trim();
                        break;
                    case XmlSchemaMaxExclusiveFacet:
                        maxExclusive ??= value.Trim();
                        break;
                }
            }

            if (enumeration.Count == 0 && stepEnumeration.Count > 0)
            {
                enumeration = stepEnumeration;
            }

            if (stepPatterns.Count > 0)
            {
                patternSteps.Add(stepPatterns);
            }
        }

        var facets = new ScalarFacets
        {
            Length = length,
            MinLength = minLength,
            MaxLength = maxLength,
            Enumeration = enumeration,
            PatternSteps = patternSteps,
            MinInclusive = minInclusive,
            MaxInclusive = maxInclusive,
            MinExclusive = minExclusive,
            MaxExclusive = maxExclusive,
            TotalDigits = totalDigits,
            FractionDigits = fractionDigits,
        };
        return facets.HasAny ? facets : ScalarFacets.None;
    }

    // Tamanho/dígitos acima de int (ex.: maxLength="99999999999") valem, na
    // prática, "sem limite": nenhum texto .NET passa de int.MaxValue. Satura em
    // vez de estourar (revisão da Fase 8, B3 — antes virava OverflowException/500).
    private static int Int(string value) =>
        decimal.TryParse(value.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? (int)Math.Min(parsed, int.MaxValue)
            : int.MaxValue;

    private static int? Min(int? current, int value) => current is { } c ? Math.Min(c, value) : value;

    private static int? Max(int? current, int value) => current is { } c ? Math.Max(c, value) : value;
}

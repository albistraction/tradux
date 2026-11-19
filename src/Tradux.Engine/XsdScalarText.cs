using System.Globalization;
using Tradux.Xsd;

namespace Tradux.Engine;

/// <summary>
/// Texto de um valor no tipo escalar do XSD de destino (decimal com casas, data,
/// data e hora, ano-mês, texto) — a regra de formatação da emissão do XML.
/// </summary>
internal static class XsdScalarText
{
    /// <summary>Falso quando o valor não tem forma no tipo (ex.: objeto num xs:string, texto não numérico num xs:decimal).</summary>
    public static bool TryFormat(SchemaScalarType type, int? fractionDigits, object value, out string text)
    {
        switch (type)
        {
            case SchemaScalarType.String:
                return ScalarValues.TryToText(value, out text);
            case SchemaScalarType.Decimal:
                return TryFormatDecimal(value, fractionDigits, out text);
            case SchemaScalarType.Date:
                return TryFormatDate(value, "yyyy-MM-dd", out text);
            case SchemaScalarType.YearMonth:
                return TryFormatDate(value, "yyyy-MM", out text);
            case SchemaScalarType.DateTime:
                // Texto passa como está (inclusive o fuso recebido, ex.: 2026-08-19T17:05:00-03:00)
                // e a validação XSD decide.
                text = value is string raw ? raw.Trim() : string.Empty;
                return value is string;
            default:
                throw new NotSupportedException($"Tipo escalar '{type}' sem formatação definida.");
        }
    }

    private static bool TryFormatDecimal(object value, int? fractionDigits, out string text)
    {
        if (!ScalarValues.TryToDecimal(value, out var number))
        {
            text = string.Empty;
            return false;
        }

        // Completa com zeros até 2 casas (ou até fractionDigits, se menor),
        // mas nunca descarta dígito significativo: arredondar em silêncio
        // mudaria valor fiscal (1.001 → "1.00"; alíquota 0.0275 → "0.03").
        // Excesso de casas vai como está e a validação XSD reprova.
        var padding = Math.Min(2, fractionDigits ?? 2);
        var format = padding == 0 ? "0.############################" : "0." + new string('0', padding) + new string('#', 28 - padding);
        text = number.ToString(format, CultureInfo.InvariantCulture);
        return true;
    }

    // Texto ISO é normalizado para a forma do tipo XSD de destino pela data "de
    // calendário" do próprio relógio, sem converter fuso (ex.: "2026-09-28T00:00:00"
    // num xs:date vira "2026-09-28"). Texto que não é data ISO passa como está e a
    // validação XSD decide.
    private static bool TryFormatDate(object value, string format, out string text)
    {
        if (value is not string raw)
        {
            text = string.Empty;
            return false;
        }

        text = ScalarValues.TryToDate(raw, out var parsed) ? parsed.DateTime.ToString(format, CultureInfo.InvariantCulture) : raw;
        return true;
    }
}

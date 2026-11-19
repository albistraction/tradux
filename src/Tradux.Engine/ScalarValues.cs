using System.Collections;
using System.Globalization;
using Tradux.Engine.Operators;

namespace Tradux.Engine;

/// <summary>
/// Única regra de coerção de valores escalares do motor — operadores, condições,
/// filtros e emissão do XML leem número, data, booleano e texto daqui. A árvore de
/// origem e os literais só trazem texto, <see cref="decimal"/>, <see cref="bool"/>,
/// nulo, dicionário e lista (ADR-0025 §8; o <c>$index</c> do item também é
/// <see cref="decimal"/>); datas chegam como texto ISO-8601.
/// </summary>
internal static class ScalarValues
{
    /// <summary>
    /// Formas XSD aceitas como data em texto: xs:date, xs:gYearMonth e xs:dateTime,
    /// com ou sem fração, com ou sem fuso ("Z" ou "±hh:mm").
    /// </summary>
    private static readonly string[] IsoDateFormats =
    [
        "yyyy-MM-dd",
        "yyyy-MM",
        "yyyy-MM-ddTHH:mm:ss",
        "yyyy-MM-ddTHH:mm:ss.FFFFFFF",
        "yyyy-MM-ddTHH:mm:ssK",
        "yyyy-MM-ddTHH:mm:ss.FFFFFFFK",
        "yyyy-MM-ddzzz",
        "yyyy-MM-ddK",
    ];

    /// <summary>
    /// Número em cultura invariante, sem separador de milhar nem espaços: "1500,50"
    /// não pode virar 150050 em silêncio, nem "1,500" virar 1500.
    /// </summary>
    public static bool TryToDecimal(object? value, out decimal result)
    {
        switch (value)
        {
            case decimal number:
                result = number;
                return true;
            case string text:
                return decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out result);
            default:
                result = default;
                return false;
        }
    }

    /// <summary>Texto ISO-8601 como instante; sem fuso, vale como UTC.</summary>
    public static bool TryToDate(object? value, out DateTimeOffset result)
    {
        if (value is string text)
        {
            return DateTimeOffset.TryParseExact(text, IsoDateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out result);
        }

        result = default;
        return false;
    }

    /// <summary>Booleano de verdade (nulo = falso); qualquer outro valor é erro de regra, nunca "truthy".</summary>
    /// <exception cref="RuleOperatorException"><c>EXPECTED_BOOLEAN</c>.</exception>
    public static bool ToBool(object? value) => value switch
    {
        null => false,
        bool b => b,
        _ => throw new RuleOperatorException("EXPECTED_BOOLEAN", $"Esperado valor booleano, recebido '{DescribeKind(value)}'."),
    };

    /// <summary>
    /// Texto de um valor escalar (números e booleanos em cultura invariante). Objeto e
    /// lista não têm texto: falso, para o chamador produzir diagnóstico crítico em vez
    /// de um nome de tipo CLR no XML.
    /// </summary>
    public static bool TryToText(object? value, out string text)
    {
        switch (value)
        {
            case null:
                text = string.Empty;
                return true;
            case string s:
                text = s;
                return true;
            case bool b:
                text = b ? "True" : "False";
                return true;
            case decimal number:
                text = number.ToString(CultureInfo.InvariantCulture);
                return true;
            default:
                text = string.Empty;
                return false;
        }
    }

    /// <summary>Nome neutro do tipo de valor para mensagens (nunca nome de tipo CLR).</summary>
    public static string DescribeKind(object? value) => value switch
    {
        null => "nulo",
        string => "texto",
        bool => "booleano",
        decimal => "número",
        IDictionary or IReadOnlyDictionary<string, object?> => "objeto",
        IEnumerable => "lista",
        _ => "objeto",
    };
}

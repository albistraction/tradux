using System.Globalization;

namespace Tradux.Engine.Operators;

/// <summary>
/// Implementação real de cada operador do catálogo (seção 13 do requisito
/// de origem). Métodos estáticos simples, chamados diretamente pelo
/// delegate que <c>TemplateCompiler</c> compila via
/// <see cref="System.Linq.Expressions"/> — não há despacho/interpretação em
/// tempo de execução, só a chamada de método já resolvida em tempo de
/// compilação (ADR-0001). Toda validação de tipo/faixa lança
/// <see cref="RuleOperatorException"/>, convertida pelo <c>RuleExecutor</c>
/// em diagnóstico crítico com código estável — nunca deixa a geração
/// inteira quebrar com uma exceção não tratada. Coerção de número, data,
/// booleano e texto: <see cref="ScalarValues"/>.
/// </summary>
public static class RuleOperatorMethods
{
    // Nomeado AreEqual (não Equals) para nunca colidir, em reflection, com o
    // método estático herdado object.Equals(object, object).
    public static object? AreEqual(object? a, object? b) => ValueEquals(a, b);

    public static object? NotEquals(object? a, object? b) => !ValueEquals(a, b);

    public static object? GreaterThan(object? a, object? b) => Compare(a, b, "greaterThan") > 0;

    public static object? LessThan(object? a, object? b) => Compare(a, b, "lessThan") < 0;

    public static object? IsEmpty(object? a) => a is null || a is string { Length: 0 };

    public static object? NotEmpty(object? a) => !(bool)IsEmpty(a)!;

    public static object? And(object?[] values) => values.All(ScalarValues.ToBool);

    public static object? Or(object?[] values) => values.Any(ScalarValues.ToBool);

    public static object? Not(object? a) => !ScalarValues.ToBool(a);

    public static object? Copy(object? a) => a;

    public static object? Coalesce(object?[] values) => values.FirstOrDefault(v => v is not null);

    public static object? SetDefault(object? value, object? defaultValue) => value ?? defaultValue;

    public static object? SetNull() => null;

    public static object? Omit() => OmitMarker.Instance;

    public static object? Concat(object?[] values) => string.Concat(values.Select(ToText));

    public static object? Substring(object? value, object? start, object? length)
    {
        var text = RequireString(value, "substring");
        var startIndex = RequireInt(start, "substring");
        if (startIndex < 0 || startIndex > text.Length)
        {
            throw new RuleOperatorException(
                "SUBSTRING_START_OUT_OF_RANGE", $"'start' ({startIndex}) fora do intervalo da string de tamanho {text.Length}.");
        }

        if (length is null)
        {
            return text[startIndex..];
        }

        var len = RequireInt(length, "substring");
        if (len < 0 || startIndex + len > text.Length)
        {
            throw new RuleOperatorException(
                "SUBSTRING_LENGTH_OUT_OF_RANGE",
                $"'length' ({len}) fora do intervalo da string de tamanho {text.Length} a partir de {startIndex}.");
        }

        return text.Substring(startIndex, len);
    }

    public static object? Replace(object? value, object? oldValue, object? newValue) =>
        RequireString(value, "replace").Replace(
            RequireString(oldValue, "replace"), RequireString(newValue, "replace"), StringComparison.Ordinal);

    public static object? Trim(object? value) => RequireString(value, "trim").Trim();

    public static object? PadLeft(object? value, object? totalWidth, object? paddingChar) =>
        RequireString(value, "padLeft").PadLeft(RequireInt(totalWidth, "padLeft"), RequirePadChar(paddingChar));

    public static object? PadRight(object? value, object? totalWidth, object? paddingChar) =>
        RequireString(value, "padRight").PadRight(RequireInt(totalWidth, "padRight"), RequirePadChar(paddingChar));

    /// <summary>
    /// Texto com no máximo <paramref name="maxLength"/> caracteres (corta o excesso; nulo continua nulo).
    /// Para limites de tamanho do XSD de destino sem o encadeamento padRight + substring + trim.
    /// </summary>
    public static object? Truncate(object? value, object? maxLength)
    {
        if (value is null)
        {
            return null;
        }

        var text = RequireString(value, "truncate");
        var max = RequireInt(maxLength, "truncate");
        if (max < 0)
        {
            throw new RuleOperatorException("TRUNCATE_LENGTH_OUT_OF_RANGE", $"'truncate' espera tamanho máximo maior ou igual a zero, recebeu {max}.");
        }

        return text.Length <= max ? text : text[..max];
    }

    public static object? FormatDate(object? value, object? format)
    {
        var fmt = RequireString(format, "formatDate");
        if (value is null)
        {
            return null;
        }

        return ScalarValues.TryToDate(value, out var date)
            ? date.ToString(fmt, CultureInfo.InvariantCulture)
            : throw new RuleOperatorException(
                "FORMAT_DATE_INVALID_TYPE",
                $"'formatDate' espera data em texto ISO-8601, recebeu '{ScalarValues.DescribeKind(value)}'.");
    }

    public static object? FormatNumber(object? value, object? format)
    {
        if (value is null)
        {
            return null;
        }

        var fmt = RequireString(format, "formatNumber");
        if (!ScalarValues.TryToDecimal(value, out var number))
        {
            throw new RuleOperatorException(
                "FORMAT_NUMBER_INVALID_TYPE", $"'formatNumber' espera um número, recebeu '{ScalarValues.DescribeKind(value)}'.");
        }

        return number.ToString(fmt, CultureInfo.InvariantCulture);
    }

    public static object? Lookup(object? key, IReadOnlyDictionary<string, string> table)
    {
        if (key is null)
        {
            return null;
        }

        var keyText = ToText(key);
        return table.TryGetValue(keyText, out var mapped)
            ? mapped
            : throw new RuleOperatorException("LOOKUP_KEY_NOT_FOUND", $"Chave '{keyText}' não encontrada na tabela de lookup.");
    }

    /// <summary>
    /// <c>lookup</c> com valor padrão (3º argumento): chave nula ou sem correspondência na tabela devolve o
    /// padrão, em vez de erro. Ex.: tabela só com as exceções (tomadores que retêm o ISS).
    /// </summary>
    public static object? LookupOrDefault(object? key, IReadOnlyDictionary<string, string> table, object? defaultValue)
    {
        if (key is null)
        {
            return defaultValue;
        }

        return table.TryGetValue(ToText(key), out var mapped) ? mapped : defaultValue;
    }

    private static bool ValueEquals(object? a, object? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        // Datas primeiro: textos ISO de precisão ou fuso diferentes comparam como instante.
        if (ScalarValues.TryToDate(a, out var dateA) && ScalarValues.TryToDate(b, out var dateB))
        {
            return dateA == dateB;
        }

        if (a.Equals(b))
        {
            return true;
        }

        return ScalarValues.TryToDecimal(a, out var da) && ScalarValues.TryToDecimal(b, out var db) && da == db;
    }

    private static int Compare(object? a, object? b, string operatorName)
    {
        if (a is null || b is null)
        {
            throw new RuleOperatorException("COMPARISON_NULL_OPERAND", $"'{operatorName}' não aceita operando nulo.");
        }

        if (ScalarValues.TryToDecimal(a, out var da) && ScalarValues.TryToDecimal(b, out var db))
        {
            return da.CompareTo(db);
        }

        if (ScalarValues.TryToDate(a, out var dateA) && ScalarValues.TryToDate(b, out var dateB))
        {
            return dateA.CompareTo(dateB);
        }

        // Texto × texto (inclusive data ISO × texto que não é data): ordinal.
        if (a is string textA && b is string textB)
        {
            return string.CompareOrdinal(textA, textB);
        }

        throw new RuleOperatorException(
            "COMPARISON_INCOMPATIBLE_TYPES", $"'{operatorName}' não sabe comparar '{ScalarValues.DescribeKind(a)}' com '{ScalarValues.DescribeKind(b)}'.");
    }

    private static string RequireString(object? value, string operatorName) => value switch
    {
        string s => s,
        null => throw new RuleOperatorException("EXPECTED_STRING", $"'{operatorName}' espera texto, recebeu nulo."),
        _ => throw new RuleOperatorException("EXPECTED_STRING", $"'{operatorName}' espera texto, recebeu '{ScalarValues.DescribeKind(value)}'."),
    };

    private static int RequireInt(object? value, string operatorName) => value switch
    {
        decimal d => checked((int)d),
        _ => throw new RuleOperatorException(
            "EXPECTED_INTEGER", $"'{operatorName}' espera um número inteiro, recebeu '{ScalarValues.DescribeKind(value)}'."),
    };

    private static char RequirePadChar(object? value) => value switch
    {
        null => ' ',
        string { Length: 1 } s => s[0],
        _ => throw new RuleOperatorException("EXPECTED_CHAR", $"Caractere de preenchimento inválido: '{value}'."),
    };

    // Objeto/lista não têm texto: erro de regra com código estável, em vez de
    // um nome de tipo CLR no XML.
    private static string ToText(object? value) =>
        ScalarValues.TryToText(value, out var text)
            ? text
            : throw new RuleOperatorException(
                "EXPECTED_SCALAR",
                $"Esperado valor escalar (texto, número, data ou booleano), recebeu '{ScalarValues.DescribeKind(value)}'.");
}

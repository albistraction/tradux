using System.Globalization;
using System.Text.Json.Nodes;
using Tradux.Engine.Operators;
using Tradux.Xsd;

namespace Tradux.Application.Quality;

/// <summary>Valor a usar num caso: um nó JSON (inclusive <c>null</c>) ou "remover o campo".</summary>
public sealed record SyntheticValue(JsonNode? Value, bool Remove = false)
{
    public static SyntheticValue Absent { get; } = new((JsonNode?)null, Remove: true);

    // Cast explícito: "new(null)" escolheria o construtor de cópia do record.
    public static SyntheticValue Null { get; } = new((JsonNode?)null);

    public static SyntheticValue Of(JsonNode? value) => new(value);
}

/// <summary>Violação planejada de uma restrição do campo (caso inválido esperado).</summary>
/// <param name="Restriction">Restrição violada (<c>maxLength</c>, <c>enumeration</c>, <c>obrigatorio</c>…).</param>
/// <param name="Value">Valor que viola a restrição (ou ausência).</param>
public sealed record InvalidVariant(string Restriction, SyntheticValue Value);

/// <summary>
/// Os valores sintéticos de um campo (ADR-0011 §3). <see cref="Typical"/> nulo
/// = não há valor válido gerável (nem vindo de cenário do autor): o campo
/// fica fora dos casos e, se alimenta obrigatório, a suíte reprova.
/// <see cref="Notes"/> = o que não pôde ser exercitado (vai para "não coberto").
/// </summary>
public sealed record FieldValues(
    SyntheticValue? Typical,
    SyntheticValue? Lower,
    SyntheticValue? Upper,
    IReadOnlyList<SyntheticValue> Enumeration,
    IReadOnlyList<InvalidVariant> Invalid,
    IReadOnlyList<string> Notes)
{
    public static FieldValues Unavailable(string reason) => new(null, null, null, [], [], [reason]);
}

/// <summary>Deriva valores determinísticos a partir do perfil do campo e das facetas do destino direto.</summary>
public static class SyntheticValues
{
    /// <summary>
    /// Teto de tamanho de texto gerado (revisão da Fase 8, B3): um
    /// <c>maxLength</c> enorme não pode virar centenas de MB por caso. Acima
    /// do teto, o limite fica "não coberto".
    /// </summary>
    public const int MaxTextLength = 4096;

    private const string TypicalDate = "2026-01-15";
    private const string TypicalYearMonth = "2026-01";
    private const string TypicalDateTime = "2026-01-15T10:30:00-03:00";

    /// <param name="field">Perfil do campo.</param>
    /// <param name="lookupTables">Tabelas DE-PARA do template.</param>
    /// <param name="seed">Valor do campo num cenário funcional do autor, usado quando nada válido é gerável.</param>
    public static FieldValues For(
        SourceField field, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> lookupTables, JsonNode? seed = null)
    {
        var values = field.Element is { } element
            ? ForElement(field, element, lookupTables)
            : ForExpression(field, lookupTables);

        if (values.Typical is null && seed is not null)
        {
            var fromScenario = SyntheticValue.Of(seed.DeepClone());
            return new FieldValues(fromScenario, fromScenario, fromScenario, [], [],
                [.. values.Notes, "Valor não gerável; usado o valor do cenário funcional do autor (limites e inválidos não exercitados)."]);
        }

        return values;
    }

    // Só em expressões/lookup: valor típico compatível com o uso, sem limites.
    private static FieldValues ForExpression(SourceField field, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> lookupTables)
    {
        var typical = LookupKey(field, lookupTables) ?? FromHints(field.Hints);
        return new FieldValues(typical, typical, typical, [], [], []);
    }

    private static SyntheticValue? LookupKey(SourceField field, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> tables) =>
        LookupKeys(field, tables).FirstOrDefault() is { } key ? Text(key) : null;

    private static IEnumerable<string> LookupKeys(SourceField field, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> tables) =>
        field.LookupTables.SelectMany(t => tables.TryGetValue(t, out var rows) ? rows.Keys.Order(StringComparer.Ordinal) : Enumerable.Empty<string>());

    private static SyntheticValue FromHints(IReadOnlyCollection<RuleArgumentKind> hints) =>
        hints.Contains(RuleArgumentKind.Date) ? Text(TypicalDate)
        : hints.Contains(RuleArgumentKind.Boolean) ? SyntheticValue.Of(JsonValue.Create(true))
        : hints.Contains(RuleArgumentKind.Number) || hints.Contains(RuleArgumentKind.Integer) ? SyntheticValue.Of(JsonValue.Create(1m))
        : Text("1"); // dígito: casa com o maior número de usos (texto, número, pattern numérico)

    private static FieldValues ForElement(SourceField field, SchemaElement element, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> lookupTables)
    {
        var facets = element.Facets;
        var values = element.ScalarType switch
        {
            SchemaScalarType.Decimal => DecimalValues(facets),
            SchemaScalarType.Date => TemporalValues(facets, TypicalDate, "2026-13-45"),
            SchemaScalarType.YearMonth => TemporalValues(facets, TypicalYearMonth, "2026-13"),
            SchemaScalarType.DateTime => TemporalValues(facets, TypicalDateTime, "2026-13-45T25:61:00"),
            _ => StringValues(facets),
        };

        // Campo direto que também é chave de DE-PARA (sem enumeração, que é
        // exercitada valor a valor para expor lacuna da tabela): o valor
        // precisa existir na tabela (revisão da Fase 8, W1).
        if (field.LookupTables.Count > 0 && facets.Enumeration.Count == 0 && values.Typical is not null)
        {
            var key = LookupKeys(field, lookupTables).FirstOrDefault(k => Fits(k, element));
            values = key is null
                ? FieldValues.Unavailable("Nenhuma chave da tabela DE-PARA satisfaz as restrições do destino.")
                : values with { Typical = Text(key), Lower = Text(key), Upper = Text(key), Notes = [.. values.Notes, "Chave de DE-PARA: limites não exercitados."] };
        }

        if (field.InvalidEligible && values.Typical is not null)
        {
            var invalid = values.Invalid.ToList();

            // Obrigatório sem default e sem xsi:nil: ausente ou nulo não tem como
            // virar XML válido (nillable obrigatório vira xsi:nil — é válido).
            if (field.Direct.Any(d => d.Required && d.Element.DefaultValue is null && d.Element.Nillability == Domain.Schema.Nillability.NotNillable))
            {
                invalid.Add(new InvalidVariant("obrigatorio-ausente", SyntheticValue.Absent));
                invalid.Add(new InvalidVariant("obrigatorio-nulo", SyntheticValue.Null));
            }

            return values with { Invalid = invalid };
        }

        return values with { Invalid = [] };
    }

    private static bool Fits(string value, SchemaElement element)
    {
        var f = element.Facets;
        var lower = f.Length ?? f.MinLength ?? 0;
        var upper = f.Length ?? f.MaxLength;
        try
        {
            return element.ScalarType != SchemaScalarType.String
                || (value.Length >= lower && (upper is null || value.Length <= upper) && PatternValueGenerator.Matches(value, f.PatternSteps));
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- texto

    private static FieldValues StringValues(ScalarFacets facets)
    {
        var invalid = new List<InvalidVariant>();
        var notes = new List<string>();
        if (facets.Enumeration.Count > 0)
        {
            var enumeration = facets.Enumeration.Select(Text).ToList();
            const string outside = "ZZ-FORA-DA-LISTA";
            if (!facets.Enumeration.Contains(outside))
            {
                invalid.Add(new InvalidVariant("enumeration", Text(outside)));
            }

            return new FieldValues(enumeration[0], enumeration[0], enumeration[0], enumeration, invalid, notes);
        }

        var lower = facets.Length ?? Math.Max(facets.MinLength ?? 1, 1);
        var upper = facets.Length ?? facets.MaxLength;
        if (lower > MaxTextLength)
        {
            return FieldValues.Unavailable($"Tamanho mínimo {lower} acima do teto de geração ({MaxTextLength}).");
        }

        int? generationUpper = upper;
        if (upper > MaxTextLength)
        {
            generationUpper = MaxTextLength;
            notes.Add($"Limite máximo {upper} acima do teto de geração ({MaxTextLength}): exercitado até {MaxTextLength}.");
        }

        string low, high, typical;
        if (facets.PatternSteps.Count > 0)
        {
            var candidates = new List<string>();
            string? reason = null;
            foreach (var pattern in facets.PatternSteps[0])
            {
                foreach (var maximal in new[] { false, true })
                {
                    try
                    {
                        candidates.Add(PatternValueGenerator.Generate(pattern, maximal));
                    }
                    catch (UnsupportedPatternException ex)
                    {
                        reason = ex.Message;
                    }
                }
            }

            List<string> valid;
            bool mismatchMatches;
            try
            {
                valid = candidates.Distinct().Where(c => FitsLength(c, lower, upper) && PatternValueGenerator.Matches(c, facets.PatternSteps))
                    .OrderBy(c => c.Length).ThenBy(c => c, StringComparer.Ordinal).ToList();
                mismatchMatches = PatternValueGenerator.Matches("#", facets.PatternSteps);
            }
            catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
            {
                // Pattern com retrocesso catastrófico (ex.: "(a*)*b"): "não coberto",
                // nunca erro 500 (3ª revisão da Fase 8, W9).
                return FieldValues.Unavailable("Pattern com retrocesso excessivo: verificação de valores estourou o tempo.");
            }

            if (valid.Count == 0)
            {
                return FieldValues.Unavailable(reason ?? "Nenhum valor gerado casa com todos os patterns e tamanhos do destino.");
            }

            low = typical = valid[0];
            high = valid[^1];
            if (generationUpper is { } u && high.Length < u)
            {
                notes.Add($"Pattern + tamanho: limite máximo exercitado com {high.Length} de {u} caracteres.");
            }

            if (!mismatchMatches)
            {
                invalid.Add(new InvalidVariant("pattern", Text("#")));
            }
        }
        else
        {
            low = new string('1', lower);
            high = new string('1', generationUpper ?? Math.Max(lower, 3));
            var typicalLength = Math.Max(lower, Math.Min(3, generationUpper ?? int.MaxValue));
            typical = new string('1', typicalLength);
        }

        if (upper is { } max && max < MaxTextLength)
        {
            invalid.Add(new InvalidVariant(facets.Length is not null ? "length" : "maxLength", Text(new string('1', max + 1))));
        }

        if (lower >= 2)
        {
            invalid.Add(new InvalidVariant(facets.Length is not null ? "length" : "minLength", Text(new string('1', lower - 1))));
        }

        return new FieldValues(Text(typical), Text(low), Text(high), [], invalid, notes);
    }

    private static bool FitsLength(string value, int lower, int? upper) => value.Length >= lower && (upper is null || value.Length <= upper);

    private static SyntheticValue Text(string value) => SyntheticValue.Of(JsonValue.Create(value));

    // ---------------------------------------------------------------- números

    private static FieldValues DecimalValues(ScalarFacets facets)
    {
        // Passo para limites exclusivos: sem fractionDigits, um passo pequeno —
        // passo 1 invertia faixas estreitas como (0.5, 1) (revisão da Fase 8, W1).
        var step = facets.FractionDigits is { } fd ? Pow10(-fd) : 0.0001m;
        decimal? lower = Dec(facets.MinInclusive) ?? Dec(facets.MinExclusive) + step;
        decimal? upper = Dec(facets.MaxInclusive) ?? Dec(facets.MaxExclusive) - step;
        if (facets.TotalDigits is { } td)
        {
            var cap = Pow10(Math.Min(td - (facets.FractionDigits ?? 0), 27)) - (facets.FractionDigits is { } f ? Pow10(-f) : 1m);
            upper = upper is { } u ? Math.Min(u, cap) : cap;
        }

        if (lower > upper)
        {
            return FieldValues.Unavailable("Faixa numérica do destino vazia para os dígitos permitidos.");
        }

        var invalid = new List<InvalidVariant> { new("tipo", Text("abc")) };
        if (facets.Enumeration.Count > 0)
        {
            var enumeration = facets.Enumeration.Select(v => Dec(v) is { } d ? Number(d) : Text(v)).ToList();
            return new FieldValues(enumeration[0], enumeration[0], enumeration[0], enumeration, invalid, []);
        }

        if (Dec(facets.MinInclusive) is not null || Dec(facets.MinExclusive) is not null)
        {
            invalid.Add(new InvalidVariant("minimo", Number(lower!.Value - step)));
        }

        if (Dec(facets.MaxInclusive) is not null || Dec(facets.MaxExclusive) is not null)
        {
            invalid.Add(new InvalidVariant("maximo", Number(upper!.Value + step)));
        }

        var typical = 1m >= (lower ?? decimal.MinValue) && 1m <= (upper ?? decimal.MaxValue) ? 1m : lower ?? upper ?? 1m;
        if (facets.FractionDigits is { } digits)
        {
            var extra = Math.Round(typical + Pow10(-(digits + 1)), digits + 1);
            if (extra >= (lower ?? decimal.MinValue) && extra <= (upper ?? decimal.MaxValue))
            {
                invalid.Add(new InvalidVariant("fractionDigits", Number(extra)));
            }
        }

        return new FieldValues(Number(typical), Number(lower ?? typical), Number(upper ?? typical), [], invalid, []);
    }

    private static SyntheticValue Number(decimal value) => SyntheticValue.Of(JsonValue.Create(value));

    private static decimal? Dec(string? text) =>
        text is not null && decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static decimal Pow10(int exponent)
    {
        var result = 1m;
        for (var i = 0; i < Math.Abs(exponent); i++)
        {
            result = exponent > 0 ? result * 10 : result / 10;
        }

        return result;
    }

    // ---------------------------------------------------------------- datas

    // Texto ISO de mesmo formato compara em ordem cronológica (ordinal).
    private static FieldValues TemporalValues(ScalarFacets facets, string typical, string invalidText)
    {
        var enumeration = facets.Enumeration.Select(Text).ToList();
        if (enumeration.Count > 0)
        {
            return new FieldValues(enumeration[0], enumeration[0], enumeration[0], enumeration, [new InvalidVariant("tipo", Text(invalidText))], []);
        }

        var value = typical;
        if (facets.MinInclusive is { } min && string.CompareOrdinal(value, min) < 0)
        {
            value = min;
        }

        if (facets.MaxInclusive is { } max && string.CompareOrdinal(value, max) > 0)
        {
            value = max;
        }

        if ((facets.MinExclusive is { } minEx && string.CompareOrdinal(value, minEx) <= 0)
            || (facets.MaxExclusive is { } maxEx && string.CompareOrdinal(value, maxEx) >= 0)
            || (facets.MinInclusive is { } lo && facets.MaxInclusive is { } hi && string.CompareOrdinal(lo, hi) > 0))
        {
            return FieldValues.Unavailable("Limites exclusivos de data: nenhum valor típico dentro da faixa.");
        }

        var notes = facets.MinInclusive is not null || facets.MaxInclusive is not null || facets.MinExclusive is not null || facets.MaxExclusive is not null
            ? new List<string> { "Limites de data: casos inválidos de mínimo/máximo não gerados." }
            : [];
        return new FieldValues(Text(value), Text(facets.MinInclusive ?? value), Text(facets.MaxInclusive ?? value), [],
            [new InvalidVariant("tipo", Text(invalidText))], notes);
    }
}

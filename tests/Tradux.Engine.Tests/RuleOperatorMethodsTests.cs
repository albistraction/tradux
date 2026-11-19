using Tradux.Engine.Operators;
using Xunit;

namespace Tradux.Engine.Tests;

/// <summary>
/// Testes diretos de cada operador do catálogo inicial (seção 13 do
/// requisito de origem) — um por categoria, mais casos de erro. O teste de
/// integração completo (compilação via Expression Trees + execução real)
/// fica em <see cref="RuleEngineGenerationTests"/>.
/// </summary>
public class RuleOperatorMethodsTests
{
    [Theory]
    [InlineData("2026-01-01", true)]
    [InlineData("2026-12-31", false)]
    [InlineData("2026-09", true)]
    public void Iso_texts_of_different_precision_compare_as_dates(string isoText, bool dateIsGreater)
    {
        Assert.Equal(dateIsGreater, RuleOperatorMethods.GreaterThan("2026-09-28", isoText));
        Assert.Equal(dateIsGreater, RuleOperatorMethods.LessThan(isoText, "2026-09-28T00:00:00"));
    }

    [Fact]
    public void Truncate_and_lookup_with_default()
    {
        Assert.Equal("abc", RuleOperatorMethods.Truncate("abcdef", 3m));
        Assert.Equal("ab", RuleOperatorMethods.Truncate("ab", 3m));
        Assert.Null(RuleOperatorMethods.Truncate(null, 3m));
        Assert.Throws<RuleOperatorException>(() => RuleOperatorMethods.Truncate("ab", -1m));

        var table = new Dictionary<string, string> { ["1"] = "um" };
        Assert.Equal("um", RuleOperatorMethods.LookupOrDefault(1m, table, "x"));
        Assert.Equal("x", RuleOperatorMethods.LookupOrDefault("2", table, "x"));
        Assert.Equal("x", RuleOperatorMethods.LookupOrDefault(null, table, "x"));
    }

    [Fact]
    public void Date_equals_its_iso_text_forms()
    {
        Assert.Equal(true, RuleOperatorMethods.AreEqual("2026-09-28", "2026-09-28T00:00:00"));
        Assert.Equal(true, RuleOperatorMethods.AreEqual("2026-09-28", "2026-09-28T00:00:00Z"));
        Assert.Equal(true, RuleOperatorMethods.AreEqual("2026-09-01", "2026-09"));
        Assert.Equal(false, RuleOperatorMethods.AreEqual("2026-09-28", "28/09/2026"));
    }

    [Fact]
    public void Date_time_with_offset_compares_as_the_instant()
    {
        // Revisão da Fase 5 (B1'): data/hora de emissão de documento fiscal vem com fuso.
        const string json = "2026-09-28T10:00:00-03:00";

        Assert.Equal(true, RuleOperatorMethods.GreaterThan(json, "2026-09-28T12:00:00Z"));
        Assert.Equal(true, RuleOperatorMethods.AreEqual(json, "2026-09-28T13:00:00Z"));
        Assert.Equal(false, RuleOperatorMethods.AreEqual("2026-09-28T10:00:00Z", json));
    }

    [Fact]
    public void Text_producing_operators_see_the_iso_text_of_a_date()
    {
        // Revisão da Fase 5 (B1''): formatDate, operadores de texto e lookup.
        var table = new Dictionary<string, string> { ["2026-09-28"] = "ok" };

        Assert.Equal("28/09/2026", RuleOperatorMethods.FormatDate("2026-09-28", "dd/MM/yyyy"));
        Assert.Equal("10:00 -03:00", RuleOperatorMethods.FormatDate("2026-09-28T10:00:00-03:00", "HH:mm zzz"));
        Assert.Equal("2026", RuleOperatorMethods.Substring("2026-09-28", 0m, 4m));
        Assert.Equal("20260928", RuleOperatorMethods.Replace("2026-09-28", "-", ""));
        Assert.Equal("2026-09-28T10:00:00-03:00", RuleOperatorMethods.Concat(["2026-09-28T10:00:00-03:00"]));
        Assert.Equal("ok", RuleOperatorMethods.Lookup("2026-09-28", table));
    }

    [Fact]
    public void Numbers_in_text_never_take_a_thousands_separator()
    {
        // Mesma regra da emissão do xs:decimal: "1500,50" não pode virar 150050 em silêncio.
        Assert.Equal(false, RuleOperatorMethods.AreEqual("1,500", 1500m));
        Assert.Equal("FORMAT_NUMBER_INVALID_TYPE",
            Assert.Throws<RuleOperatorException>(() => RuleOperatorMethods.FormatNumber("1500,50", "0.00")).Code);
        Assert.Equal("1500.50", RuleOperatorMethods.FormatNumber("1500.5", "0.00"));
        Assert.Equal(true, RuleOperatorMethods.GreaterThan("-2.5", -3m));
    }

    [Fact]
    public void FormatDate_rejects_text_that_is_not_an_iso_date()
    {
        var ex = Assert.Throws<RuleOperatorException>(() => RuleOperatorMethods.FormatDate("28/09/2026", "yyyy"));
        Assert.Equal("FORMAT_DATE_INVALID_TYPE", ex.Code);
    }

    [Fact]
    public void Date_against_non_iso_text_compares_as_text()
    {
        Assert.Equal(true, RuleOperatorMethods.GreaterThan("2026-09-28", "1"));
        Assert.Equal(true, RuleOperatorMethods.LessThan("2026-09-28", "ontem"));
    }

    [Fact]
    public void Non_scalar_values_are_rule_errors_not_clr_type_names()
    {
        // B8: objeto/lista em operador de texto → erro com código estável.
        var nested = new Dictionary<string, object?> { ["A"] = 1m };
        var ex = Assert.Throws<RuleOperatorException>(() => RuleOperatorMethods.Concat(["x", nested]));
        Assert.Equal("EXPECTED_SCALAR", ex.Code);
        Assert.DoesNotContain("System.", ex.Message);
        Assert.Equal("EXPECTED_SCALAR", Assert.Throws<RuleOperatorException>(() => RuleOperatorMethods.Concat([new List<object?> { 1m }])).Code);
    }

    [Fact]
    public void AreEqual_compares_numbers_and_their_text()
    {
        Assert.Equal(true, RuleOperatorMethods.AreEqual(10m, "10"));
        Assert.Equal(true, RuleOperatorMethods.AreEqual(10m, 10.00m));
        Assert.Equal(false, RuleOperatorMethods.AreEqual(10m, "11"));
        Assert.Equal(true, RuleOperatorMethods.AreEqual(null, null));
        Assert.Equal(false, RuleOperatorMethods.AreEqual(null, 1m));
    }

    [Fact]
    public void NotEquals_is_the_negation_of_AreEqual()
    {
        Assert.Equal(true, RuleOperatorMethods.NotEquals("a", "b"));
        Assert.Equal(false, RuleOperatorMethods.NotEquals("a", "a"));
    }

    [Fact]
    public void GreaterThan_and_LessThan_compare_dates()
    {
        const string earlier = "2026-01-01";
        const string later = "2026-12-31";

        Assert.Equal(true, RuleOperatorMethods.GreaterThan(later, earlier));
        Assert.Equal(true, RuleOperatorMethods.LessThan(earlier, later));
    }

    [Fact]
    public void Comparison_with_null_operand_throws()
    {
        var ex = Assert.Throws<RuleOperatorException>(() => RuleOperatorMethods.GreaterThan(null, 1m));
        Assert.Equal("COMPARISON_NULL_OPERAND", ex.Code);
    }

    [Fact]
    public void Comparison_with_incompatible_types_throws()
    {
        var ex = Assert.Throws<RuleOperatorException>(() => RuleOperatorMethods.GreaterThan("a", 1m));
        Assert.Equal("COMPARISON_INCOMPATIBLE_TYPES", ex.Code);
    }

    [Fact]
    public void IsEmpty_treats_null_and_empty_string_as_empty()
    {
        Assert.Equal(true, RuleOperatorMethods.IsEmpty(null));
        Assert.Equal(true, RuleOperatorMethods.IsEmpty(""));
        Assert.Equal(false, RuleOperatorMethods.IsEmpty("a"));
        Assert.Equal(false, RuleOperatorMethods.NotEmpty(null));
        Assert.Equal(true, RuleOperatorMethods.NotEmpty("a"));
    }

    [Fact]
    public void And_Or_Not_implement_boolean_logic()
    {
        Assert.Equal(true, RuleOperatorMethods.And([true, true, true]));
        Assert.Equal(false, RuleOperatorMethods.And([true, false]));
        Assert.Equal(true, RuleOperatorMethods.Or([false, false, true]));
        Assert.Equal(false, RuleOperatorMethods.Or([false, false]));
        Assert.Equal(false, RuleOperatorMethods.Not(true));
    }

    [Fact]
    public void Boolean_operators_reject_non_boolean_operands()
    {
        var ex = Assert.Throws<RuleOperatorException>(() => RuleOperatorMethods.Not("nao-e-bool"));
        Assert.Equal("EXPECTED_BOOLEAN", ex.Code);
    }

    [Fact]
    public void Copy_returns_the_value_unchanged()
    {
        Assert.Equal("x", RuleOperatorMethods.Copy("x"));
        Assert.Null(RuleOperatorMethods.Copy(null));
    }

    [Fact]
    public void Coalesce_returns_first_non_null()
    {
        Assert.Equal("b", RuleOperatorMethods.Coalesce([null, "b", "c"]));
        Assert.Null(RuleOperatorMethods.Coalesce([null, null]));
    }

    [Fact]
    public void SetDefault_falls_back_only_when_value_is_null()
    {
        Assert.Equal("x", RuleOperatorMethods.SetDefault("x", "fallback"));
        Assert.Equal("fallback", RuleOperatorMethods.SetDefault(null, "fallback"));
    }

    [Fact]
    public void SetNull_and_Omit_are_distinct_sentinels()
    {
        Assert.Null(RuleOperatorMethods.SetNull());
        Assert.Same(OmitMarker.Instance, RuleOperatorMethods.Omit());
    }

    [Fact]
    public void Concat_formats_each_value_invariantly_and_treats_null_as_empty()
    {
        Assert.Equal("A10.50", RuleOperatorMethods.Concat(["A", 10.50m, null]));
    }

    [Fact]
    public void Substring_supports_2_and_3_argument_forms()
    {
        Assert.Equal("cde", RuleOperatorMethods.Substring("abcde", 2m, null));
        Assert.Equal("cd", RuleOperatorMethods.Substring("abcde", 2m, 2m));
    }

    [Fact]
    public void Substring_out_of_range_throws()
    {
        var ex = Assert.Throws<RuleOperatorException>(() => RuleOperatorMethods.Substring("abc", 10m, null));
        Assert.Equal("SUBSTRING_START_OUT_OF_RANGE", ex.Code);
    }

    [Fact]
    public void Replace_and_Trim_work_as_expected()
    {
        Assert.Equal("xyz", RuleOperatorMethods.Replace("abc", "abc", "xyz"));
        Assert.Equal("abc", RuleOperatorMethods.Trim("  abc  "));
    }

    [Fact]
    public void PadLeft_and_PadRight_use_space_by_default_and_accept_custom_char()
    {
        Assert.Equal("  ab", RuleOperatorMethods.PadLeft("ab", 4m, null));
        Assert.Equal("ab00", RuleOperatorMethods.PadRight("ab", 4m, "0"));
    }

    [Fact]
    public void FormatDate_and_FormatNumber_are_culture_invariant()
    {
        Assert.Equal("2026-09-28", RuleOperatorMethods.FormatDate("2026-09-28T23:00:00", "yyyy-MM-dd"));
        Assert.Equal("1234.50", RuleOperatorMethods.FormatNumber(1234.5m, "0.00"));
    }

    [Fact]
    public void Lookup_resolves_key_and_throws_for_unmapped_key()
    {
        var table = new Dictionary<string, string> { ["1"] = "Normal", ["2"] = "Cancelada" };

        Assert.Equal("Normal", RuleOperatorMethods.Lookup("1", table));

        var ex = Assert.Throws<RuleOperatorException>(() => RuleOperatorMethods.Lookup("9", table));
        Assert.Equal("LOOKUP_KEY_NOT_FOUND", ex.Code);
    }
}

/// <summary>Metadados do catálogo para o editor visual (Fase 6).</summary>
public class RuleOperatorMetadataTests
{
    [Fact]
    public void Every_default_operator_has_builtin_metadata_consistent_with_its_arity()
    {
        var described = RuleOperatorCatalog.CreateDefault().Describe();

        Assert.Equal(26, described.Count); // 24 da seção 13 + ifElse e truncate (Fase 21)
        foreach (var (definition, metadata) in described)
        {
            Assert.NotEqual("Customizado", metadata.Category);
            Assert.True(
                metadata.RepeatsLastParameter
                    ? definition.MaxArguments == int.MaxValue && metadata.Parameters.Count <= definition.MinArguments
                    : metadata.Parameters.Count == definition.MaxArguments,
                $"{definition.Name}: {metadata.Parameters.Count} parâmetros × aridade {definition.MinArguments}..{definition.MaxArguments}");
        }
    }

    [Fact]
    public void Builtin_metadata_and_default_catalog_list_the_same_operators()
    {
        Assert.Equal(
            RuleOperatorMetadata.BuiltIn.Keys.Order(StringComparer.Ordinal),
            RuleOperatorCatalog.CreateDefault().Describe().Select(d => d.Definition.Name));
    }

    [Fact]
    public void Custom_operator_without_metadata_gets_a_generic_description()
    {
        var catalog = new RuleOperatorCatalog();
        catalog.Register(new RuleOperatorDefinition("x", 1, 1, (_, args, _) => args[0]));

        var (_, metadata) = Assert.Single(catalog.Describe());

        Assert.Equal("Customizado", metadata.Category);
        Assert.Single(metadata.Parameters);
    }
}

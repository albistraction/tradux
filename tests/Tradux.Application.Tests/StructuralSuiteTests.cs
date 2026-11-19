using System.Text.Json;
using Tradux.Application.Quality;
using Tradux.Application.Snapshots;
using Tradux.Contracts.Quality;
using Tradux.Domain.Mapping;
using Tradux.Engine.Operators;
using Tradux.Tests.Shared;

namespace Tradux.Application.Tests;

/// <summary>Fase 8 (seção 19, ADR-0011): gerador de pattern, perfil de origem e suíte estrutural.</summary>
public class StructuralSuiteTests
{
    private static QualityReport RunSuite(SnapshotContent content)
    {
        var template = new SnapshotCompiler().CompileContent(content).Template;
        return StructuralTestRunner.Run(template, content.Mapping, content.LookupTables);
    }

    private static string Describe(QualityReport report) =>
        string.Join("\n", report.Cases.Where(c => !c.Passed).Select(c => $"{c.Name} [{c.Target}] {c.FailureCode} @ {c.DestinationXPath}"))
        + "\n" + string.Join("\n", report.NotCovered.Select(g => $"{g.Area} {g.Path}: {g.Reason}"));

    // ------------------------------------------------------------------ pattern

    [Theory]
    [InlineData(@"\d{14}", false, "11111111111111")]
    [InlineData(@"\d{2}\.\d{2}\.\d{2}", false, "11.11.11")]
    [InlineData(@"[A-Z]{2}\d{0,3}", false, "AA")]
    [InlineData(@"[A-Z]{2}\d{0,3}", true, "AA111")]
    [InlineData(@"(AB|CD)x?", false, "AB")]
    [InlineData(@"(AB|CD)x?", true, "CDx")]
    [InlineData(@"[^0-9]+", false, "A")]
    [InlineData(@"\d+-\w*", true, "111-AA")]
    public void Pattern_generator_produces_matching_min_and_max_values(string pattern, bool maximal, string expected)
    {
        var value = PatternValueGenerator.Generate(pattern, maximal);

        Assert.Equal(expected, value);
        Assert.True(PatternValueGenerator.Matches(value, [[pattern]]));
    }

    [Theory]
    [InlineData(@"\p{L}+")]
    [InlineData(@"[a-z-[aeiou]]")]
    [InlineData(@"\i\c*")]
    public void Pattern_outside_the_subset_is_reported_not_invented(string pattern)
    {
        Assert.Throws<UnsupportedPatternException>(() => PatternValueGenerator.Generate(pattern, false));
    }

    // ------------------------------------------------------------------ perfil

    [Fact]
    public void Profile_separates_direct_uses_from_expressions_predicates_and_lookups()
    {
        var content = SnapshotFixtures.SimpleDps(numeroPrefix: "C-");
        var schema = new SnapshotCompiler().CompileContent(content).Template.Xsd.Schema;

        var root = SourceProfiler.Build(schema, content.Mapping).Root;

        var cnpj = root.Fields["CnpjPrestador"];
        Assert.True(cnpj.InvalidEligible);
        Assert.True(cnpj.Direct.Single().Required);
        Assert.False(root.Fields["CnpjTomador"].Direct.Single().Required);

        var numero = root.Fields["Numero"];
        Assert.True(numero.Transformed);
        Assert.False(numero.Constrained); // concat("C-", Numero): a restrição do destino não vale para a entrada
        Assert.True(numero.FeedsRequired);

        var situacao = root.Fields["CodigoSituacao"];
        Assert.True(situacao.Constrained);
        Assert.False(situacao.InvalidEligible); // chave de lookup e condição
        Assert.Equal(["situacoes"], situacao.LookupTables);
        Assert.True(situacao.InPredicate);
    }

    [Fact]
    public void Profile_follows_collections_into_the_item_scope()
    {
        var content = SnapshotFixtures.PedidoComItens();
        var schema = new SnapshotCompiler().CompileContent(content).Template.Xsd.Schema;

        var root = SourceProfiler.Build(schema, content.Mapping).Root;

        var itens = root.Collections["Itens"];
        Assert.Equal((0, (int?)null), (itens.MinOccurs, itens.MaxOccurs));
        Assert.True(itens.Item.Fields["ValorUnitario"].InPredicate); // filtro ValorUnitario > 0
        Assert.True(itens.Item.Fields["Descricao"].Transformed);
        Assert.Contains(RuleArgumentKind.Any, itens.Item.Fields["Descricao"].Hints.Append(RuleArgumentKind.Any));
        Assert.DoesNotContain("$index", itens.Item.Fields.Keys);
    }

    // ------------------------------------------------------------------ suíte

    [Fact]
    public void Suite_covers_every_category_the_contract_allows_and_the_reference_template_passes()
    {
        var report = RunSuite(SnapshotFixtures.SimpleDps());

        Assert.True(report.Passed, Describe(report));
        var categories = report.Cases.Select(c => c.Category).ToHashSet();
        Assert.Superset(new HashSet<StructuralCategory>
        {
            StructuralCategory.Required, StructuralCategory.Complete, StructuralCategory.LowerBound, StructuralCategory.UpperBound,
            StructuralCategory.Enumeration, StructuralCategory.Null, StructuralCategory.Invalid,
        }, categories);

        // Um inválido esperado por restrição crítica de campo com uso direto.
        Assert.Contains(report.Cases, c => c.Name == "invalido:pattern:CnpjPrestador");
        Assert.Contains(report.Cases, c => c.Name == "invalido:obrigatorio-ausente:CnpjPrestador");
        Assert.Contains(report.Cases, c => c.Name == "invalido:maxLength:Serie");
        Assert.Contains(report.Cases, c => c.Name == "invalido:maximo:ValorServico");
        Assert.Contains(report.Cases, c => c.Name == "invalido:fractionDigits:ValorServico");
        Assert.Contains(report.Cases, c => c.Name == "invalido:tipo:DataEmissao");
        Assert.DoesNotContain(report.Cases, c => c.Name == "invalido:obrigatorio-ausente:CodigoSituacao"); // tem default no XSD
        Assert.DoesNotContain(report.Cases, c => c.Name == "invalido:obrigatorio-nulo:ValorDesconto"); // nillable
    }

    [Fact]
    public void Enumeration_values_are_each_exercised_and_expose_a_lookup_table_gap()
    {
        // XSD aceita situação 1, 2 e 3; a tabela DE-PARA só traduz 1 e 2.
        var content = SnapshotFixtures.SimpleDps();
        var tables = new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["situacoes"] = new Dictionary<string, string> { ["1"] = "Normal", ["2"] = "Cancelada" },
        };

        var report = RunSuite(new SnapshotContent
        {
            SchemaFiles = content.SchemaFiles, SchemaEntryPoint = content.SchemaEntryPoint, Mapping = content.Mapping, LookupTables = tables,
        });

        Assert.True(report.Cases.Count(c => !c.Passed) == 1, Describe(report));
        var failed = Assert.Single(report.Cases, c => !c.Passed);
        Assert.Equal(StructuralCategory.Enumeration, failed.Category);
        Assert.Equal("LOOKUP_KEY_NOT_FOUND", failed.FailureCode);
    }

    [Fact]
    public void Template_with_date_rules_lookups_and_conditions_passes_the_suite()
    {
        var report = RunSuite(SnapshotFixtures.SimpleDpsWithDateRule());

        Assert.True(report.Passed, Describe(report));
    }

    [Fact]
    public void Collections_are_exercised_empty_and_with_several_items()
    {
        var report = RunSuite(SnapshotFixtures.PedidoComItens());

        Assert.True(report.Passed, Describe(report));
        Assert.Contains(report.Cases, c => c.Name == "repeticao-vazia");
        Assert.Contains(report.Cases, c => c.Name == "repeticao-varios");
        Assert.Contains(report.Cases, c => c.Name == "invalido:tipo:ValorTotal");
    }

    [Fact]
    public void Template_that_can_produce_invalid_xml_from_valid_input_fails_the_suite()
    {
        // serie tem maxLength 5; o prefixo sozinho já estoura.
        var content = SnapshotFixtures.SimpleDps();
        var mapping = new MappingTemplate
        {
            Fields = [.. content.Mapping.Fields.Where(f => f.DestinationElementName != "serie"),
                new FieldMapping("serie", OperatorNode.Of("concat", new LiteralNode("SERIE-"), new FieldReferenceNode("Serie")))],
        };

        var report = RunSuite(new SnapshotContent
        {
            SchemaFiles = content.SchemaFiles, SchemaEntryPoint = content.SchemaEntryPoint, Mapping = mapping, LookupTables = content.LookupTables,
        });

        Assert.False(report.Passed);
        Assert.Contains(report.Cases, c => c.Name == "completo" && !c.Passed && c.DestinationXPath!.Contains("serie"));
    }

    [Fact]
    public void Choice_content_is_profiled_not_reported_as_unmappable()
    {
        var content = new SnapshotContent
        {
            SchemaFiles = new Dictionary<string, byte[]>
            {
                ["exemplo-composto.xsd"] = File.ReadAllBytes(SnapshotFixtures.SchemaPath("exemplo-composto.xsd")),
                ["exemplo-composto-tipos.xsd"] = File.ReadAllBytes(SnapshotFixtures.SchemaPath("exemplo-composto-tipos.xsd")),
            },
            SchemaEntryPoint = "exemplo-composto.xsd",
            Mapping = new MappingTemplate(),
        };
        var schema = Xsd.XsdSchemaImporter.ImportInMemory(content.SchemaFiles, content.SchemaEntryPoint).Schema;

        var profile = SourceProfiler.Build(schema, content.Mapping);

        Assert.DoesNotContain(profile.NotCovered, n => n.Area == "choice"); // Fase 15: choice é gerado (ADR-0018)
    }

    [Fact]
    public void Report_never_carries_the_generated_values()
    {
        var report = RunSuite(SnapshotFixtures.SimpleDps());

        var json = JsonSerializer.Serialize(report);
        Assert.DoesNotContain("11111111111111", json);
        Assert.DoesNotContain("ZZ-FORA-DA-LISTA", json);
        Assert.DoesNotContain("2026-01-15", json);
    }
}

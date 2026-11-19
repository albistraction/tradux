using System.Diagnostics;
using System.Text;
using Tradux.Application.Layers;
using Tradux.Application.Publishing;
using Tradux.Application.Quality;
using Tradux.Application.Snapshots;
using Tradux.Contracts;
using Tradux.Contracts.Quality;
using Tradux.Domain.Mapping;
using Tradux.Tests.Shared;

namespace Tradux.Application.Tests;

/// <summary>Gates e suíte estrutural: casos-limite que já escaparam.</summary>
public class QualityReviewRegressionTests
{
    private static readonly TemplateKey Key = new(SnapshotFixtures.Organization, "r", SnapshotFixtures.Operation);

    private static SnapshotContent Content(string elements, params FieldMapping[] fields) => new()
    {
        SchemaFiles = new Dictionary<string, byte[]>
        {
            ["r.xsd"] = Encoding.UTF8.GetBytes($$"""
                <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:t="urn:r" targetNamespace="urn:r" elementFormDefault="qualified">
                  <xs:element name="R"><xs:complexType><xs:sequence>{{elements}}</xs:sequence></xs:complexType></xs:element>
                </xs:schema>
                """),
        },
        SchemaEntryPoint = "r.xsd",
        Mapping = new MappingTemplate { Fields = fields },
    };

    private static string Restricted(string name, string restriction, string @base = "xs:string") =>
        $"""<xs:element name="{name}"><xs:simpleType><xs:restriction base="{@base}">{restriction}</xs:restriction></xs:simpleType></xs:element>""";

    private static QualityReport Suite(SnapshotContent content, IReadOnlyList<System.Text.Json.JsonElement>? seeds = null)
    {
        var template = new SnapshotCompiler().CompileContent(content).Template;
        return StructuralTestRunner.Run(template, content.Mapping, content.LookupTables, seeds);
    }

    private static string Describe(QualityReport report) =>
        string.Join("\n", report.Cases.Where(c => !c.Passed).Select(c => $"{c.Name} {c.FailureCode} @ {c.DestinationXPath}"));

    [Fact]
    public async Task Content_publication_always_runs_the_structural_and_functional_gates()
    {
        var content = Content(Restricted("a", """<xs:maxLength value="3"/>"""),
            new FieldMapping("a", OperatorNode.Of("concat", new LiteralNode("TOOLONG"), new FieldReferenceNode("A"))));
        var service = new ContentPublicationService(new InMemorySnapshotRepository(), new SnapshotCompiler(), new InMemoryAuditSink());

        var result = await service.PublishAsync(Key, content, [], "x");

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Code == StructuralTestGate.StructuralTestFailedCode);
        Assert.Contains(result.Diagnostics, d => d.Code == FunctionalScenarioGate.ScenarioRequiredCode);
    }

    [Fact]
    public void A_field_without_generable_value_does_not_switch_the_suite_off()
    {
        // "letras" tem pattern fora do subconjunto; "desc" continua exercitado e expõe a lacuna da tabela.
        var content = Content(
            Restricted("letras", """<xs:pattern value="\p{L}+"/>""") +
            Restricted("situacao", """<xs:enumeration value="1"/><xs:enumeration value="2"/><xs:enumeration value="3"/>""") +
            """<xs:element name="desc" type="xs:string"/>""",
            FieldMapping.Direct("Letras", "letras"),
            FieldMapping.Direct("Situacao", "situacao"),
            new FieldMapping("desc", OperatorNode.Of("lookup", new FieldReferenceNode("Situacao"), new LiteralNode("t")))) with
        {
            LookupTables = new Dictionary<string, IReadOnlyDictionary<string, string>> { ["t"] = new Dictionary<string, string> { ["1"] = "a", ["2"] = "b" } },
        };

        var report = Suite(content);

        Assert.False(report.Passed);
        Assert.Contains(report.Cases, c => c.FailureCode == StructuralTestRunner.ValueUnavailableCode && c.Target == "Letras");
        Assert.Contains(report.Cases, c => c.FailureCode == "LOOKUP_KEY_NOT_FOUND");
        Assert.Contains(report.NotCovered, g => g.Path == "Letras");
    }

    [Fact]
    public void The_author_scenario_supplies_a_value_the_generator_cannot()
    {
        var content = Content(Restricted("letras", """<xs:pattern value="\p{L}+"/>"""), FieldMapping.Direct("Letras", "letras"));

        var report = Suite(content, [SnapshotFixtures.Json("""{ "Letras": "abc" }""")]);

        Assert.True(report.Passed, Describe(report));
        Assert.Contains(report.NotCovered, g => g.Reason.Contains("cenário funcional"));
    }

    [Fact]
    public void Huge_lengths_and_nested_repetitions_stay_within_the_generation_ceiling()
    {
        var content = Content(
            Restricted("grande", """<xs:maxLength value="2147483647"/>""") +
            Restricted("aninhado", """<xs:pattern value="((\d{100}){100}){1000}"/>""") + "",
            FieldMapping.Direct("Grande", "grande"),
            FieldMapping.Direct("Aninhado", "aninhado"));
        var before = GC.GetTotalAllocatedBytes(precise: true);
        var watch = Stopwatch.StartNew();

        var report = Suite(content);

        Assert.True(GC.GetTotalAllocatedBytes(precise: true) - before < 100_000_000, "suíte alocou mais de 100 MB");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20));
        var failed = Assert.Single(report.Cases, c => !c.Passed);
        Assert.Equal((StructuralTestRunner.ValueUnavailableCode, "Aninhado"), (failed.FailureCode, failed.Target));
        Assert.Contains(report.NotCovered, g => g.Path == "Grande" && g.Reason.Contains("teto"));
        Assert.Throws<UnsupportedPatternException>(() => PatternValueGenerator.Generate(@"((\d{100}){100}){1000}", false));
    }

    [Theory]
    [InlineData("(a?){2000000000}")]
    [InlineData("((a?){2000000000}){2000000000}")]
    [InlineData("(a?){2000000000}b")]
    public void Repetition_of_empty_content_is_bounded_in_cpu_not_only_memory(string pattern)
    {
        var watch = Stopwatch.StartNew();

        Assert.Throws<UnsupportedPatternException>(() => PatternValueGenerator.Generate(pattern, false));
        Assert.Throws<UnsupportedPatternException>(() => PatternValueGenerator.Generate(pattern, true));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"levou {watch.Elapsed}");
    }

    [Fact]
    public async Task Content_that_cannot_be_normalized_is_a_refusal_with_audit()
    {
        var audit = new InMemoryAuditSink();
        var service = new ContentPublicationService(new InMemorySnapshotRepository(), new SnapshotCompiler(), audit);
        var content = Content("""<xs:element name="a" type="xs:string"/>""", new FieldMapping("a", new LiteralNode(Guid.NewGuid())));

        var result = await service.PublishAsync(Key, content, SnapshotFixtures.DpsScenarios(), "x");

        Assert.False(result.Success);
        Assert.Contains(audit.Events, e => e.Outcome == "rejected");
    }

    [Fact]
    public void Catastrophic_backtracking_pattern_is_refused_at_import()
    {
        var content = new SnapshotContent
        {
            SchemaFiles = new Dictionary<string, byte[]>
            {
                ["r.xsd"] = Encoding.UTF8.GetBytes("""
                    <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:t="urn:r" targetNamespace="urn:r" elementFormDefault="qualified">
                      <xs:simpleType name="Base"><xs:restriction base="xs:string"><xs:pattern value="(a*)*b"/></xs:restriction></xs:simpleType>
                      <xs:simpleType name="Derivado"><xs:restriction base="t:Base"><xs:pattern value="a{30}"/></xs:restriction></xs:simpleType>
                      <xs:element name="R"><xs:complexType><xs:sequence><xs:element name="v" type="t:Derivado" minOccurs="0"/></xs:sequence></xs:complexType></xs:element>
                    </xs:schema>
                    """),
            },
            SchemaEntryPoint = "r.xsd",
            Mapping = new MappingTemplate { Fields = [FieldMapping.Direct("V", "v")] },
        };

        // Fase 9: o formato de retrocesso exponencial é recusado já na importação
        // (o validador XSD do .NET não tem timeout); o gerador segue protegido
        // pelo timeout de regex como segunda barreira.
        var ex = Assert.Throws<System.Xml.Schema.XmlSchemaException>(() =>
            Xsd.XsdSchemaImporter.ImportInMemory(content.SchemaFiles, content.SchemaEntryPoint));
        Assert.Contains("retrocesso exponencial", ex.Message);
    }

    [Fact]
    public void Facet_beyond_the_validator_range_is_an_import_error_not_a_crash()
    {
        var content = Content(Restricted("enorme", """<xs:maxLength value="99999999999"/>"""), FieldMapping.Direct("E", "enorme"));

        Assert.Throws<System.Xml.Schema.XmlSchemaException>(() =>
            Xsd.XsdSchemaImporter.ImportInMemory(content.SchemaFiles, content.SchemaEntryPoint));
    }

    [Fact]
    public void Min_length_above_the_typical_length_without_max_length_is_generated()
    {
        var content = Content(Restricted("a", """<xs:minLength value="5"/>"""), FieldMapping.Direct("A", "a"));

        var report = Suite(content);

        Assert.True(report.Passed, Describe(report));
        Assert.Contains(report.Cases, c => c.Name == "invalido:minLength:A" && c.Passed);
    }

    [Fact]
    public void Direct_field_that_is_also_a_lookup_key_uses_a_table_key()
    {
        var content = Content(Restricted("codigo", """<xs:maxLength value="5"/>""") + """<xs:element name="nome" type="xs:string"/>""",
            FieldMapping.Direct("Codigo", "codigo"),
            new FieldMapping("nome", OperatorNode.Of("lookup", new FieldReferenceNode("Codigo"), new LiteralNode("t")))) with
        {
            LookupTables = new Dictionary<string, IReadOnlyDictionary<string, string>> { ["t"] = new Dictionary<string, string> { ["AB"] = "x" } },
        };

        Assert.True(Suite(content).Passed, Describe(Suite(content)));
    }

    [Theory]
    [InlineData("""<xs:maxInclusive value="2020-12-31"/>""", "xs:date")]
    [InlineData("""<xs:minExclusive value="0.5"/><xs:maxExclusive value="1"/>""", "xs:decimal")]
    public void Bounds_of_dates_and_exclusive_decimals_are_respected(string restriction, string @base)
    {
        var content = Content(Restricted("v", restriction, @base), FieldMapping.Direct("V", "v"));

        var report = Suite(content);

        Assert.True(report.Passed, Describe(report));
    }
}

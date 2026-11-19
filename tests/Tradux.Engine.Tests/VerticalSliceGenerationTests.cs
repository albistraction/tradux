using System.Xml.Linq;
using Tradux.Engine.Diagnostics;
using Tradux.Domain.Mapping;
using Xunit;

namespace Tradux.Engine.Tests;

/// <summary>
/// Prova o gate da Fase 2 (docs/ROADMAP.md): "um objeto de entrada gera XML
/// válido sem strings concatenadas e com diagnóstico útil", usando
/// docs/schemas/exemplo-simples.xsd como fixture-alvo.
/// </summary>
public class VerticalSliceGenerationTests
{
    private static string FixturePath => RepositoryPaths.Combine("docs", "schemas", "exemplo-simples.xsd");

    private static readonly FieldMapping[] AllFieldMappings =
    [
        FieldMapping.Direct("Numero", "numero"),
        FieldMapping.Direct("Serie", "serie"),
        FieldMapping.Direct("DataEmissao", "dataEmissao"),
        FieldMapping.Direct("Competencia", "competencia"),
        FieldMapping.Direct("ValorServico", "valorServico"),
        FieldMapping.Direct("ValorDesconto", "valorDesconto"),
        FieldMapping.Direct("CodigoServico", "codigoServico"),
        FieldMapping.Direct("CnpjPrestador", "cnpjPrestador"),
        FieldMapping.Direct("CnpjTomador", "cnpjTomador"),
        FieldMapping.Direct("DiscriminacaoServico", "discriminacaoServico"),
        FieldMapping.Direct("CodigoSituacao", "codigoSituacao"),
    ];

    private static MappingTemplate TemplateWithout(params string[] destinationElementNamesToExclude)
    {
        var excluded = destinationElementNamesToExclude.ToHashSet();
        return new MappingTemplate
        {
            Fields = AllFieldMappings.Where(m => !excluded.Contains(m.DestinationElementName)).ToList(),
        };
    }

    private static readonly XNamespace Ns = "http://exemplo.ilustrativo.tradux/dps/v1";
    private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";

    [Fact]
    public void Realistic_input_generates_valid_xml_without_critical_diagnostics()
    {
        var template = EngineHarness.Compile(FixturePath, TemplateWithout());

        var result = EngineHarness.Generate(DpsSourceData.Realista(), template);

        Assert.True(result.Success);
        Assert.NotNull(result.Xml);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == EngineDiagnosticSeverity.Critical);

        var doc = XDocument.Parse(result.Xml!);
        Assert.Equal(Ns + "DPS", doc.Root!.Name);
        Assert.Equal("1.00", doc.Root.Attribute("versao")!.Value);
        Assert.Equal("000123", doc.Root.Element(Ns + "numero")!.Value);
        Assert.Equal("2026-09-28", doc.Root.Element(Ns + "dataEmissao")!.Value);
        Assert.Equal("2026-09", doc.Root.Element(Ns + "competencia")!.Value);
        Assert.Equal("1500.50", doc.Root.Element(Ns + "valorServico")!.Value);
    }

    [Fact]
    public void Decimal_with_more_places_than_the_schema_allows_is_rejected_never_silently_rounded()
    {
        // Achado da suíte estrutural da Fase 8: o formato fixo "0.00" fazia
        // 1500.505 virar "1500.51" (ou 0.0275 virar "0.03") sem aviso.
        var template = EngineHarness.Compile(FixturePath, TemplateWithout());
        var data = DpsSourceData.Realista();
        data.ValorServico = 1500.505m;

        var result = EngineHarness.Generate(data, template);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Severity == EngineDiagnosticSeverity.Critical && d.DestinationXPath!.Contains("valorServico"));
    }

    [Fact]
    public void Generated_xml_is_not_built_by_string_concatenation_it_is_a_real_xml_tree()
    {
        // Prova estrutural (não apenas textual) de "sem strings concatenadas":
        // o resultado deve ser re-parseável como árvore XML bem formada, com
        // elementos filhos reais navegáveis por nome qualificado — uma
        // concatenação ingênua de string tipicamente falha em casos como
        // escaping de caracteres especiais (testado abaixo).
        var template = EngineHarness.Compile(FixturePath, TemplateWithout());
        var input = DpsSourceData.Realista();
        input.DiscriminacaoServico = "Consultoria <especial> & \"aspas\"";

        var result = EngineHarness.Generate(input, template);

        Assert.True(result.Success);
        var doc = XDocument.Parse(result.Xml!);
        Assert.Equal(
            "Consultoria <especial> & \"aspas\"",
            doc.Root!.Element(Ns + "discriminacaoServico")!.Value);
    }

    [Fact]
    public void Nillable_required_field_with_null_value_is_emitted_as_xsi_nil()
    {
        var template = EngineHarness.Compile(FixturePath, TemplateWithout());
        var input = DpsSourceData.Realista();
        input.ValorDesconto = null;

        var result = EngineHarness.Generate(input, template);

        Assert.True(result.Success);
        var doc = XDocument.Parse(result.Xml!);
        var valorDesconto = doc.Root!.Element(Ns + "valorDesconto")!;
        Assert.Equal("true", valorDesconto.Attribute(Xsi + "nil")!.Value);
    }

    [Fact]
    public void Optional_field_with_null_value_is_omitted_not_emitted_empty()
    {
        var template = EngineHarness.Compile(FixturePath, TemplateWithout());
        var input = DpsSourceData.Realista();
        input.Serie = null;

        var result = EngineHarness.Generate(input, template);

        Assert.True(result.Success);
        var doc = XDocument.Parse(result.Xml!);
        Assert.Null(doc.Root!.Element(Ns + "serie"));
    }

    [Fact]
    public void Required_element_without_any_mapping_is_a_critical_compile_time_diagnostic()
    {
        var template = EngineHarness.Compile(FixturePath, TemplateWithout("numero"));

        var result = EngineHarness.Generate(DpsSourceData.Realista(), template);

        Assert.False(result.Success);
        Assert.Null(result.Xml);
        var diagnostic = Assert.Single(result.Diagnostics, d => d.Code == "MAPPING_MISSING_REQUIRED");
        Assert.Equal(EngineDiagnosticSeverity.Critical, diagnostic.Severity);
        Assert.Equal("/DPS/numero", diagnostic.DestinationXPath);
    }

    [Fact]
    public void Required_non_nillable_field_resolved_to_null_at_runtime_is_a_critical_diagnostic()
    {
        var template = EngineHarness.Compile(FixturePath, TemplateWithout());
        var input = DpsSourceData.Realista();
        input.Numero = null; // mapeado, mas o valor de origem é nulo

        var result = EngineHarness.Generate(input, template);

        Assert.False(result.Success);
        var diagnostic = Assert.Single(result.Diagnostics, d => d.Code == "REQUIRED_VALUE_MISSING");
        Assert.Equal(EngineDiagnosticSeverity.Critical, diagnostic.Severity);
        Assert.Equal("/DPS/numero", diagnostic.DestinationXPath);
        Assert.Equal("Numero", diagnostic.SourcePath);
        Assert.NotNull(diagnostic.Suggestion);
    }

    [Fact]
    public void Value_violating_schema_pattern_fails_xsd_validation_with_actionable_diagnostic()
    {
        // cnpjPrestador exige 14 dígitos (tns:Cnpj14Type); "123" viola o
        // pattern. RuleExecutor não modela facets de conteúdo (isso é
        // responsabilidade do XSD/XsdValidator), então essa falha só
        // aparece no estágio final de validação contra o schema exato.
        var template = EngineHarness.Compile(FixturePath, TemplateWithout());
        var input = DpsSourceData.Realista();
        input.CnpjPrestador = "123";

        var result = EngineHarness.Generate(input, template);

        Assert.False(result.Success);
        Assert.Null(result.Xml);
        var diagnostic = Assert.Single(result.Diagnostics, d => d.Code == "XSD_VALIDATION");
        Assert.Equal(EngineDiagnosticSeverity.Critical, diagnostic.Severity);
        Assert.Equal("/DPS/cnpjPrestador", diagnostic.DestinationXPath);
        Assert.NotNull(diagnostic.Suggestion);
        Assert.Contains("123", diagnostic.Message);
    }

    [Fact]
    public void Required_field_without_mapping_but_with_schema_default_uses_default_value()
    {
        var template = EngineHarness.Compile(FixturePath, TemplateWithout("codigoSituacao"));

        var result = EngineHarness.Generate(DpsSourceData.Realista(), template);

        Assert.True(result.Success);
        var doc = XDocument.Parse(result.Xml!);
        Assert.Equal("1", doc.Root!.Element(Ns + "codigoSituacao")!.Value);
        Assert.Contains(result.Diagnostics, d => d.Code == "DEFAULT_APPLIED" && d.Severity == EngineDiagnosticSeverity.Info);
    }
}

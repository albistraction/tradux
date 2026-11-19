using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Tradux.Application.Generation;
using Tradux.Application.Layers;
using Tradux.Application.Snapshots;
using Tradux.Contracts;
using Tradux.Contracts.Design;
using Tradux.Domain.Layers;
using Tradux.Tests.Shared;

using Tradux.Application.Publishing;
namespace Tradux.Application.Tests;

/// <summary>Fase 12 (ADR-0015): XML de origem (XSD de origem) → XML de destino, pelo mesmo motor.</summary>
public class SourceXmlInputTests
{
    private const string Org = "org-a";
    private const string Template = "dps-erp";
    private const string Op = "emissao";
    private static readonly XNamespace Dps = "http://exemplo.ilustrativo.tradux/dps/v1";

    private static readonly TemplateKey Key = new(Org, Template, Op);

    private static async Task<TestServices> PublishedAsync()
    {
        var services = new TestServices();
        await services.PublishAsync(Key, SnapshotFixtures.ErpToDps(), SnapshotFixtures.ErpScenarios(), "autor");
        return services;
    }

    private static Task<GenerationResult> TransformAsync(TestServices services, string xml, IReadOnlyList<int>? candidateVersions = null) =>
        services.Generation.TransformAsync(Key, Encoding.UTF8.GetBytes(xml), candidateVersions: candidateVersions);

    [Fact]
    public async Task Source_xml_is_validated_and_mapped_to_the_destination_xsd()
    {
        var services = await PublishedAsync();

        var result = await TransformAsync(services, SnapshotFixtures.ErpSourceXml());

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
        var dps = XDocument.Parse(result.Xml!).Root!;
        Assert.Equal("000123", dps.Element(Dps + "numero")!.Value);
        Assert.Equal("2026-09-28", dps.Element(Dps + "dataEmissao")!.Value);
        Assert.Equal("1500.50", dps.Element(Dps + "valorServico")!.Value);   // texto de conteúdo simples ($value)
        Assert.Equal("true", (string?)dps.Element(Dps + "valorDesconto")!.Attribute(XName.Get("nil", "http://www.w3.org/2001/XMLSchema-instance"))); // xsi:nil na origem → nil no destino
        Assert.Equal("98765432000188", dps.Element(Dps + "cnpjTomador")!.Value);
        Assert.Equal("1", dps.Element(Dps + "codigoSituacao")!.Value);       // atributo @situacao
        Assert.Equal("1", result.VersionUsed);
    }

    [Fact]
    public async Task Xml_and_the_equivalent_json_produce_the_same_xml()
    {
        var services = await PublishedAsync();
        var json = JsonSerializer.SerializeToElement(Xsd.SourceXmlReader.Read(
            Xsd.XsdSchemaImporter.ImportSourceInMemory(SnapshotFixtures.ErpSourceSchema().Files, "origem-exemplo.xsd"), SnapshotFixtures.ErpSourceXml(), GenerationLimits.Default.SourceXml));

        var fromXml = await TransformAsync(services, SnapshotFixtures.ErpSourceXml());
        var fromJson = await services.Generation.GenerateAsync(new GenerateXmlRequest(Org, Template, Op, json));

        Assert.True(fromJson.Success);
        Assert.Equal(fromXml.Xml, fromJson.Xml);
    }

    [Fact]
    public async Task Xml_outside_the_source_xsd_fails_with_position_and_nothing_is_generated()
    {
        var services = await PublishedAsync();
        var xml = SnapshotFixtures.ErpSourceXml().Replace("<Cnpj>12345678000199</Cnpj>", "<Cnpj>123</Cnpj>");

        var result = await TransformAsync(services, xml);

        Assert.False(result.Success);
        Assert.Null(result.Xml);
        Assert.Contains(result.Diagnostics, d => d.Code == XmlGenerationService.SourceXmlInvalidCode && d.Message.StartsWith("Linha ", StringComparison.Ordinal));
        Assert.Equal("1", Assert.Single(result.VersionsAttempted!).Version);
    }

    [Fact]
    public async Task Template_without_source_schema_refuses_xml_input_but_json_keeps_working()
    {
        var services = new TestServices();
        await services.PublishAsync(Key, SnapshotFixtures.SimpleDps(), SnapshotFixtures.DpsScenarios(), "autor");

        var result = await TransformAsync(services, SnapshotFixtures.ErpSourceXml());

        Assert.Contains(result.Diagnostics, d => d.Code == XmlGenerationService.SourceSchemaMissingCode);
        Assert.True((await services.Generation.GenerateAsync(new GenerateXmlRequest(Org, Template, Op, SnapshotFixtures.DpsJson()))).Success);
    }

    [Fact]
    public async Task Fallback_reads_the_xml_with_each_versions_own_source_schema()
    {
        var services = new TestServices();
        await services.PublishAsync(Key, SnapshotFixtures.SimpleDps(), SnapshotFixtures.DpsScenarios(), "autor"); // v1: sem origem
        await services.PublishAsync(Key, SnapshotFixtures.ErpToDps(), SnapshotFixtures.ErpScenarios(), "autor");  // v2: com origem

        var result = await TransformAsync(services, SnapshotFixtures.ErpSourceXml(), candidateVersions: [1, 2]);

        Assert.True(result.Success);
        Assert.Equal("2", result.VersionUsed);
        Assert.Contains(result.VersionsAttempted!, a => a.Version == "1" && a.Diagnostics.Any(d => d.Code == XmlGenerationService.SourceSchemaMissingCode));
    }

    [Fact]
    public async Task Xml_scenario_is_a_real_publication_gate_and_its_failure_never_echoes_values()
    {
        var services = new TestServices();
        var broken = SnapshotFixtures.ErpScenarios()[0] with { SourceXml = SnapshotFixtures.ErpSourceXml().Replace("98765432000188", "CNPJ-SECRETO") };

        var error = await services.Publication.PublishAsync(Key, SnapshotFixtures.ErpToDps(), [broken], "autor");

        Assert.False(error.Success);
        Assert.Contains(error.Diagnostics, d => d.Code == FunctionalScenarioGate.ScenarioFailedCode);
        Assert.DoesNotContain(error.Diagnostics, d => d.Message.Contains("CNPJ-SECRETO", StringComparison.Ordinal));
    }

    /// <summary>
    /// Hash de <see cref="HashSample"/> sem XSD de origem, igual ao de antes da Fase 12:
    /// snapshots já publicados não mudam de hash quando o conteúdo ganha um campo novo.
    /// </summary>
    private const string HashWithoutSourceSchema = "9cd9ff0df48a501d989620d088dcf3308f37c2b97c17c4221ce3235dada25562";

    private static SnapshotContent HashSample() => new()
    {
        SchemaFiles = new Dictionary<string, byte[]>
        {
            ["h.xsd"] = Encoding.UTF8.GetBytes("""<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:element name="R"><xs:complexType><xs:sequence><xs:element name="a" type="xs:string"/></xs:sequence></xs:complexType></xs:element></xs:schema>"""),
        },
        SchemaEntryPoint = "h.xsd",
        Mapping = new Domain.Mapping.MappingTemplate
        {
            Fields = [new Domain.Mapping.FieldMapping("a", Domain.Mapping.OperatorNode.Of("lookup", new Domain.Mapping.FieldReferenceNode("A"), new Domain.Mapping.LiteralNode("t")))],
        },
        LookupTables = new Dictionary<string, IReadOnlyDictionary<string, string>> { ["t"] = new Dictionary<string, string> { ["2"] = "dois", ["1"] = "um" } },
    };

    [Fact]
    public void Source_schema_enters_the_hash_only_when_present()
    {
        var content = HashSample();
        var (xsdHash, _) = new SnapshotCompiler().CompileContent(content);

        var withoutSource = SnapshotCompiler.ComputeContentHash(xsdHash, content);
        var withSource = SnapshotCompiler.ComputeContentHash(xsdHash, content with { SourceSchema = SnapshotFixtures.ErpSourceSchema() });

        Assert.Equal(HashWithoutSourceSchema, withoutSource);
        Assert.NotEqual(withoutSource, withSource);
    }

    [Fact]
    public async Task Base_layer_can_carry_the_source_schema_and_the_resolved_template_accepts_xml()
    {
        var services = new TestServices();
        var admin = services.Layered;
        var content = SnapshotFixtures.ErpToDps();
        var source = content.SourceSchema!;
        var layer = new NewLayerVersion
        {
            OrganizationId = Org, LayerId = "base-erp", Kind = LayerKind.Base,
            Schema = new LayerSchema(content.SchemaFiles, content.SchemaEntryPoint),
            SourceSchema = new LayerSchema(source.Files, source.EntryPoint),
            Overrides = LayerOverrides.FromMappingTemplate(content.Mapping, "DPS"),
        };
        var created = (await admin.CreateLayerVersionAsync(layer, "autor")).Layer;
        var withoutSource = (await admin.CreateLayerVersionAsync(layer with { LayerId = "base-json", SourceSchema = null }, "autor")).Layer;

        Assert.True((await admin.BindAndPublishAsync(Key, created.Ref, UpdatePolicy.Manual, SnapshotFixtures.ErpScenarios(), "autor")).Success);
        var result = await TransformAsync(services, SnapshotFixtures.ErpSourceXml());

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Code)));
        Assert.NotEqual(created.ContentHash, withoutSource.ContentHash);
        Assert.Equal(withoutSource.ContentHash, LayerHashing.Compute(LayerKind.Base, null, false, layer.Schema, layer.Overrides, layer.LookupOverrides));
        await Assert.ThrowsAsync<ArgumentException>(() => admin.CreateLayerVersionAsync(new NewLayerVersion
        {
            OrganizationId = Org, LayerId = "cliente", Kind = LayerKind.Client, Parent = created.Ref, SourceSchema = layer.SourceSchema,
        }, "autor"));
    }

    [Fact]
    public async Task Package_round_trip_keeps_the_source_schema_and_the_hash()
    {
        var a = await PublishedAsync();
        var package = (await a.Packages.ExportAsync(Key, null, "a"))!;

        var b = new TestServices();
        var imported = await b.Packages.ImportAsync(package, Key, "b");

        Assert.True(imported.HashPreserved);
        Assert.True((await TransformAsync(b, SnapshotFixtures.ErpSourceXml())).Success);
    }

    [Fact]
    public void Designer_describes_the_source_fields_and_tests_a_draft_with_xml_input()
    {
        var designer = new Design.DraftDesignService();
        var sourceUpload = SnapshotFixtures.UploadFile("origem-exemplo.xsd");

        var tree = designer.DescribeSource(sourceUpload);

        Assert.Equal("NotaErp", tree.RootElementName);
        var fields = tree.Root.Children!;
        Assert.Contains(fields, f => f.Path == "@situacao" && f.Type == "text");
        var valor = fields.Single(f => f.Name == "Servico").Children!.Single(f => f.Name == "Valor");
        Assert.Equal(["Servico.Valor.@moeda", "Servico.Valor.$value"], valor.Children!.Select(c => c.Path));
        var item = fields.Single(f => f.Name == "Itens").Children!.Single(f => f.Name == "Item");
        Assert.Equal("list", item.Type);
        Assert.Equal(["Descricao", "Quantidade", "ValorUnitario", "$index"], item.Item!.Children!.Select(c => c.Path));

        var content = SnapshotFixtures.ErpToDps();
        var draft = new DraftTestRequest(
            SnapshotFixtures.UploadFile("exemplo-simples.xsd"),
            content.Mapping, default) { SourceSchema = sourceUpload, SourceXml = SnapshotFixtures.ErpSourceXml() };
        var ok = designer.Test(draft);
        var invalid = designer.Test(draft with { SourceXml = SnapshotFixtures.ErpSourceXml().Replace("situacao=\"1\"", "situacao=\"9\"") });

        Assert.True(ok.Publishable, string.Join("; ", ok.CompileDiagnostics.Select(d => d.Code)));
        Assert.False(invalid.Publishable);
        Assert.Contains(invalid.CompileDiagnostics, d => d.Code == XmlGenerationService.SourceXmlInvalidCode);
        Assert.Throws<ArgumentException>(() => designer.Test(draft with { SourceSchema = null }));
    }
}

public class EmptySourceXmlTests
{
    [Fact]
    public async Task Empty_source_xml_is_an_invalid_input_diagnostic_not_a_crash()
    {
        var services = new TestServices();
        var key = new TemplateKey("org-a", "dps-erp", "emissao");
        await services.PublishAsync(key, SnapshotFixtures.ErpToDps(), SnapshotFixtures.ErpScenarios());

        var empty = await services.Generation.TransformAsync(key, []);
        var blank = await services.Generation.TransformAsync(key, " "u8.ToArray());

        Assert.All([empty, blank], result =>
        {
            Assert.False(result.Success);
            Assert.Contains(result.Diagnostics, d => d.Code == XmlGenerationService.SourceXmlInvalidCode);
        });
    }
}

/// <summary>Correção pós-Fase 12: destino com ds:Signature do xmldsig (conector externo assina).</summary>
public class DestinationSignatureEngineTests
{
    private const string Dsig = """
        <schema xmlns="http://www.w3.org/2001/XMLSchema" targetNamespace="http://www.w3.org/2000/09/xmldsig#" elementFormDefault="qualified">
          <element name="Signature"><complexType><sequence>
            <element name="SignatureValue"><complexType><simpleContent><extension base="base64Binary"><attribute name="Id" type="ID"/></extension></simpleContent></complexType></element>
            <element name="Object" minOccurs="0"><complexType mixed="true"><sequence><any namespace="##any" processContents="lax" maxOccurs="unbounded"/></sequence></complexType></element>
          </sequence></complexType></element>
        </schema>
        """;

    private static SnapshotContent Content(string signatureOccurs) => new()
    {
        SchemaFiles = new Dictionary<string, byte[]>
        {
            ["d.xsd"] = Encoding.UTF8.GetBytes($$"""
                <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:ds="http://www.w3.org/2000/09/xmldsig#" targetNamespace="urn:nota" xmlns="urn:nota" elementFormDefault="qualified">
                  <xs:import namespace="http://www.w3.org/2000/09/xmldsig#" schemaLocation="dsig.xsd"/>
                  <xs:element name="Nota"><xs:complexType><xs:sequence>
                    <xs:element name="Numero" type="xs:string"/>
                    <xs:element ref="ds:Signature" minOccurs="{{signatureOccurs}}"/>
                  </xs:sequence></xs:complexType></xs:element>
                </xs:schema>
                """),
            ["dsig.xsd"] = Encoding.UTF8.GetBytes(Dsig),
        },
        SchemaEntryPoint = "d.xsd",
        Mapping = new Tradux.Domain.Mapping.MappingTemplate { Fields = [Tradux.Domain.Mapping.FieldMapping.Direct("Numero", "Numero")] },
    };

    [Fact]
    public void Optional_unmapped_signature_is_omitted_and_the_xml_validates()
    {
        var (_, template) = new SnapshotCompiler().CompileContent(Content("0"));

        var outcome = Tradux.Engine.XmlGenerationEngine.Generate(template, Tradux.Domain.SourceData.SourceDataTree.FromJson(JsonDocument.Parse("""{"Numero":"1"}""").RootElement));

        Assert.True(outcome.Success, string.Join("; ", outcome.Diagnostics.Select(d => d.Code)));
        Assert.DoesNotContain("Signature", outcome.Xml, StringComparison.Ordinal);
    }

    [Fact]
    public void Required_signature_is_left_to_the_signing_connector()
    {
        // O conector assina depois da geração (ADR-0019): o DE-PARA não a exige e o XML antes
        // da assinatura é válido sem ela.
        var (_, template) = new SnapshotCompiler().CompileContent(Content("1"));

        Assert.DoesNotContain(template.CompileTimeDiagnostics, d => d.DestinationXPath?.EndsWith("/Signature", StringComparison.Ordinal) == true);
        var outcome = Tradux.Engine.XmlGenerationEngine.Generate(template, Tradux.Domain.SourceData.SourceDataTree.FromJson(JsonDocument.Parse("""{"Numero":"1"}""").RootElement));
        Assert.True(outcome.Success, string.Join("; ", outcome.Diagnostics.Select(d => d.Code)));
        Assert.DoesNotContain("Signature", outcome.Xml, StringComparison.Ordinal);
    }
}

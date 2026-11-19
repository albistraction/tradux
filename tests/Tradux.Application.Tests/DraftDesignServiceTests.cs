using Tradux.Application.Design;
using Tradux.Application.Generation;
using Tradux.Contracts;
using Tradux.Contracts.Design;
using Tradux.Domain.Mapping;
using Tradux.Tests.Shared;

namespace Tradux.Application.Tests;

/// <summary>Casos de uso de desenho do editor visual (Fase 6).</summary>
public class DraftDesignServiceTests
{
    private readonly DraftDesignService _service = new();

    private static DraftTestRequest Draft(MappingTemplate mapping, string? dataJson = null) => new(
        SnapshotFixtures.UploadFile("exemplo-simples.xsd"), mapping, SnapshotFixtures.Json(dataJson ?? SnapshotFixtures.DpsJson().GetRawText()))
    {
        LookupTables = SnapshotFixtures.SimpleDps().LookupTables,
    };

    [Fact]
    public void Lists_the_26_operators_with_categories_and_parameters()
    {
        var operators = _service.ListOperators();

        Assert.Equal(26, operators.Count); // 24 da seção 13 + ifElse e truncate (Fase 21)
        var concat = Assert.Single(operators, o => o.Name == "concat");
        Assert.Equal("Texto", concat.Category);
        Assert.Null(concat.MaxArguments);
        Assert.True(concat.RepeatsLastParameter);
        var lookup = Assert.Single(operators, o => o.Name == "lookup");
        Assert.Equal("LookupTable", lookup.Parameters[1].Kind);
        Assert.Equal("Boolean", Assert.Single(operators, o => o.Name == "equals").Result);
    }

    [Fact]
    public void Describes_simple_schema_with_cardinality_nillability_and_paths()
    {
        var tree = _service.DescribeSchema(SnapshotFixtures.UploadFile("exemplo-simples.xsd"));

        Assert.Equal("DPS", tree.RootElementName);
        Assert.True(tree.Root.Mappable);
        var numero = Assert.Single(tree.Root.Children, c => c.Name == "numero");
        Assert.Equal(("/DPS/numero", DestinationNodeKind.Scalar, 1, "String"), (numero.Path, numero.Kind, numero.MinOccurs, numero.ScalarType));
        Assert.Equal(0, Assert.Single(tree.Root.Children, c => c.Name == "serie").MinOccurs);
        Assert.True(Assert.Single(tree.Root.Children, c => c.Name == "valorDesconto").Nillable);
        Assert.Equal("YearMonth", Assert.Single(tree.Root.Children, c => c.Name == "competencia").ScalarType);
    }

    [Fact]
    public void Describes_composite_schema_with_mappable_choice_content_and_collections_as_repeatable()
    {
        var tree = _service.DescribeSchema(SnapshotFixtures.UploadFile("exemplo-composto.xsd", "exemplo-composto-tipos.xsd"));

        var all = Flatten(tree.Root).ToList();
        Assert.Contains(all, n => n.ContentModel == "choice" && n.Mappable); // Fase 15 (ADR-0018)
        Assert.Contains(all, n => n.Kind == DestinationNodeKind.Complex && n.IsRepeatable && n.Mappable);
    }

    [Fact]
    public void Collection_schema_exposes_repeatable_item_under_its_envelope()
    {
        var tree = _service.DescribeSchema(SnapshotFixtures.UploadFile("exemplo-colecao.xsd"));

        var itens = Assert.Single(tree.Root.Children, c => c.Name == "itens");
        var item = Assert.Single(itens.Children);
        Assert.Equal(("/PedidoComItens/itens/item", 0, (int?)null), (item.Path, item.MinOccurs, item.MaxOccurs));
        Assert.True(item.IsRepeatable);
    }

    [Theory]
    [InlineData("nao-e-base64!!")]
    public void Invalid_base64_is_an_argument_error(string content)
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            _service.DescribeSchema(new SchemaUpload([new SchemaFileUpload("x.xsd", content)], "x.xsd")));
        Assert.Contains("Base64", ex.Message);
    }

    [Fact]
    public void Path_traversal_in_uploaded_file_name_is_rejected()
    {
        var bytes = Convert.ToBase64String(File.ReadAllBytes(SnapshotFixtures.SchemaPath("exemplo-simples.xsd")));

        Assert.Throws<ArgumentException>(() =>
            _service.DescribeSchema(new SchemaUpload([new SchemaFileUpload("../x.xsd", bytes)], "../x.xsd")));
    }

    [Fact]
    public void Complete_draft_is_publishable_and_matches_semantically_equivalent_expected_xml()
    {
        var first = _service.Test(Draft(SnapshotFixtures.SimpleDps().Mapping));
        Assert.True(first.Publishable, string.Join("; ", first.Generation!.Diagnostics.Select(d => d.Code)));

        // Mesmo documento com outro prefixo de namespace e indentação: tem de bater.
        var document = System.Xml.Linq.XDocument.Parse(first.Generation!.Xml!);
        var root = document.Root!;
        System.Xml.Linq.Extensions.Remove(root.Attributes().Where(a => a.IsNamespaceDeclaration));
        root.SetAttributeValue(System.Xml.Linq.XNamespace.Xmlns + "n", root.Name.NamespaceName);
        var reprefixed = document.ToString(System.Xml.Linq.SaveOptions.None);
        Assert.Contains("<n:DPS", reprefixed);

        var second = _service.Test(Draft(SnapshotFixtures.SimpleDps().Mapping) with { ExpectedXml = reprefixed });

        Assert.True(second.ExpectedXmlMatches, reprefixed);
        Assert.True(second.Publishable);
        Assert.Equal(first.ContentHash, second.ContentHash);
    }

    [Fact]
    public void Different_expected_xml_is_reported_and_blocks_publishing()
    {
        var result = _service.Test(Draft(SnapshotFixtures.SimpleDps().Mapping) with { ExpectedXml = "<DPS xmlns=\"x\"/>" });

        Assert.False(result.ExpectedXmlMatches);
        Assert.False(result.Publishable);
        Assert.Contains(result.CompileDiagnostics, d => d.Code == DraftDesignService.ExpectedXmlMismatchCode);
    }

    [Fact]
    public void Required_field_without_mapping_is_a_compile_diagnostic_on_its_xpath()
    {
        var mapping = new MappingTemplate
        {
            Fields = SnapshotFixtures.SimpleDps().Mapping.Fields.Where(f => f.DestinationElementName != "numero").ToList(),
        };

        var result = _service.Test(Draft(mapping));

        Assert.False(result.Publishable);
        Assert.Contains(result.CompileDiagnostics, d => d.Severity == DiagnosticSeverity.Critical && d.DestinationXPath == "/DPS/numero");
    }

    [Fact]
    public void Unknown_operator_is_a_diagnostic_not_an_exception()
    {
        var mapping = new MappingTemplate { Fields = [new FieldMapping("numero", OperatorNode.Of("naoExiste"))] };

        var result = _service.Test(Draft(mapping));

        Assert.False(result.Publishable);
        Assert.Null(result.Generation);
        Assert.Equal(XmlGenerationService.SnapshotCompilationFailedCode, Assert.Single(result.CompileDiagnostics).Code);
    }

    [Fact]
    public void Nested_source_paths_can_be_mapped()
    {
        var mapping = new MappingTemplate
        {
            Fields = SnapshotFixtures.SimpleDps().Mapping.Fields
                .Select(f => f.DestinationElementName == "cnpjTomador" ? FieldMapping.Direct("Tomador.Cnpj", "cnpjTomador") : f)
                .ToList(),
        };
        var data = SnapshotFixtures.DpsJson().GetRawText().TrimEnd().TrimEnd('}') + ", \"Tomador\": { \"Cnpj\": \"11222333000144\" } }";

        var result = _service.Test(Draft(mapping, data));

        Assert.True(result.Publishable, string.Join("; ", result.Generation!.Diagnostics.Select(d => d.Code)));
        Assert.Contains("<cnpjTomador>11222333000144</cnpjTomador>", result.Generation.Xml);
    }

    // ---- Correções da revisão independente da Fase 6 ----

    private const string GroupInsideSequenceXsd = """
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:grp" xmlns="urn:grp" elementFormDefault="qualified">
          <xs:element name="R">
            <xs:complexType>
              <xs:sequence>
                <xs:element name="a" type="xs:string"/>
                <xs:choice>
                  <xs:element name="cpf" type="xs:string"/>
                  <xs:element name="cnpj" type="xs:string"/>
                </xs:choice>
                <xs:element name="linha" maxOccurs="unbounded">
                  <xs:complexType><xs:sequence><xs:element name="v" type="xs:string"/></xs:sequence></xs:complexType>
                </xs:element>
              </xs:sequence>
            </xs:complexType>
          </xs:element>
        </xs:schema>
        """;

    [Fact]
    public void Signature_node_is_marked_signed_by_caller_in_the_editor_json()
    {
        // O editor lê "signedByCaller" (wwwroot/js/model/draft.js): quem chama o serviço assina (ADR-0025).
        var tree = _service.DescribeSchema(SnapshotFixtures.Upload("""
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:ds="http://www.w3.org/2000/09/xmldsig#">
              <xs:element name="Nota"><xs:complexType><xs:sequence>
                <xs:element name="Numero" type="xs:string"/>
                <xs:element ref="ds:Signature"/>
              </xs:sequence></xs:complexType></xs:element>
            </xs:schema>
            """));

        var signature = Assert.Single(tree.Root.Children, c => c.Name == "Signature");
        Assert.True(signature.SignedByCaller);
        Assert.Equal(0, signature.MinOccurs);
        Assert.Contains("\"signedByCaller\":true",
            System.Text.Json.JsonSerializer.Serialize(signature, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Group_inside_sequence_has_its_own_node_path_and_its_children_use_the_element_path()
    {
        var tree = _service.DescribeSchema(SnapshotFixtures.Upload(GroupInsideSequenceXsd));

        var group = Assert.Single(tree.Root.Children, c => c.Kind == DestinationNodeKind.Group);
        Assert.Equal("/R/(choice#1)", group.Path);
        Assert.True(tree.Root.Mappable); // Fase 15: o motor gera choice (ADR-0018)
        Assert.Equal(["/R/cpf", "/R/cnpj"], group.Children.Select(c => c.Path)); // mesmo caminho que o XML e o mapeamento usam
        Assert.All(group.Children, c => Assert.True(c.Mappable));
        Assert.Equal(Flatten(tree.Root).Count(), Flatten(tree.Root).Select(n => n.Path).Distinct().Count());
    }

    [Fact]
    public void Critical_compile_diagnostics_are_reported_once_without_a_generation_result()
    {
        var mapping = new MappingTemplate
        {
            Fields = SnapshotFixtures.SimpleDps().Mapping.Fields.Where(f => f.DestinationElementName != "numero").ToList(),
        };

        var result = _service.Test(Draft(mapping));

        Assert.Null(result.Generation);
        Assert.Single(result.CompileDiagnostics, d => d.DestinationXPath == "/DPS/numero");
    }

    [Fact]
    public void Expected_xml_with_doctype_is_rejected_without_expanding_entities()
    {
        const string withDtd = """<!DOCTYPE x [<!ENTITY a "aaaaaaaaaa">]><DPS xmlns="http://exemplo.ilustrativo.tradux/dps/v1">&a;</DPS>""";

        var result = _service.Test(Draft(SnapshotFixtures.SimpleDps().Mapping) with { ExpectedXml = withDtd });

        Assert.False(result.ExpectedXmlMatches);
        Assert.Contains(result.CompileDiagnostics, d => d.Code == DraftDesignService.ExpectedXmlInvalidCode);
    }

    [Fact]
    public void Expected_xml_mismatch_points_to_the_first_difference()
    {
        var generated = _service.Test(Draft(SnapshotFixtures.SimpleDps().Mapping)).Generation!.Xml!;
        var expected = generated.Replace("<numero>000123</numero>", "<numero>999</numero>");

        var result = _service.Test(Draft(SnapshotFixtures.SimpleDps().Mapping) with { ExpectedXml = expected });

        var mismatch = Assert.Single(result.CompileDiagnostics, d => d.Code == DraftDesignService.ExpectedXmlMismatchCode);
        Assert.Equal("/DPS/numero", mismatch.DestinationXPath);
        Assert.Contains("\"999\"", mismatch.Message);
        Assert.Contains("\"000123\"", mismatch.Message);
    }

    [Fact]
    public void Missing_entry_point_or_null_file_is_an_argument_error_not_a_crash()
    {
        var bytes = Convert.ToBase64String(File.ReadAllBytes(SnapshotFixtures.SchemaPath("exemplo-simples.xsd")));

        Assert.Throws<ArgumentException>(() => _service.DescribeSchema(new SchemaUpload([new SchemaFileUpload("a.xsd", bytes)], null!)));
        Assert.Throws<ArgumentException>(() => _service.DescribeSchema(new SchemaUpload([null!], "a.xsd")));
        Assert.Throws<ArgumentException>(() => _service.DescribeSchema(new SchemaUpload([new SchemaFileUpload("a.xsd", null!)], "a.xsd")));
    }

    private static IEnumerable<DestinationNode> Flatten(DestinationNode node) =>
        new[] { node }.Concat(node.Children.SelectMany(Flatten));

    [Fact]
    public async Task Draft_test_and_quality_are_audited_with_kind_and_outcome_only()
    {
        var audit = new InMemoryAuditSink();
        var service = new DraftDesignService(audit: audit);
        var principal = new Security.AccessPrincipal(SnapshotFixtures.Organization, "ana", []);

        var test = await service.TestAsync(Draft(SnapshotFixtures.SimpleDps().Mapping), principal);
        await service.QualityAsync(new DraftQualityRequest(SnapshotFixtures.UploadFile("exemplo-simples.xsd"), SnapshotFixtures.SimpleDps().Mapping), principal);

        Assert.True(test.Publishable);
        Assert.Collection(audit.Events,
            e => Assert.Equal(("template.test", "design:test", "success", "ana"), (e.Action, e.Target, e.Outcome, e.Actor)),
            e => Assert.Equal(("template.test", "design:quality"), (e.Action, e.Target)));
        Assert.All(audit.Events, e => Assert.Equal(SnapshotFixtures.Organization, e.OrganizationId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.TestAsync(Draft(SnapshotFixtures.SimpleDps().Mapping), principal));
    }
}

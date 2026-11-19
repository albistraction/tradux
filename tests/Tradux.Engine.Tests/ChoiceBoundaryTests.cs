using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Tradux.Domain.Mapping;
using Tradux.Domain.SourceData;
using Tradux.Engine.Compilation;
using Tradux.Xsd;
using Xunit;

namespace Tradux.Engine.Tests;

/// <summary>
/// xs:choice/xs:all e grupos aninhados no destino (Fase 15, ADR-0018): grupos são
/// transparentes no mapeamento. O ramo que tem conteúdo vai para o XML. Choice
/// único com dois ramos com conteúdo, ou sem ramo, é diagnóstico claro, nunca XML
/// errado.
/// </summary>
public class ChoiceBoundaryTests
{
    private static string FixturePath => RepositoryPaths.Combine("docs", "schemas", "exemplo-composto.xsd");

    private const string Xsd = """
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
          <xs:element name="R">
            <xs:complexType>
              <xs:sequence>
                <xs:element name="a" type="xs:string"/>
                <xs:element name="tomador">
                  <xs:complexType>
                    <xs:choice>
                      <xs:element name="cpf" type="xs:string"/>
                      <xs:element name="cadastro"><xs:complexType><xs:sequence><xs:element name="im" type="xs:string"/></xs:sequence></xs:complexType></xs:element>
                    </xs:choice>
                  </xs:complexType>
                </xs:element>
                <xs:element name="lista">
                  <xs:complexType>
                    <xs:choice maxOccurs="3">
                      <xs:element name="x" type="xs:string"/>
                      <xs:element name="y"><xs:complexType><xs:sequence><xs:element name="v" type="xs:string"/></xs:sequence></xs:complexType></xs:element>
                    </xs:choice>
                  </xs:complexType>
                </xs:element>
              </xs:sequence>
            </xs:complexType>
          </xs:element>
        </xs:schema>
        """;

    private static readonly MappingTemplate Mapping = new()
    {
        Fields = [FieldMapping.Direct("A", "a")],
        Complex =
        [
            new ComplexMapping
            {
                DestinationElementName = "tomador",
                Content = new MappingTemplate
                {
                    Fields = [FieldMapping.Direct("Cpf", "cpf")],
                    Complex = [new ComplexMapping { DestinationElementName = "cadastro", Content = new MappingTemplate { Fields = [FieldMapping.Direct("Im", "im")] } }],
                },
            },
            new ComplexMapping
            {
                DestinationElementName = "lista",
                Content = new MappingTemplate
                {
                    Fields = [FieldMapping.Direct("X", "x")],
                    Complex = [new ComplexMapping { DestinationElementName = "y", Content = new MappingTemplate { Fields = [FieldMapping.Direct("V", "v")] } }],
                },
            },
        ],
    };

    private static GenerationOutcome Generate(string json)
    {
        var xsd = XsdSchemaImporter.ImportInMemory(new Dictionary<string, byte[]> { ["a.xsd"] = Encoding.UTF8.GetBytes(Xsd) }, "a.xsd");
        var template = TemplateCompiler.Compile(Mapping, xsd);
        return XmlGenerationEngine.Generate(template, SourceDataTree.FromJson(JsonDocument.Parse(json).RootElement));
    }

    [Fact]
    public void The_branch_with_content_is_generated_and_the_other_is_dropped_without_errors()
    {
        var outcome = Generate("""{"A":"1","Cpf":"123","X":"x1"}""");

        Assert.True(outcome.Success, string.Join("; ", outcome.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
        var root = XDocument.Parse(outcome.Xml!).Root!;
        Assert.Equal("123", root.Element("tomador")!.Element("cpf")!.Value);
        Assert.Null(root.Element("tomador")!.Element("cadastro")); // ramo sem dado: fora, e sem REQUIRED_VALUE_MISSING de "im"
    }

    [Fact]
    public void Repeated_choice_emits_every_branch_with_content_in_schema_order()
    {
        var outcome = Generate("""{"A":"1","Im":"55","X":"x1","V":"v1"}""");

        Assert.True(outcome.Success, string.Join("; ", outcome.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
        var lista = XDocument.Parse(outcome.Xml!).Root!.Element("lista")!;
        Assert.Equal(["x", "y"], lista.Elements().Select(e => e.Name.LocalName));
    }

    [Theory]
    [InlineData("""{"A":"1","Cpf":"123","Im":"55","X":"x"}""", "CHOICE_MULTIPLE_BRANCHES")]
    [InlineData("""{"A":"1","X":"x"}""", "CHOICE_NO_BRANCH")]
    [InlineData("""{"A":"1","Cpf":"1"}""", "CHOICE_NO_BRANCH")] // lista: choice repetível exige 1 ocorrência
    public void Wrong_number_of_branches_is_a_clear_diagnostic(string json, string code)
    {
        var outcome = Generate(json);

        Assert.False(outcome.Success);
        Assert.Contains(outcome.Diagnostics, d => d.Code == code);
    }

    [Fact]
    public void Leaving_the_required_choice_element_unmapped_is_a_clear_diagnostic_not_a_crash()
    {
        var importedXsd = XsdFiles.Import(FixturePath);

        var compiled = TemplateCompiler.Compile(new MappingTemplate(), importedXsd);

        Assert.Contains(compiled.CompileTimeDiagnostics, d => d.Code == "MAPPING_MISSING_REQUIRED" && d.DestinationXPath!.EndsWith("/tomador"));
        Assert.Contains(compiled.CompileTimeDiagnostics, d => d.Code == "MAPPING_MISSING_REQUIRED" && d.DestinationXPath!.EndsWith("/itens"));
        Assert.True(compiled.HasCriticalCompileTimeDiagnostics);
    }

    [Fact]
    public void Required_choice_without_any_mapped_branch_is_reported_at_compile_time()
    {
        var importedXsd = XsdFiles.Import(FixturePath);
        var template = new MappingTemplate { Complex = [new ComplexMapping { DestinationElementName = "tomador", Content = new MappingTemplate() }] };

        var compiled = TemplateCompiler.Compile(template, importedXsd);

        Assert.Contains(compiled.CompileTimeDiagnostics, d => d.Code == "MAPPING_MISSING_REQUIRED_CHOICE" && d.Message.Contains("tomadorPorDocumento", StringComparison.Ordinal));
    }
}

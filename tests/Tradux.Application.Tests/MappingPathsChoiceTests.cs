namespace Tradux.Application.Tests;

/// <summary>Revisão da Fase 19: camadas (MappingPaths) com a mesma regra de choice do editor.</summary>
public class MappingPathsChoiceTests
{
    [Fact]
    public void Direct_choice_content_does_not_emit_unchosen_required_branches_and_keeps_single_object()
    {
        // Rps do webserviceNFSe: conteúdo xs:choice direto, ramos complexos obrigatórios.
        const string xsd = """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xs:element name="R"><xs:complexType><xs:sequence>
                <xs:element name="Rps" maxOccurs="unbounded"><xs:complexType><xs:choice maxOccurs="9">
                  <xs:element name="A"><xs:complexType><xs:sequence><xs:element name="v" type="xs:string"/></xs:sequence></xs:complexType></xs:element>
                  <xs:element name="B"><xs:complexType><xs:sequence><xs:element name="w" type="xs:string"/></xs:sequence></xs:complexType></xs:element>
                </xs:choice></xs:complexType></xs:element>
              </xs:sequence></xs:complexType></xs:element>
            </xs:schema>
            """;
        var tree = Design.DestinationTreeBuilder.Build(Xsd.XsdSchemaImporter.ImportInMemory(
            new Dictionary<string, byte[]> { ["r.xsd"] = System.Text.Encoding.UTF8.GetBytes(xsd) }, "r.xsd"));
        var original = new Domain.Mapping.MappingTemplate
        {
            Collections =
            [
                new Domain.Mapping.CollectionMapping
                {
                    DestinationElementName = "Rps", SourceCollectionPath = "infDPS", SingleObjectAsItem = true,
                    ItemContent = new Domain.Mapping.MappingTemplate
                    {
                        Complex = [new Domain.Mapping.ComplexMapping { DestinationElementName = "A", Content = new Domain.Mapping.MappingTemplate { Fields = [Domain.Mapping.FieldMapping.Direct("x", "v")] } }],
                    },
                },
            ],
        };

        var rebuilt = Layers.MappingPaths.Build(tree.Root, Layers.MappingPaths.Flatten(original, tree.RootElementName));

        var rps = Assert.Single(rebuilt.Collections);
        Assert.True(rps.SingleObjectAsItem); // opção não se perde nas camadas
        Assert.Equal(["A"], rps.ItemContent.Complex.Select(c => c.DestinationElementName)); // B não sai vazio
    }
}

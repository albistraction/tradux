using System.Text;
using Xunit;

namespace Tradux.Xsd.Tests;

/// <summary>
/// <see cref="XsdSchemaImporter.ImportInMemory"/>: recompila um snapshot a
/// partir dos XSDs gravados no próprio documento — o mesmo resultado do upload.
/// </summary>
public class InMemoryImportTests
{
    private static Dictionary<string, byte[]> CompositeFiles() => new()
    {
        ["exemplo-composto.xsd"] = File.ReadAllBytes(RepositoryPaths.Combine("docs", "schemas", "exemplo-composto.xsd")),
        ["exemplo-composto-tipos.xsd"] = File.ReadAllBytes(RepositoryPaths.Combine("docs", "schemas", "exemplo-composto-tipos.xsd")),
    };

    [Fact]
    public void In_memory_import_resolves_includes_and_hashes_deterministically()
    {
        var first = XsdSchemaImporter.ImportInMemory(CompositeFiles(), "exemplo-composto.xsd");
        var second = XsdSchemaImporter.ImportInMemory(CompositeFiles(), "exemplo-composto.xsd");

        Assert.Equal(first.ContentHash, second.ContentHash);
        Assert.Equal("DpsComposta", first.Schema.RootElementName);
    }

    [Fact]
    public void Missing_included_file_fails_loudly_instead_of_producing_a_partial_schema()
    {
        var files = CompositeFiles();
        files.Remove("exemplo-composto-tipos.xsd");

        Assert.ThrowsAny<Exception>(() => XsdSchemaImporter.ImportInMemory(files, "exemplo-composto.xsd"));
    }

    [Fact]
    public void Sent_file_of_a_namespace_the_entry_point_uses_without_import_joins_the_set()
    {
        // Como o XSD da Agili: dsig:Signature sem xs:import do xmldsig.
        var principal = Encoding.UTF8.GetBytes("""
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:dsig="urn:assinatura"
                       targetNamespace="urn:nota" xmlns="urn:nota" elementFormDefault="qualified">
              <xs:element name="Nota"><xs:complexType><xs:sequence>
                <xs:element name="Numero" type="xs:string"/>
                <xs:element ref="dsig:Assinatura" minOccurs="0"/>
              </xs:sequence></xs:complexType></xs:element>
            </xs:schema>
            """);
        var assinatura = Encoding.UTF8.GetBytes("""
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:assinatura">
              <xs:element name="Assinatura" type="xs:string"/>
            </xs:schema>
            """);
        var semAssinatura = new Dictionary<string, byte[]> { ["nota.xsd"] = principal };
        var comAssinatura = new Dictionary<string, byte[]> { ["nota.xsd"] = principal, ["assinatura.xsd"] = assinatura };

        Assert.ThrowsAny<Exception>(() => XsdSchemaImporter.ImportInMemory(semAssinatura, "nota.xsd"));

        var imported = XsdSchemaImporter.ImportInMemory(comAssinatura, "nota.xsd", "Nota");
        Assert.Equal("Nota", imported.Schema.RootElementName);
        Assert.NotEqual(
            XsdSchemaImporter.ImportInMemory(new Dictionary<string, byte[]>(comAssinatura) { ["assinatura.xsd"] = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(assinatura).Replace("xs:string", "xs:token")) }, "nota.xsd", "Nota").ContentHash,
            imported.ContentHash);
    }

    [Fact]
    public void Signature_without_the_xmldsig_file_uses_the_built_in_copy()
    {
        var files = new Dictionary<string, byte[]>
        {
            ["nota.xsd"] = Encoding.UTF8.GetBytes("""
                <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:dsig="http://www.w3.org/2000/09/xmldsig#"
                           targetNamespace="urn:nota" xmlns="urn:nota" elementFormDefault="qualified">
                  <xs:element name="Nota"><xs:complexType><xs:sequence>
                    <xs:element name="Numero" type="xs:string"/>
                    <xs:element ref="dsig:Signature" minOccurs="0"/>
                  </xs:sequence></xs:complexType></xs:element>
                </xs:schema>
                """),
        };

        var imported = XsdSchemaImporter.ImportInMemory(files, "nota.xsd");

        Assert.Equal("Nota", imported.Schema.RootElementName);
        Assert.Equal(imported.ContentHash, XsdSchemaImporter.ImportInMemory(files, "nota.xsd").ContentHash);
    }

    [Fact]
    public void Required_signature_is_left_to_the_connector_so_the_unsigned_xml_is_valid()
    {
        // Como o tcIdentificacaoPrestador da Agili: choice obrigatório ChaveDigital | dsig:Signature,
        // e uma assinatura obrigatória fora de choice.
        var files = new Dictionary<string, byte[]>
        {
            ["nota.xsd"] = Encoding.UTF8.GetBytes("""
                <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:dsig="http://www.w3.org/2000/09/xmldsig#"
                           targetNamespace="urn:nota" xmlns="urn:nota" elementFormDefault="qualified">
                  <xs:element name="Nota"><xs:complexType><xs:sequence>
                    <xs:element name="Prestador"><xs:complexType><xs:sequence>
                      <xs:choice>
                        <xs:element name="ChaveDigital" type="xs:string"/>
                        <xs:element ref="dsig:Signature"/>
                      </xs:choice>
                      <xs:element name="Cnpj" type="xs:string"/>
                    </xs:sequence></xs:complexType></xs:element>
                    <xs:element ref="dsig:Signature"/>
                  </xs:sequence></xs:complexType></xs:element>
                </xs:schema>
                """),
        };

        var imported = XsdSchemaImporter.ImportInMemory(files, "nota.xsd", "Nota");

        string[] Errors(string xml)
        {
            var errors = new List<string>();
            System.Xml.Schema.Extensions.Validate(System.Xml.Linq.XDocument.Parse(xml), imported.SchemaSet, (_, e) => errors.Add(e.Message));
            return errors.ToArray();
        }

        Assert.Empty(Errors("""<Nota xmlns="urn:nota"><Prestador><Cnpj>1</Cnpj></Prestador></Nota>"""));
        Assert.Empty(Errors("""<Nota xmlns="urn:nota"><Prestador><ChaveDigital>k</ChaveDigital><Cnpj>1</Cnpj></Prestador></Nota>"""));
        Assert.NotEmpty(Errors("""<Nota xmlns="urn:nota"><Prestador></Prestador></Nota>"""));
    }

    [Theory]
    [InlineData("../fora.xsd")]
    [InlineData("/absoluto.xsd")]
    public void File_names_escaping_the_set_are_rejected(string name)
    {
        var files = new Dictionary<string, byte[]> { [name] = Encoding.UTF8.GetBytes("<xs:schema xmlns:xs=\"http://www.w3.org/2001/XMLSchema\"/>") };

        Assert.Throws<InvalidOperationException>(() => XsdSchemaImporter.ImportInMemory(files, name));
    }

    [Fact]
    public void Doctype_is_rejected()
    {
        var files = new Dictionary<string, byte[]>
        {
            ["x.xsd"] = Encoding.UTF8.GetBytes("<!DOCTYPE x [<!ENTITY e SYSTEM \"file:///c:/windows/win.ini\">]><xs:schema xmlns:xs=\"http://www.w3.org/2001/XMLSchema\"/>"),
        };

        Assert.ThrowsAny<Exception>(() => XsdSchemaImporter.ImportInMemory(files, "x.xsd"));
    }

    [Fact]
    public void Size_and_count_limits_apply()
    {
        var tiny = new XsdImportSecurityOptions(MaxFileBytes: 10);
        Assert.Throws<InvalidOperationException>(() => XsdSchemaImporter.ImportInMemory(CompositeFiles(), "exemplo-composto.xsd", options: tiny));

        var oneFile = new XsdImportSecurityOptions(MaxPackageEntryCount: 1);
        Assert.Throws<InvalidOperationException>(() => XsdSchemaImporter.ImportInMemory(CompositeFiles(), "exemplo-composto.xsd", options: oneFile));
    }
}

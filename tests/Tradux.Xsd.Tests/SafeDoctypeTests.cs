using System.Text;

namespace Tradux.Xsd.Tests;

/// <summary>
/// xmldsig-core-schema.xsd oficial do W3C (DOCTYPE com entidade interna
/// literal) é aceito; qualquer outro DOCTYPE continua recusado (XXE, seção 11).
/// </summary>
public class SafeDoctypeTests
{
    // Cabeçalho do xmldsig-core-schema.xsd do W3C (o identificador externo nunca é buscado).
    private const string W3cHeader = """
        <?xml version="1.0" encoding="utf-8"?>
        <!DOCTYPE schema
          PUBLIC "-//W3C//DTD XMLSchema 200102//EN" "http://www.w3.org/2001/XMLSchema.dtd"
         [
           <!ATTLIST schema
             xmlns:ds CDATA #FIXED "http://www.w3.org/2000/09/xmldsig#">
           <!ENTITY dsig 'http://www.w3.org/2000/09/xmldsig#'>
           <!ENTITY % p ''>
           <!ENTITY % s ''>
          ]>
        """;

    private static string Dsig(string header = W3cHeader) => header + """
        <schema xmlns="http://www.w3.org/2001/XMLSchema" xmlns:ds="&dsig;" targetNamespace="&dsig;" version="0.1" elementFormDefault="qualified">
          <element name="Signature" type="ds:SignatureType"/>
          <complexType name="SignatureType"><sequence><element name="SignatureValue" type="string"/></sequence></complexType>
        </schema>
        """;

    private const string Main = """
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:ds="http://www.w3.org/2000/09/xmldsig#">
          <xs:import namespace="http://www.w3.org/2000/09/xmldsig#" schemaLocation="xmldsig-core-schema.xsd"/>
          <xs:element name="Nota"><xs:complexType><xs:sequence>
            <xs:element name="Numero" type="xs:string"/>
            <xs:element ref="ds:Signature" minOccurs="0"/>
          </xs:sequence></xs:complexType></xs:element>
        </xs:schema>
        """;

    private static Dictionary<string, byte[]> Files(string dsig) => new()
    {
        ["nota.xsd"] = Encoding.UTF8.GetBytes(Main),
        ["xmldsig-core-schema.xsd"] = Encoding.UTF8.GetBytes(dsig),
    };

    [Fact]
    public void Official_w3c_xmldsig_is_accepted_for_source_and_destination()
    {
        var source = XsdSchemaImporter.ImportSourceInMemory(Files(Dsig()), "nota.xsd", "Nota");
        var tree = SourceXmlReader.Read(source,
            "<Nota><Numero>1</Numero><Signature xmlns=\"http://www.w3.org/2000/09/xmldsig#\"><SignatureValue>abc</SignatureValue></Signature></Nota>", TestSourceXml.Limits);

        Assert.Equal("1", tree["Numero"]);
        Assert.Contains(source.Shape.Fields, f => f.Name == "Signature" && f.IsObject);
        Assert.Equal("Nota", XsdSchemaImporter.ImportInMemory(Files(Dsig()), "nota.xsd", "Nota").Schema.RootElementName);
    }

    [Theory]
    [InlineData("<!DOCTYPE schema [<!ENTITY dsig SYSTEM \"file:///c:/windows/win.ini\">]>", "entidade externa (XXE)")]
    [InlineData("<!DOCTYPE schema [<!ENTITY a 'x'><!ENTITY dsig '&a;&a;'>]>", "entidade aninhada (billion laughs)")]
    [InlineData("<!DOCTYPE schema [<!ENTITY % ext SYSTEM \"http://exemplo/x.dtd\"> %ext;<!ENTITY dsig 'x'>]>", "entidade de parâmetro externa")]
    [InlineData("<!DOCTYPE schema [<!ELEMENT schema ANY><!ENTITY dsig 'x'>]>", "declaração de elemento")]
    [InlineData("<!DOCTYPE schema SYSTEM \"http://exemplo/x.dtd\" [<!ENTITY dsig 'x'><!ENTITY dsig2 \"<a/>\">]>", "marcação no valor")]
    [InlineData("<!DOCTYPE schema [<!ENTITY % e 'x'> %e; <!ENTITY dsig 'x'>]>", "referência a entidade de parâmetro")]
    [InlineData("<!DOCTYPE schema [<!ENTITY lt 'X'><!ENTITY dsig 'x'>]>", "redeclaração de entidade predefinida")]
    [InlineData("<!DOCTYPE schema [<?pi x?><!ENTITY dsig 'x'>]>", "instrução de processamento")]
    public void Any_other_doctype_is_still_refused(string header, string reason)
    {
        var error = Assert.Throws<InvalidOperationException>(() => XsdSchemaImporter.ImportSourceInMemory(Files(Dsig(header)), "nota.xsd", "Nota"));

        Assert.True(error.Message.Contains("DOCTYPE", StringComparison.Ordinal), reason);
    }

    [Fact]
    public void Entity_amplification_is_refused_during_expansion_without_building_it_in_memory()
    {
        // Revisão de segurança, B1: 1,7 MB de entrada que expandiria para ~600 MB.
        var dsig = "<!DOCTYPE s [<!ENTITY e '" + new string('x', 1024) + "'>]><s>" + string.Concat(Enumerable.Repeat("&e;", 600_000)) + "</s>";
        var files = new Dictionary<string, byte[]> { ["nota.xsd"] = Encoding.UTF8.GetBytes(Main), ["xmldsig-core-schema.xsd"] = Encoding.UTF8.GetBytes(dsig) };
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var error = Assert.Throws<InvalidOperationException>(() => XsdSchemaImporter.ImportSourceInMemory(files, "nota.xsd", "Nota"));

        Assert.Contains("limite", error.Message, StringComparison.Ordinal);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"levou {clock.Elapsed}");
        var allocatedMb = (GC.GetAllocatedBytesForCurrentThread() - allocatedBefore) / 1048576.0;
        Assert.True(allocatedMb < 200, $"alocou {allocatedMb:0} MB");
    }

    [Fact]
    public void Two_doctypes_unsupported_encoding_and_unterminated_attlist_are_refused_quickly()
    {
        var twice = Dsig() + "<!-- --><!DOCTYPE x [<!ENTITY y 'z'>]>";
        Assert.Throws<InvalidOperationException>(() => XsdSchemaImporter.ImportSourceInMemory(Files(twice), "nota.xsd", "Nota"));

        var cp1252 = Dsig().Replace("encoding=\"utf-8\"", "encoding=\"windows-1252\"");
        var encodingError = Assert.Throws<InvalidOperationException>(() => XsdSchemaImporter.ImportSourceInMemory(Files(cp1252), "nota.xsd", "Nota"));
        Assert.Contains("windows-1252", encodingError.Message, StringComparison.Ordinal);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var attlist = "<!DOCTYPE s [" + string.Concat(Enumerable.Repeat("<!ATTLIST a b CDATA \"x\" ", 10_000)) + "]><s/>";
        Assert.Throws<InvalidOperationException>(() => XsdSchemaImporter.ImportSourceInMemory(Files(attlist), "nota.xsd", "Nota"));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"levou {clock.Elapsed}");
    }

    [Fact]
    public void Entities_are_not_expanded_inside_comments_or_cdata()
    {
        var dsig = Dsig().Replace("<element name=\"Signature\"", "<!-- &dsig; --><annotation><documentation><![CDATA[&dsig;]]></documentation></annotation><element name=\"Signature\"");

        var source = XsdSchemaImporter.ImportSourceInMemory(Files(dsig), "nota.xsd", "Nota");

        Assert.Contains(source.Shape.Fields, f => f.Name == "Signature");
    }
}

/// <summary>
/// Destino com a assinatura do xmldsig real (correção pós-Fase 12): xs:any,
/// conteúdo simples com atributo e xs:integer não impedem a importação —
/// viram conteúdo opaco/texto; o resto do XSD continua mapeável.
/// </summary>
public class DestinationSignatureContentTests
{
    private const string Dsig = """
        <schema xmlns="http://www.w3.org/2001/XMLSchema" xmlns:ds="http://www.w3.org/2000/09/xmldsig#" targetNamespace="http://www.w3.org/2000/09/xmldsig#" elementFormDefault="qualified">
          <element name="Signature"><complexType><sequence>
            <element name="SignatureValue"><complexType><simpleContent><extension base="base64Binary"><attribute name="Id" type="ID"/></extension></simpleContent></complexType></element>
            <element name="HMACOutputLength" type="integer" minOccurs="0"/>
            <element name="Object" minOccurs="0"><complexType mixed="true"><sequence><any namespace="##any" processContents="lax" maxOccurs="unbounded"/></sequence></complexType></element>
          </sequence></complexType></element>
        </schema>
        """;

    private static Dictionary<string, byte[]> Files(string signatureOccurs) => new()
    {
        ["d.xsd"] = Encoding.UTF8.GetBytes($$"""
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:ds="http://www.w3.org/2000/09/xmldsig#">
              <xs:import namespace="http://www.w3.org/2000/09/xmldsig#" schemaLocation="dsig.xsd"/>
              <xs:element name="Nota"><xs:complexType><xs:sequence>
                <xs:element name="Numero" type="xs:string"/>
                <xs:element ref="ds:Signature" minOccurs="{{signatureOccurs}}"/>
              </xs:sequence></xs:complexType></xs:element>
            </xs:schema>
            """),
        ["dsig.xsd"] = Encoding.UTF8.GetBytes(Dsig),
    };

    [Fact]
    public void Signature_with_any_simple_content_and_integer_is_imported_as_opaque_content()
    {
        var schema = XsdSchemaImporter.ImportInMemory(Files("0"), "d.xsd", "Nota").Schema;

        var signature = schema.RootContent.Items.OfType<ComplexElementItem>().Single(i => i.Name == "Signature");
        Assert.Equal(0, signature.Cardinality.MinOccurs); // ref preserva a cardinalidade do uso
        // A estrutura do xmldsig não entra no DE-PARA: quem chama o serviço assina depois (ADR-0025).
        Assert.True(signature.SignedByCaller);
        Assert.True(ParticleRules.ContainsOpaque(signature.Content));
        Assert.DoesNotContain(signature.Content.Items, i => i is ComplexElementItem or ScalarElementItem);
    }
}

/// <summary>Revisão de segurança, rodada 2: B1 e avisos 1, 2 e 4.</summary>
public class SafeDoctypeRound2Tests
{
    private static Dictionary<string, byte[]> Single(string xsd) => new() { ["s.xsd"] = Encoding.UTF8.GetBytes(xsd) };

    [Theory]
    [InlineData("<xs:any maxOccurs=\"99999999999\"/>")]
    [InlineData("<xs:element name=\"e\" type=\"xs:string\" maxOccurs=\"99999999999\"/>")]
    [InlineData("<xs:sequence maxOccurs=\"99999999999\"><xs:element name=\"e\" type=\"xs:string\"/></xs:sequence>")]
    public void Cardinality_above_int_is_a_schema_error_never_an_overflow(string item)
    {
        var xsd = $"<xs:schema xmlns:xs=\"http://www.w3.org/2001/XMLSchema\"><xs:element name=\"R\"><xs:complexType><xs:sequence>{item}</xs:sequence></xs:complexType></xs:element></xs:schema>";

        Assert.Throws<System.Xml.Schema.XmlSchemaException>(() => XsdSchemaImporter.ImportInMemory(Single(xsd), "s.xsd"));
    }

    [Fact]
    public void Invalid_utf8_bytes_quotes_in_entity_values_and_doctype_outside_the_prolog_are_refused()
    {
        const string Body = "<xs:schema xmlns:xs=\"http://www.w3.org/2001/XMLSchema\"><xs:element name=\"R\" type=\"xs:string\"/></xs:schema>";
        var latin = Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"utf-8\"?><!DOCTYPE s [<!ENTITY e 'x'>]>").Concat(Encoding.Latin1.GetBytes("<!-- ção -->" + Body)).ToArray();
        Assert.Throws<InvalidOperationException>(() => XsdSchemaImporter.ImportInMemory(new Dictionary<string, byte[]> { ["s.xsd"] = latin }, "s.xsd"));

        Assert.Throws<InvalidOperationException>(() => XsdSchemaImporter.ImportInMemory(Single("<!DOCTYPE s [<!ENTITY t 'string\" fixed=\"X'>]>" + Body), "s.xsd"));
        Assert.Throws<InvalidOperationException>(() => XsdSchemaImporter.ImportInMemory(Single("<!-- <!DOCTYPE a [<!ENTITY d 'x'>]> -->" + Body), "s.xsd"));
    }
}

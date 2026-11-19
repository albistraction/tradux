using Tradux.Domain.SourceContracts;
using Tradux.Domain.SourceData;
using System.Text;

namespace Tradux.Xsd.Tests;

/// <summary>Fase 12 (ADR-0015): contrato de origem XSD e leitura de XML de origem.</summary>
public class SourceXmlReaderTests
{
    private static SourceSchema Source() => XsdSchemaImporter.ImportSourceInMemory(
        new Dictionary<string, byte[]> { ["origem-exemplo.xsd"] = File.ReadAllBytes(RepositoryPaths.Combine("docs", "schemas", "origem-exemplo.xsd")) },
        "origem-exemplo.xsd");

    private static string SampleXml() => File.ReadAllText(RepositoryPaths.Combine("docs", "schemas", "origem-exemplo.xml"));

    private static T At<T>(IReadOnlyDictionary<string, object?> tree, params string[] path)
    {
        object? current = tree;
        foreach (var segment in path)
        {
            current = ((IReadOnlyDictionary<string, object?>)current!)[segment];
        }

        return (T)current!;
    }

    [Fact]
    public void Shape_comes_from_the_xsd_objects_lists_values_attributes_and_simple_content()
    {
        var shape = Source().Shape;

        Assert.Equal("NotaErp", shape.RootElementName);
        Assert.Equal("http://exemplo.ilustrativo.tradux/erp/v1", shape.TargetNamespace);
        Assert.Equal(["@versao", "@situacao", "Identificacao", "Prestador", "Tomador", "Servico", "Itens"], shape.Fields.Select(f => f.Name));
        var servico = shape.Fields.Single(f => f.Name == "Servico");
        var valor = servico.Children.Single(c => c.Name == "Valor");
        Assert.True(valor.IsObject);
        Assert.Equal(["@moeda", "$value"], valor.Children.Select(c => c.Name));
        Assert.Equal(SourceValueType.Number, valor.Children.Single(c => c.Name == "$value").ValueType);
        Assert.True(servico.Children.Single(c => c.Name == "Desconto").Nillable);
        var item = shape.Fields.Single(f => f.Name == "Itens").Children.Single(c => c.Name == "Item");
        Assert.True(item.Repeated);
        Assert.Equal(SourceValueType.Number, item.Children.Single(c => c.Name == "Quantidade").ValueType); // xs:int
        Assert.Equal(SourceValueType.Date, shape.Fields.Single(f => f.Name == "Identificacao").Children.Single(c => c.Name == "DataEmissao").ValueType);
        Assert.False(shape.Fields.Single(f => f.Name == "Tomador").Required);
    }

    [Fact]
    public void Valid_xml_becomes_the_same_tree_a_json_would()
    {
        var tree = SourceXmlReader.Read(Source(), SampleXml(), TestSourceXml.Limits);

        Assert.Equal("2.1", At<string>(tree, "@versao"));
        Assert.Equal("1", At<string>(tree, "@situacao"));
        Assert.Equal("000123", At<string>(tree, "Identificacao", "Numero")); // texto preserva zeros à esquerda
        Assert.Equal("2026-09-28", At<string>(tree, "Identificacao", "DataEmissao"));
        Assert.Equal(1500.50m, At<decimal>(tree, "Servico", "Valor", "$value"));
        Assert.Equal("BRL", At<string>(tree, "Servico", "Valor", "@moeda"));
        Assert.True(At<IReadOnlyDictionary<string, object?>>(tree, "Servico").ContainsKey("Desconto"));
        Assert.Null(At<IReadOnlyDictionary<string, object?>>(tree, "Servico")["Desconto"]); // xsi:nil = presente e nulo
        Assert.False(At<IReadOnlyDictionary<string, object?>>(tree, "Tomador").ContainsKey("Nome")); // ausente ≠ nulo
        var itens = At<List<object?>>(tree, "Itens", "Item"); // um item só continua lista (vem do XSD)
        Assert.Single(itens);
        Assert.Equal(10m, ((IReadOnlyDictionary<string, object?>)itens[0]!)["Quantidade"]);
    }

    [Fact]
    public void Bytes_respect_the_declared_encoding()
    {
        var xml = SampleXml().Replace("UTF-8", "ISO-8859-1").Replace("Consultoria em sistemas", "Serviço de manutenção");
        using var stream = new MemoryStream(Encoding.Latin1.GetBytes(xml));

        var tree = SourceXmlReader.Read(Source(), stream, TestSourceXml.Limits);

        Assert.Equal("Serviço de manutenção", At<string>(tree, "Servico", "Discriminacao"));
    }

    [Theory]
    [InlineData("<Numero>000123</Numero>", "", "fora do XSD: falta Numero")]
    [InlineData("<Quantidade>10</Quantidade>", "<Quantidade>dez</Quantidade>", "tipo xs:int")]
    [InlineData("situacao=\"1\"", "situacao=\"9\"", "enumeração do atributo")]
    [InlineData("<Cnpj>12345678000199</Cnpj>", "<Cnpj>123</Cnpj>", "pattern")]
    public void Xml_outside_the_source_xsd_is_refused_with_positions(string find, string replace, string reason)
    {
        var xml = SampleXml().Replace(find, replace);

        var error = Assert.Throws<SourceXmlException>(() => SourceXmlReader.Read(Source(), xml, TestSourceXml.Limits));

        Assert.NotEmpty(error.Problems);
        Assert.All(error.Problems, p => Assert.True(p.Line > 0, reason));
    }

    [Fact]
    public void Malformed_dtd_wrong_root_and_oversized_xml_are_refused()
    {
        var source = Source();

        Assert.Throws<SourceXmlException>(() => SourceXmlReader.Read(source, "<NotaErp><sem-fechar>", TestSourceXml.Limits));
        Assert.Throws<SourceXmlException>(() => SourceXmlReader.Read(source,
            "<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY e SYSTEM \"file:///c:/windows/win.ini\">]><NotaErp>&e;</NotaErp>", TestSourceXml.Limits));
        var wrongRoot = Assert.Throws<SourceXmlException>(() => SourceXmlReader.Read(source,
            "<Outra xmlns=\"http://exemplo.ilustrativo.tradux/erp/v1\"/>", TestSourceXml.Limits));
        Assert.Contains(wrongRoot.Problems, p => p.Message.Contains("raiz", StringComparison.Ordinal));
        Assert.Throws<SourceXmlException>(() => SourceXmlReader.Read(source, SampleXml(), TestSourceXml.Limits with { MaxCharacters = 200 }));
    }

    [Fact]
    public void Source_import_types_them_and_destination_writes_them_as_validated_text()
    {
        // xs:int, xs:boolean, xs:dateTime: a origem tipa (número/texto/data); o destino os escreve
        // como texto e a validação XSD do XML gerado aplica o tipo (correção pós-Fase 12).
        const string xsd = """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xs:element name="Pedido">
                <xs:complexType>
                  <xs:sequence>
                    <xs:element name="Quantidade" type="xs:int"/>
                    <xs:element name="Ativo" type="xs:boolean"/>
                    <xs:element name="Quando" type="xs:dateTime"/>
                  </xs:sequence>
                </xs:complexType>
              </xs:element>
            </xs:schema>
            """;
        var files = new Dictionary<string, byte[]> { ["p.xsd"] = Encoding.UTF8.GetBytes(xsd) };

        var source = XsdSchemaImporter.ImportSourceInMemory(files, "p.xsd");
        var tree = SourceXmlReader.Read(source, "<Pedido><Quantidade>3</Quantidade><Ativo>true</Ativo><Quando>2026-09-30T10:00:00-03:00</Quando></Pedido>", TestSourceXml.Limits);

        Assert.Equal(3m, tree["Quantidade"]);
        Assert.Equal("true", tree["Ativo"]);
        Assert.Equal("2026-09-30T10:00:00-03:00", tree["Quando"]);
        // Fase 15 (ADR-0018): xs:dateTime no destino tem tipo próprio; int/boolean seguem como texto.
        var destination = XsdSchemaImporter.ImportInMemory(files, "p.xsd").Schema.RootContent.Items.OfType<ScalarElementItem>().ToDictionary(i => i.Element.Name, i => i.Element.ScalarType);
        Assert.Equal(SchemaScalarType.String, destination["Quantidade"]);
        Assert.Equal(SchemaScalarType.String, destination["Ativo"]);
        Assert.Equal(SchemaScalarType.DateTime, destination["Quando"]);
    }
}

/// <summary>Revisão independente da Fase 12: regressões dos avisos 1–5.</summary>
public class SourceXmlReviewRegressionTests
{
    private static SourceSchema Import(string xsd) =>
        XsdSchemaImporter.ImportSourceInMemory(new Dictionary<string, byte[]> { ["s.xsd"] = Encoding.UTF8.GetBytes(xsd) }, "s.xsd");

    private const string XsdNs = "xmlns:xs=\"http://www.w3.org/2001/XMLSchema\"";

    [Fact]
    public void Invalid_xsi_nil_inside_xs_any_never_throws_a_500_class_exception()
    {
        var source = Import($"<xs:schema {XsdNs}><xs:element name=\"R\"><xs:complexType><xs:sequence><xs:any processContents=\"skip\" maxOccurs=\"unbounded\"/></xs:sequence></xs:complexType></xs:element></xs:schema>");

        var tree = SourceXmlReader.Read(source, "<R xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\"><x xsi:nil=\"abc\"/><y xsi:nil=\"true\"/></R>", TestSourceXml.Limits);

        Assert.NotNull(tree["x"]);
        Assert.Null(tree["y"]);
    }

    [Fact]
    public void Identity_constraints_of_the_source_xsd_are_enforced()
    {
        var source = Import($"<xs:schema {XsdNs}><xs:element name=\"R\"><xs:complexType><xs:sequence><xs:element name=\"i\" type=\"xs:string\" maxOccurs=\"unbounded\"/></xs:sequence></xs:complexType><xs:unique name=\"u\"><xs:selector xpath=\"i\"/><xs:field xpath=\".\"/></xs:unique></xs:element></xs:schema>");

        Assert.Throws<SourceXmlException>(() => SourceXmlReader.Read(source, "<R><i>1</i><i>1</i></R>", TestSourceXml.Limits));
        Assert.NotNull(SourceXmlReader.Read(source, "<R><i>1</i><i>2</i></R>", TestSourceXml.Limits));
    }

    [Fact]
    public void Exponentially_expanding_types_are_refused_quickly_instead_of_exhausting_memory()
    {
        var types = new StringBuilder();
        for (var i = 0; i < 30; i++)
        {
            types.Append($"<xs:complexType name=\"T{i}\"><xs:sequence><xs:element name=\"a\" type=\"T{i + 1}\"/><xs:element name=\"b\" type=\"T{i + 1}\"/></xs:sequence></xs:complexType>");
        }

        types.Append("<xs:complexType name=\"T30\"><xs:sequence><xs:element name=\"v\" type=\"xs:string\"/></xs:sequence></xs:complexType>");
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var error = Assert.Throws<NotSupportedException>(() => Import($"<xs:schema {XsdNs}><xs:element name=\"R\" type=\"T0\"/>{types}</xs:schema>"));

        Assert.Contains("se expande em mais de", error.Message, StringComparison.Ordinal);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"levou {clock.Elapsed}");
    }

    [Fact]
    public void Attribute_types_and_root_simple_content_match_what_the_editor_shows()
    {
        var source = Import($"<xs:schema {XsdNs}><xs:element name=\"R\"><xs:complexType><xs:simpleContent><xs:extension base=\"xs:string\"><xs:attribute name=\"n\" type=\"xs:decimal\"/></xs:extension></xs:simpleContent></xs:complexType></xs:element></xs:schema>");

        var tree = SourceXmlReader.Read(source, "<R n=\"10.5\">texto</R>", TestSourceXml.Limits);

        Assert.Equal(SourceValueType.Number, source.Shape.Fields.Single(f => f.Name == "@n").ValueType);
        Assert.Equal(10.5m, tree["@n"]);
        Assert.Equal("texto", tree["$value"]);
    }
}

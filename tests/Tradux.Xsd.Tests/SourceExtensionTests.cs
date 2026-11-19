using Tradux.Domain.SourceContracts;
using Tradux.Domain.SourceData;
namespace Tradux.Xsd.Tests;

/// <summary>Fase 13 (ADR-0016): contrato de entrada = XSD base + extensões montadas na árvore.</summary>
public class SourceExtensionTests
{
    private static SourceSchema Base() => XsdSchemaImporter.ImportSourceInMemory(
        new Dictionary<string, byte[]> { ["origem-exemplo.xsd"] = File.ReadAllBytes(RepositoryPaths.Combine("docs", "schemas", "origem-exemplo.xsd")) },
        "origem-exemplo.xsd");

    // Bloco próprio na raiz (como o infErp dentro da DPS) + campo extra dentro de um bloco do XSD.
    internal static IReadOnlyList<SourceExtension> Extensions() =>
    [
        new("", new SourceExtensionNode("infErp", IsObject: true, Children:
        [
            new SourceExtensionNode("Pedido", IsObject: false, Required: true),
            new SourceExtensionNode("Faturas", IsObject: true, Children:
            [
                new SourceExtensionNode("Fatura", IsObject: true, Repeated: true, Children:
                [
                    new SourceExtensionNode("Numero", IsObject: false, Required: true),
                    new SourceExtensionNode("Valor", IsObject: false, SourceValueType.Number),
                    new SourceExtensionNode("Vencimento", IsObject: false, SourceValueType.Date),
                ]),
            ]),
        ])),
        new("Servico", new SourceExtensionNode("Observacao", IsObject: false)),
    ];

    private const string Block = """
          <infErp>
            <Pedido>PED-9</Pedido>
            <Faturas>
              <Fatura><Numero>1</Numero><Valor>100.50</Valor><Vencimento>2026-09-25</Vencimento></Fatura>
              <Fatura><Numero>2</Numero><Valor>67.00</Valor></Fatura>
            </Faturas>
          </infErp>
        """;

    private static string Xml(string block = Block, string observacao = "<Observacao>urgente</Observacao>") =>
        File.ReadAllText(RepositoryPaths.Combine("docs", "schemas", "origem-exemplo.xml"))
            .Replace("</Itens>", "</Itens>" + block)
            .Replace("<Codigo>01.02.03</Codigo>", "<Codigo>01.02.03</Codigo>" + observacao);

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
    public void Extensions_are_merged_into_the_shape_and_marked()
    {
        var shape = Base().WithExtensions(Extensions()).Shape;

        var block = shape.Fields.Single(f => f.Name == "infErp");
        Assert.True(block.IsExtension && block.IsObject);
        Assert.True(block.Children.Single(c => c.Name == "Faturas").Children.Single(c => c.Name == "Fatura").Repeated);
        Assert.Contains(shape.Fields.Single(f => f.Name == "Servico").Children, c => c.Name == "Observacao" && c.IsExtension);
        Assert.False(shape.Fields.Single(f => f.Name == "Servico").IsExtension);
    }

    [Theory]
    [InlineData("NaoExiste", "Campo", "não existe")]
    [InlineData("Servico.Codigo", "Campo", "não é um bloco")]
    [InlineData("Servico", "Codigo", "já existe")]
    [InlineData("", "nome com espaço", "inválido")]
    [InlineData("", "", "inválido")]
    [InlineData("Servico.Valor", "Centavos", "valor próprio")] // conteúdo simples (moeda="BRL" + texto): revisão, achado 3
    public void Invalid_extensions_are_refused_with_a_clear_reason(string parent, string name, string reason)
    {
        var error = Assert.Throws<SourceExtensionException>(() =>
            Base().WithExtensions([new SourceExtension(parent, new SourceExtensionNode(name, IsObject: false))]));

        Assert.Contains(reason, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Xml_with_the_extension_block_is_validated_and_read_with_lists_and_types()
    {
        var tree = SourceXmlReader.Read(Base().WithExtensions(Extensions()), Xml(), TestSourceXml.Limits);

        Assert.Equal("PED-9", At<string>(tree, "infErp", "Pedido"));
        var faturas = At<List<object?>>(tree, "infErp", "Faturas", "Fatura");
        Assert.Equal(2, faturas.Count);
        Assert.Equal(100.50m, ((IReadOnlyDictionary<string, object?>)faturas[0]!)["Valor"]);
        Assert.Equal("urgente", At<string>(tree, "Servico", "Observacao"));
        Assert.Equal("000123", At<string>(tree, "Identificacao", "Numero")); // base continua igual
    }

    [Fact]
    public void Extension_block_can_be_anywhere_inside_its_parent()
    {
        var xml = Xml(block: "", observacao: "").Replace("<Identificacao>", Block + "<Identificacao>");

        var tree = SourceXmlReader.Read(Base().WithExtensions(Extensions()), xml, TestSourceXml.Limits);

        Assert.Equal("PED-9", At<string>(tree, "infErp", "Pedido"));
    }

    [Theory]
    [InlineData("<Pedido>PED-9</Pedido>", "", "Falta o elemento 'Pedido'")]
    [InlineData("<Pedido>PED-9</Pedido>", "<Pedido>PED-9</Pedido><Extra>x</Extra>", "não está previsto")]
    [InlineData("<Valor>100.50</Valor>", "<Valor>cem</Valor>", "número")]
    [InlineData("<Vencimento>2026-09-25</Vencimento>", "<Vencimento>25/09/2026</Vencimento>", "data ISO")]
    [InlineData("<Pedido>PED-9</Pedido>", "<Pedido>PED-9</Pedido><Pedido>PED-10</Pedido>", "não é repetível")]
    public void Extension_rules_refuse_the_xml_with_position(string find, string replace, string reason)
    {
        var error = Assert.Throws<SourceXmlException>(() => SourceXmlReader.Read(Base().WithExtensions(Extensions()), Xml(Block.Replace(find, replace)), TestSourceXml.Limits));

        Assert.Contains(error.Problems, p => p.Message.Contains(reason, StringComparison.Ordinal) && p.Line > 0);
    }

    [Fact]
    public void The_base_part_is_still_validated_by_the_xsd_and_unknown_blocks_are_refused()
    {
        var withExtensions = Base().WithExtensions(Extensions());

        Assert.Throws<SourceXmlException>(() => SourceXmlReader.Read(withExtensions, Xml().Replace("<Cnpj>12345678000199</Cnpj>", "<Cnpj>1</Cnpj>"), TestSourceXml.Limits));
        Assert.Throws<SourceXmlException>(() => SourceXmlReader.Read(Base(), Xml(), TestSourceXml.Limits)); // sem as extensões o XSD recusa o bloco
    }

    [Fact]
    public void Extension_without_node_or_with_null_child_is_refused_not_crashed()
    {
        Assert.Throws<SourceExtensionException>(() => Base().WithExtensions([new SourceExtension("", null!)]));
        Assert.Throws<SourceExtensionException>(() => Base().WithExtensions([null!]));
        Assert.Throws<SourceExtensionException>(() => Base().WithExtensions(
            [new SourceExtension("", new SourceExtensionNode("infErp", IsObject: true, Children: [null!]))]));
    }

    [Theory]
    [InlineData("<Pedido>PED-9</Pedido>", "<Pedido xmlns=\"urn:outro\">PED-9</Pedido>", "não está previsto")] // achado 6
    [InlineData("<Pedido>PED-9</Pedido>", "<Pedido xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xsi:nil=\"true\"/>", "não aceita atributos")] // achado 7
    public void Extension_elements_keep_the_parent_namespace_and_are_not_nillable(string find, string replace, string reason)
    {
        var error = Assert.Throws<SourceXmlException>(() => SourceXmlReader.Read(Base().WithExtensions(Extensions()), Xml(Block.Replace(find, replace)), TestSourceXml.Limits));

        Assert.Contains(error.Problems, p => p.Message.Contains(reason, StringComparison.Ordinal) && p.Line > 0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Deep_xml_is_refused_while_reading_without_stack_overflow(bool withExtensions)
    {
        // Revisão da Fase 13, achados 1 e 12: profundidade limitada durante a leitura.
        var source = withExtensions ? Base().WithExtensions(Extensions()) : Base();
        var deep = string.Concat(Enumerable.Repeat("<a>", 20_000)) + string.Concat(Enumerable.Repeat("</a>", 20_000));

        var error = Assert.Throws<SourceXmlException>(() => SourceXmlReader.Read(source, Xml().Replace("</Itens>", "</Itens>" + deep), TestSourceXml.Limits));

        Assert.Contains(error.Problems, p => p.Message.Contains("profundidade", StringComparison.Ordinal) && p.Line > 0);
    }

    [Fact]
    public void Many_occurrences_of_a_repeated_extension_are_validated_in_linear_time()
    {
        // Revisão da Fase 13, achado 2: antes 40 mil ocorrências levavam ~52 s.
        var source = Base().WithExtensions([new SourceExtension("", new SourceExtensionNode("e", IsObject: false, Repeated: true))]);
        var many = string.Concat(Enumerable.Repeat("<e>1</e>", 60_000));
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var tree = SourceXmlReader.Read(source, Xml(block: many, observacao: ""), TestSourceXml.Limits);

        Assert.Equal(60_000, ((List<object?>)tree["e"]!).Count);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"levou {watch.Elapsed}");
    }

    [Fact]
    public void Element_count_is_limited_while_reading()
    {
        var source = Base().WithExtensions([new SourceExtension("", new SourceExtensionNode("e", IsObject: false, Repeated: true))]);
        var many = string.Concat(Enumerable.Repeat("<e/>", 2_000));

        var error = Assert.Throws<SourceXmlException>(() =>
            SourceXmlReader.Read(source, Xml(block: many, observacao: ""), TestSourceXml.Limits with { MaxElements = 1_000 }));

        Assert.Contains(error.Problems, p => p.Message.Contains("elementos", StringComparison.Ordinal));
    }

    [Fact]
    public void Base_xsd_errors_keep_their_position_when_extensions_are_skipped()
    {
        var error = Assert.Throws<SourceXmlException>(() =>
            SourceXmlReader.Read(Base().WithExtensions(Extensions()), Xml().Replace("<Cnpj>12345678000199</Cnpj>", "<Cnpj>1</Cnpj>"), TestSourceXml.Limits));

        Assert.Contains(error.Problems, p => p.Line > 1 && p.Position > 0);
    }

    [Fact]
    public void Hash_ignores_json_shape_differences_of_the_same_extensions()
    {
        IReadOnlyList<SourceExtension> a = [new("", new SourceExtensionNode("b", IsObject: true, SourceValueType.Number, Children: null))];
        IReadOnlyList<SourceExtension> b = [new("", new SourceExtensionNode("b", IsObject: true, Children: []))];

        Assert.Equal(
            System.Text.Json.JsonSerializer.Serialize(SourceExtensionMerger.Normalize(a)),
            System.Text.Json.JsonSerializer.Serialize(SourceExtensionMerger.Normalize(b)));
    }

    // XSD com elementos locais sem namespace (comum em XSDs municipais): revisão da Fase 13, R1.
    private static SourceSchema Unqualified() => XsdSchemaImporter.ImportSourceInMemory(
        new Dictionary<string, byte[]>
        {
            ["u.xsd"] = System.Text.Encoding.UTF8.GetBytes("""
                <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:t" xmlns="urn:t" elementFormDefault="unqualified">
                  <xs:element name="R">
                    <xs:complexType><xs:sequence>
                      <xs:element name="A" maxOccurs="unbounded">
                        <xs:complexType><xs:sequence><xs:element name="B" type="xs:string"/></xs:sequence></xs:complexType>
                      </xs:element>
                    </xs:sequence></xs:complexType>
                  </xs:element>
                </xs:schema>
                """),
        },
        "u.xsd");

    [Fact]
    public void Extensions_follow_unqualified_local_elements()
    {
        var source = Unqualified().WithExtensions(
        [
            new SourceExtension("A", new SourceExtensionNode("X", IsObject: false, SourceValueType.Number, Required: true)),
            new SourceExtension("", new SourceExtensionNode("Y", IsObject: true, Children: [new SourceExtensionNode("Z", IsObject: false)])),
        ]);

        var tree = SourceXmlReader.Read(source, "<t:R xmlns:t='urn:t'><A><B>b</B><X>1</X></A><Y><Z>z</Z></Y></t:R>", TestSourceXml.Limits);

        Assert.Equal(1m, ((IReadOnlyDictionary<string, object?>)At<List<object?>>(tree, "A")[0]!)["X"]);
        Assert.Equal("z", At<string>(tree, "Y", "Z"));
        var error = Assert.Throws<SourceXmlException>(() => SourceXmlReader.Read(source, "<t:R xmlns:t='urn:t'><A><B>b</B><t:X>1</t:X></A><Y/></t:R>", TestSourceXml.Limits));
        Assert.Contains(error.Problems, p => p.Message.Contains("Falta o elemento 'X'", StringComparison.Ordinal));
    }

    [Fact]
    public void Many_extensions_under_many_parents_stay_linear()
    {
        // Revisão da Fase 13, A2: 2.000 extensões × 40 mil pais levavam ~21 s.
        var source = Unqualified().WithExtensions(Enumerable.Range(0, 2_000)
            .Select(i => new SourceExtension("A", new SourceExtensionNode($"X{i}", IsObject: false))).ToList());
        var xml = "<t:R xmlns:t='urn:t'>" + string.Concat(Enumerable.Repeat("<A><B>b</B><X7>1</X7></A>", 30_000)) + "</t:R>";
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var tree = SourceXmlReader.Read(source, xml, TestSourceXml.Limits);

        Assert.Equal(30_000, At<List<object?>>(tree, "A").Count);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"levou {watch.Elapsed}");
    }
}

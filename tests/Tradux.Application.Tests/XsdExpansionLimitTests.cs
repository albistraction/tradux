using System.Diagnostics;
using System.Text;
using Tradux.Application.Design;
using Tradux.Application.Snapshots;
using Tradux.Contracts.Design;
using Tradux.Domain.Mapping;
using Tradux.Tests.Shared;

namespace Tradux.Application.Tests;

/// <summary>
/// Revisão da correção pós-Fase 14, rodada 3: XSDs pequenos cuja árvore
/// <b>expandida</b> é funda ou exponencial. Antes, derrubavam o processo
/// (estouro de pilha no editor, depois da importação) ou consumiam GB de memória.
/// Tudo passa pelo serviço numa thread comum, como no servidor.
/// </summary>
public class XsdExpansionLimitTests
{
    private const string Ns = "xmlns:xs=\"http://www.w3.org/2001/XMLSchema\"";

    private static string Range(int n, Func<int, string> f) => string.Concat(Enumerable.Range(1, n).Select(f));

    // T_k = n níveis anônimos aninhados e, no fundo, um elemento do tipo T_{k-1}.
    private static string DeepElements(int types, int nesting) => $"""
        <xs:schema {Ns}>
          <xs:complexType name="T0"><xs:sequence><xs:element name="fim" type="xs:string"/></xs:sequence></xs:complexType>
          {Range(types, k => $"<xs:complexType name=\"T{k}\"><xs:sequence>{string.Concat(Enumerable.Repeat("<xs:element name=\"n\"><xs:complexType><xs:sequence>", nesting))}<xs:element name=\"c\" type=\"T{k - 1}\"/>{string.Concat(Enumerable.Repeat("</xs:sequence></xs:complexType></xs:element>", nesting))}</xs:sequence></xs:complexType>")}
          <xs:element name="R" type="T{types}"/>
        </xs:schema>
        """;

    private static void RefusedFast(Action action, string reason)
    {
        var watch = Stopwatch.StartNew();
        var error = Assert.Throws<ArgumentException>(action);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"levou {watch.Elapsed}");
        Assert.Contains(reason, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Deep_expanded_tree_is_refused_for_destination_and_source_instead_of_crashing_the_editor()
    {
        var service = new DraftDesignService();
        var xsd = SnapshotFixtures.Upload(DeepElements(types: 100, nesting: 40)); // altura de referências pequena, ~4.000 níveis expandidos

        RefusedFast(() => service.DescribeSchema(xsd), "mais de 128 níveis");
        RefusedFast(() => service.DescribeSource(xsd), "mais de 128 níveis");
    }

    [Fact]
    public void Exponential_fan_out_is_refused_for_destination()
    {
        // T_k com 4 elementos do tipo T_{k-1}: 4^12 nós a partir de 3 KB (antes: 63 s e 5,8 GB).
        var xsd = $"""
            <xs:schema {Ns}>
              <xs:complexType name="T0"><xs:sequence><xs:element name="v" type="xs:string"/></xs:sequence></xs:complexType>
              {Range(12, k => $"<xs:complexType name=\"T{k}\"><xs:sequence>{string.Concat(Enumerable.Range(0, 4).Select(i => $"<xs:element name=\"c{i}\" type=\"T{k - 1}\"/>"))}</xs:sequence></xs:complexType>")}
              <xs:element name="R" type="T12"/>
            </xs:schema>
            """;

        RefusedFast(() => new DraftDesignService().DescribeSchema(SnapshotFixtures.Upload(xsd)), "mais de 20000 elementos");
    }

    [Fact]
    public void Exponential_union_is_refused()
    {
        // S_k = união de 8 × S_{k-1}: 1 KB virava 10 s e 3,4 GB.
        var xsd = $"""
            <xs:schema {Ns}>
              <xs:simpleType name="S0"><xs:restriction base="xs:string"/></xs:simpleType>
              {Range(9, k => $"<xs:simpleType name=\"S{k}\"><xs:union memberTypes=\"{string.Join(' ', Enumerable.Repeat($"S{k - 1}", 8))}\"/></xs:simpleType>")}
              <xs:element name="R"><xs:complexType><xs:sequence><xs:element name="v" type="S9"/></xs:sequence></xs:complexType></xs:element>
            </xs:schema>
            """;

        RefusedFast(() => new DraftDesignService().DescribeSchema(SnapshotFixtures.Upload(xsd)), "membros de union/list");
    }

    [Theory]
    [InlineData("anyAttribute")]
    [InlineData("xpath")]
    public void Wildcard_namespaces_and_identity_xpath_are_bounded(string kind)
    {
        var xsd = kind == "anyAttribute"
            ? $"""<xs:schema {Ns}><xs:element name="R"><xs:complexType><xs:sequence><xs:element name="v" type="xs:string"/></xs:sequence><xs:anyAttribute namespace="{string.Join(' ', Enumerable.Range(0, 100).Select(i => $"urn:n{i}"))}"/></xs:complexType></xs:element></xs:schema>"""
            : $"""<xs:schema {Ns}><xs:element name="R"><xs:complexType><xs:sequence><xs:element name="a" type="xs:string" maxOccurs="unbounded"/></xs:sequence></xs:complexType><xs:unique name="u"><xs:selector xpath="{string.Join('|', Enumerable.Repeat("a", 100))}"/><xs:field xpath="."/></xs:unique></xs:element></xs:schema>""";

        RefusedFast(() => new DraftDesignService().DescribeSchema(SnapshotFixtures.Upload(xsd)), kind == "anyAttribute" ? "namespaces demais" : "XPath");
    }

    [Fact]
    public void Recursive_destination_type_in_a_snapshot_is_a_compilation_error_not_a_crash()
    {
        // Revisão (A3): fora do editor virava NotSupportedException sem tratamento (500).
        var xsd = $"""<xs:schema {Ns}><xs:complexType name="T"><xs:sequence><xs:element name="a" type="xs:string"/><xs:element name="c" type="T" minOccurs="0"/></xs:sequence></xs:complexType><xs:element name="R" type="T"/></xs:schema>""";
        var content = new SnapshotContent
        {
            SchemaFiles = new Dictionary<string, byte[]> { ["a.xsd"] = Encoding.UTF8.GetBytes(xsd) },
            SchemaEntryPoint = "a.xsd",
            Mapping = new MappingTemplate(),
        };

        Assert.Throws<SnapshotCompilationException>(() => new SnapshotCompiler().CompileContent(content));
    }

    // ---- Rodada 4 ----

    [Fact]
    public void Identity_constraints_have_a_combined_cost_limit()
    {
        // 100 xs:unique sobre .//a: 8 KB de XSD custavam 30 s e 2,3 GB por XML de entrada de 870 KB.
        var xsd = $"""
            <xs:schema {Ns}><xs:element name="R"><xs:complexType><xs:sequence><xs:element name="a" type="xs:string" maxOccurs="unbounded"/></xs:sequence></xs:complexType>
            {Range(100, i => $"<xs:unique name=\"u{i}\"><xs:selector xpath=\".//a\"/><xs:field xpath=\".\"/></xs:unique>")}
            </xs:element></xs:schema>
            """;

        RefusedFast(() => new DraftDesignService().DescribeSource(SnapshotFixtures.Upload(xsd)), "xs:key/xs:unique/xs:keyref demais");
    }

    [Fact]
    public void Anonymous_union_in_attribute_is_counted()
    {
        // União anônima em atributo escapava do limite: ~3 KB por atributo = 1 milhão de membros.
        var xsd = $"""
            <xs:schema {Ns}>
              <xs:simpleType name="S1"><xs:union memberTypes="xs:string xs:int"/></xs:simpleType>
              <xs:element name="R"><xs:complexType><xs:sequence><xs:element name="v" type="xs:string"/></xs:sequence>
                <xs:attribute name="x"><xs:simpleType><xs:union memberTypes="{string.Join(' ', Enumerable.Repeat("S1", 100))}"/></xs:simpleType></xs:attribute>
              </xs:complexType></xs:element>
            </xs:schema>
            """;

        RefusedFast(() => new DraftDesignService().DescribeSchema(SnapshotFixtures.Upload(xsd)), "membros de union/list");
    }

    [Fact]
    public void Structural_suite_with_deeply_nested_collections_stays_bounded()
    {
        // 13 coleções aninhadas com 3 itens cada: 3^13 objetos (28 s e 2,6 GB).
        const int depth = 13;
        var xsd = $"<xs:schema {Ns}><xs:element name=\"R\"><xs:complexType><xs:sequence>"
            + string.Concat(Enumerable.Range(1, depth).Select(i => $"<xs:element name=\"a{i}\" minOccurs=\"0\" maxOccurs=\"unbounded\"><xs:complexType><xs:sequence>"))
            + "<xs:element name=\"v\" type=\"xs:string\"/>"
            + string.Concat(Enumerable.Repeat("</xs:sequence></xs:complexType></xs:element>", depth))
            + "</xs:sequence></xs:complexType></xs:element></xs:schema>";
        MappingTemplate Level(int i) => i > depth
            ? new MappingTemplate { Fields = [new FieldMapping("v", new FieldReferenceNode("v"))] }
            : new MappingTemplate { Collections = [new CollectionMapping { DestinationElementName = $"a{i}", SourceCollectionPath = $"l{i}", ItemContent = Level(i + 1) }] };
        var watch = Stopwatch.StartNew();

        var result = new DraftDesignService().Quality(new DraftQualityRequest(SnapshotFixtures.Upload(xsd), Level(1)));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"levou {watch.Elapsed}");
        Assert.Contains(result.Report.NotCovered, gap => gap.Reason.Contains("Dados sintéticos grandes demais", StringComparison.Ordinal));
    }

    // ---- Rodada 5: orçamento de tempo total (cobre qualquer custo que cresça com o documento) ----

    private static Tradux.Xsd.SourceSchema Source(string xsd, string? root = null) =>
        Tradux.Xsd.XsdSchemaImporter.ImportSourceInMemory(new Dictionary<string, byte[]> { ["a.xsd"] = Encoding.UTF8.GetBytes(xsd) }, "a.xsd", root);

    private static void SourceRefusedWithin(Tradux.Xsd.SourceSchema source, string xml, TimeSpan max)
    {
        var watch = Stopwatch.StartNew();
        var error = Assert.Throws<Tradux.Xsd.SourceXmlException>(() => Tradux.Xsd.SourceXmlReader.Read(source, xml, Tradux.Application.Generation.GenerationLimits.Default.SourceXml));
        Assert.True(watch.Elapsed < max, $"levou {watch.Elapsed}");
        Assert.Contains(error.Problems, problem => problem.Message.Contains("tempo limite", StringComparison.Ordinal));
    }

    [Fact]
    public void Nested_identity_scopes_hit_the_validation_time_budget()
    {
        // 16 xs:unique num elemento recursivo: 25 KB de XML custavam 4 s; 830 KB, 116 s.
        var xsd = $"""
            <xs:schema {Ns}>
              <xs:element name="R"><xs:complexType><xs:sequence><xs:element ref="n" maxOccurs="unbounded"/></xs:sequence></xs:complexType></xs:element>
              <xs:element name="n">
                <xs:complexType><xs:sequence><xs:element ref="n" minOccurs="0"/></xs:sequence><xs:attribute name="v" type="xs:string"/></xs:complexType>
                {Range(16, i => $"<xs:unique name=\"u{i}\"><xs:selector xpath=\".//n\"/><xs:field xpath=\"@v\"/></xs:unique>")}
              </xs:element>
            </xs:schema>
            """;
        var chain = string.Concat(Enumerable.Range(0, 30).Select(i => $"<n v=\"{{0}}_{i}\">")) + string.Concat(Enumerable.Repeat("</n>", 30));
        var xml = "<R>" + string.Concat(Enumerable.Range(0, 2_000).Select(k => chain.Replace("{0}", k.ToString(System.Globalization.CultureInfo.InvariantCulture)))) + "</R>";

        SourceRefusedWithin(Source(xsd, "R"), xml, TimeSpan.FromSeconds(6));
    }

    [Fact]
    public void Ambiguous_pattern_on_many_values_hits_the_validation_time_budget()
    {
        // (a|aa){1,29}b0 logo abaixo do timeout por valor: 100 valores = 32 s.
        var xsd = $$"""
            <xs:schema {{Ns}}>
              <xs:simpleType name="P"><xs:restriction base="xs:string"><xs:pattern value="(a|aa){1,29}b0"/></xs:restriction></xs:simpleType>
              <xs:element name="R"><xs:complexType><xs:sequence><xs:element name="v" type="P" maxOccurs="unbounded"/></xs:sequence></xs:complexType></xs:element>
            </xs:schema>
            """;
        var xml = "<R>" + string.Concat(Enumerable.Repeat($"<v>{new string('a', 29)}c</v>", 100)) + "</R>";

        Tradux.Xsd.Security.RegexTimeout.Configure();
        SourceRefusedWithin(Source(xsd), xml, TimeSpan.FromSeconds(6));
    }

    [Fact]
    public void Structural_suite_with_required_nested_collections_and_many_fields_stays_bounded()
    {
        // 9 coleções aninhadas obrigatórias (minOccurs=3) × 200 campos: 41 s e 1,6 GB.
        const int depth = 9, fields = 200;
        var leafFields = string.Concat(Enumerable.Range(0, fields).Select(i => $"<xs:element name=\"f{i}\" type=\"xs:string\"/>"));
        var xsd = $"<xs:schema {Ns}><xs:element name=\"R\"><xs:complexType><xs:sequence>"
            + string.Concat(Enumerable.Range(1, depth).Select(i => $"<xs:element name=\"a{i}\" minOccurs=\"3\" maxOccurs=\"unbounded\"><xs:complexType><xs:sequence>{leafFields}"))
            + string.Concat(Enumerable.Repeat("</xs:sequence></xs:complexType></xs:element>", depth))
            + "</xs:sequence></xs:complexType></xs:element></xs:schema>";
        MappingTemplate Level(int i)
        {
            var own = Enumerable.Range(0, fields).Select(f => new FieldMapping($"f{f}", new FieldReferenceNode($"f{f}"))).ToList();
            return i > depth
                ? new MappingTemplate()
                : new MappingTemplate { Collections = [new CollectionMapping { DestinationElementName = $"a{i}", SourceCollectionPath = $"l{i}", ItemContent = new MappingTemplate { Fields = own, Collections = Level(i + 1).Collections } }] };
        }

        var before = GC.GetTotalMemory(forceFullCollection: true);
        var watch = Stopwatch.StartNew();
        var result = new DraftDesignService().Quality(new DraftQualityRequest(SnapshotFixtures.Upload(xsd), Level(1)));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(25), $"levou {watch.Elapsed}");
        Assert.True(GC.GetTotalMemory(forceFullCollection: false) - before < 1_500_000_000, "memória");
        Assert.False(result.Passed); // dados sintéticos cortados: reprova em vez de aprovar sem testar
    }

    // ---- Rodada 6: avaliações de pattern numa mesma leitura; prazo da suíte e do motor ----

    private static string PatternTypes(int count, int length) => string.Concat(Enumerable.Range(0, count).Select(i =>
        $"<xs:simpleType name=\"P{i}\"><xs:restriction base=\"xs:string\"><xs:pattern value=\"(a|aa){{1,{length}}}b{i}\"/></xs:restriction></xs:simpleType>"));

    [Fact]
    public void Union_of_many_patterns_is_refused_at_import()
    {
        // 64 patterns caros avaliados para um só valor, numa só leitura: 21 a 31 s por valor (origem e motor).
        var xsd = $"""
            <xs:schema {Ns}>{PatternTypes(64, 30)}
              <xs:simpleType name="U"><xs:union memberTypes="{string.Join(' ', Enumerable.Range(0, 64).Select(i => $"P{i}"))}"/></xs:simpleType>
              <xs:element name="R"><xs:complexType><xs:sequence><xs:element name="v" type="U"/></xs:sequence></xs:complexType></xs:element>
            </xs:schema>
            """;

        RefusedFast(() => new DraftDesignService().DescribeSource(SnapshotFixtures.Upload(xsd)), "avaliações de pattern por valor");
        RefusedFast(() => new DraftDesignService().DescribeSchema(SnapshotFixtures.Upload(xsd)), "avaliações de pattern por valor");
    }

    [Fact]
    public void Attributes_of_one_tag_have_a_pattern_evaluation_limit()
    {
        // 4 atributos × union de 4 patterns: 16 avaliações na mesma tag de abertura (antes: 86 s com 64 membros).
        var xsd = $"""
            <xs:schema {Ns}>{PatternTypes(4, 28)}
              <xs:simpleType name="U"><xs:union memberTypes="P0 P1 P2 P3"/></xs:simpleType>
              <xs:element name="R"><xs:complexType><xs:sequence><xs:element name="v" type="xs:string"/></xs:sequence>
                {Range(4, i => $"<xs:attribute name=\"a{i}\" type=\"U\"/>")}
              </xs:complexType></xs:element>
            </xs:schema>
            """;

        RefusedFast(() => new DraftDesignService().DescribeSource(SnapshotFixtures.Upload(xsd)), "avaliações de pattern por tag");
    }

    [Fact]
    public void Generation_stops_at_its_time_budget_with_many_expensive_values()
    {
        // Revisão (A2): orçamento total do motor. 40 valores × pattern caro (até 250 ms cada) passaria de 10 s.
        var xsd = $"""
            <xs:schema {Ns}>{PatternTypes(1, 29)}
              <xs:element name="R"><xs:complexType><xs:sequence>{Range(40, i => $"<xs:element name=\"v{i}\" type=\"P0\"/>")}</xs:sequence></xs:complexType></xs:element>
            </xs:schema>
            """;
        var mapping = new MappingTemplate { Fields = Enumerable.Range(1, 40).Select(i => new FieldMapping($"v{i}", new LiteralNode(new string('a', 29) + "c"))).ToList() };
        var watch = Stopwatch.StartNew();

        var result = new DraftDesignService().Test(new DraftTestRequest(SnapshotFixtures.Upload(xsd), mapping, System.Text.Json.JsonDocument.Parse("{}").RootElement));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(9), $"levou {watch.Elapsed}");
        Assert.False(result.Generation!.Success);
        var timeouts = result.Generation.Diagnostics.Count(d => d.Code == "XSD_VALIDATION_TIMEOUT");
        Assert.InRange(timeouts, 1, 25); // os que estouraram sozinhos + um ao esgotar o prazo (não 40)
    }

    [Fact]
    public void Suite_deadline_covers_synthetic_values_with_expensive_patterns()
    {
        // Revisão (C2): valores sintéticos com pattern caro ficavam fora do relógio (100 campos = 57 s).
        var xsd = $$"""
            <xs:schema {{Ns}}>
              <xs:simpleType name="B"><xs:restriction base="xs:string"><xs:pattern value="(a|aa){1,30}b0"/></xs:restriction></xs:simpleType>
              <xs:simpleType name="T"><xs:restriction base="B"><xs:pattern value="a{30}c"/></xs:restriction></xs:simpleType>
              <xs:element name="R"><xs:complexType><xs:sequence>{{Range(100, i => $"<xs:element name=\"f{i}\" type=\"T\"/>")}}</xs:sequence></xs:complexType></xs:element>
            </xs:schema>
            """;
        var mapping = new MappingTemplate { Fields = Enumerable.Range(1, 100).Select(i => new FieldMapping($"f{i}", new FieldReferenceNode($"f{i}"))).ToList() };
        var watch = Stopwatch.StartNew();

        var result = new DraftDesignService().Quality(new DraftQualityRequest(SnapshotFixtures.Upload(xsd), mapping));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"levou {watch.Elapsed}");
        Assert.False(result.Passed);
    }
}

using System.Diagnostics;
using System.Text;

namespace Tradux.Xsd.Tests;

/// <summary>
/// Tamanho de modelo de conteúdo limitado antes de compilar: a compilação do
/// .NET explode com milhares de elementos num bloco (10 mil opcionais → ~21 s).
/// </summary>
public class ContentModelSizeTests
{
    private static string Wrap(string content) =>
        "<xs:schema xmlns:xs=\"http://www.w3.org/2001/XMLSchema\"><xs:element name=\"R\"><xs:complexType><xs:sequence>" + content + "</xs:sequence></xs:complexType></xs:element></xs:schema>";

    private static string Optional(int count, string prefix = "e") =>
        string.Concat(Enumerable.Range(0, count).Select(i => $"<xs:element name=\"{prefix}{i}\" type=\"xs:string\" minOccurs=\"0\"/>"));

    private static ImportedXsd Import(string xsd, XsdImportSecurityOptions? options = null) =>
        XsdSchemaImporter.ImportInMemory(new Dictionary<string, byte[]> { ["a.xsd"] = Encoding.UTF8.GetBytes(xsd) }, "a.xsd", options: options);

    [Fact]
    public void Thousands_of_elements_in_one_block_are_refused_fast_instead_of_compiling_for_seconds()
    {
        var watch = Stopwatch.StartNew();

        var error = Assert.Throws<InvalidOperationException>(() => Import(Wrap(Optional(10_000))));

        Assert.Contains("mais de 1000 elementos num mesmo modelo de conteúdo", error.Message, StringComparison.Ordinal);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"levou {watch.Elapsed}");
    }

    [Theory]
    [InlineData("nested")]
    [InlineData("groupRef")]
    [InlineData("extension")]
    public void Inline_groups_group_references_and_extension_count_toward_the_same_block(string shape)
    {
        var xsd = shape switch
        {
            // 3 sequências internas de 400: o mesmo modelo de conteúdo tem 1.200.
            "nested" => Wrap(string.Concat(Enumerable.Range(0, 3).Select(j => $"<xs:sequence>{Optional(400, $"s{j}_")}</xs:sequence>"))),
            "groupRef" => $"""
                <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
                  <xs:group name="G"><xs:sequence>{Optional(600, "g")}</xs:sequence></xs:group>
                  <xs:element name="R"><xs:complexType><xs:sequence><xs:group ref="G"/>{Optional(600)}</xs:sequence></xs:complexType></xs:element>
                </xs:schema>
                """,
            _ => $"""
                <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
                  <xs:complexType name="Base"><xs:sequence>{Optional(600, "b")}</xs:sequence></xs:complexType>
                  <xs:element name="R"><xs:complexType><xs:complexContent><xs:extension base="Base"><xs:sequence>{Optional(600)}</xs:sequence></xs:extension></xs:complexContent></xs:complexType></xs:element>
                </xs:schema>
                """,
        };

        Assert.Throws<InvalidOperationException>(() => Import(xsd));
    }

    [Fact]
    public void Total_declarations_are_limited_and_limits_are_configurable()
    {
        // 50 blocos de 100 campos: cada bloco cabe, o total (5.051) não, com limite 5.000.
        var blocks = Wrap(string.Concat(Enumerable.Range(0, 50).Select(i =>
            $"<xs:element name=\"B{i}\" minOccurs=\"0\"><xs:complexType><xs:sequence>{Optional(100)}</xs:sequence></xs:complexType></xs:element>")));

        Assert.NotNull(Import(blocks));
        var error = Assert.Throws<InvalidOperationException>(() => Import(blocks, new XsdImportSecurityOptions(MaxElementDeclarations: 5_000)));
        Assert.Contains("mais de 5000 elementos", error.Message, StringComparison.Ordinal);
        Assert.NotNull(Import(Wrap(Optional(1_500)), new XsdImportSecurityOptions(MaxElementsPerContentModel: 2_000)));
    }

    [Fact]
    public void A_block_at_the_limit_still_imports()
    {
        Assert.NotNull(Import(Wrap(Optional(1_000))));
    }

    private const string Ns = "xmlns:xs=\"http://www.w3.org/2001/XMLSchema\"";

    private static void RefusedFast(string xsd, string reason, IReadOnlyDictionary<string, string>? others = null)
    {
        var files = new Dictionary<string, byte[]> { ["a.xsd"] = Encoding.UTF8.GetBytes(xsd) };
        foreach (var (name, content) in others ?? new Dictionary<string, string>())
        {
            files[name] = Encoding.UTF8.GetBytes(content);
        }

        var watch = Stopwatch.StartNew();
        var error = Assert.ThrowsAny<Exception>(() => XsdSchemaImporter.ImportInMemory(files, "a.xsd"));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"levou {watch.Elapsed}");
        Assert.True(error is InvalidOperationException or System.Xml.Schema.XmlSchemaException, error.ToString());
        Assert.Contains(reason, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Many_types_reusing_a_large_group_hit_the_global_budget()
    {
        // Revisão (C1): 300 tipos × grupo de 1.000 compilavam em ~11 s.
        RefusedFast($"""
            <xs:schema {Ns}>
              <xs:group name="G"><xs:sequence>{Optional(1_000)}</xs:sequence></xs:group>
              {string.Concat(Enumerable.Range(0, 300).Select(i => $"<xs:complexType name=\"T{i}\"><xs:sequence><xs:group ref=\"G\"/></xs:sequence></xs:complexType>"))}
              <xs:element name="R" type="T0"/>
            </xs:schema>
            """, "somam mais de 20000");
    }

    [Fact]
    public void Long_group_reference_chain_is_refused_not_counted_as_zero()
    {
        // Revisão (C2): 33 níveis com 9.900 opcionais no fundo compilavam em ~21 s.
        // Declarada do topo para o fundo: a contagem desce a cadeia inteira; acima de 256 níveis, recusa (protege a pilha).
        var chain = string.Concat(Enumerable.Range(1, 300).Reverse().Select(k => $"<xs:group name=\"g{k}\"><xs:sequence><xs:group ref=\"g{k - 1}\"/></xs:sequence></xs:group>"));
        RefusedFast($"""
            <xs:schema {Ns}>
              <xs:element name="R"><xs:complexType><xs:sequence><xs:group ref="g300"/></xs:sequence></xs:complexType></xs:element>
              {chain}
              <xs:group name="g0"><xs:sequence>{Optional(5)}</xs:sequence></xs:group>
            </xs:schema>
            """, "mais de 256 níveis");
        RefusedFast($"""
            <xs:schema {Ns}>
              <xs:group name="g0"><xs:sequence>{Optional(5_000)}</xs:sequence></xs:group>
              {string.Concat(Enumerable.Range(1, 33).Select(k => $"<xs:group name=\"g{k}\"><xs:sequence><xs:group ref=\"g{k - 1}\"/></xs:sequence></xs:group>"))}
              <xs:element name="R"><xs:complexType><xs:sequence><xs:group ref="g33"/></xs:sequence></xs:complexType></xs:element>
            </xs:schema>
            """, "mais de 1000 elementos");
    }

    [Fact]
    public void Redefine_is_refused()
    {
        // Revisão (C3): itens de xs:redefine não eram contados (10 mil → ~27 s).
        RefusedFast($"""
            <xs:schema {Ns}>
              <xs:redefine schemaLocation="b.xsd"><xs:group name="G"><xs:sequence><xs:group ref="G"/>{Optional(5_000)}</xs:sequence></xs:group></xs:redefine>
              <xs:element name="R"><xs:complexType><xs:sequence><xs:group ref="G"/></xs:sequence></xs:complexType></xs:element>
            </xs:schema>
            """, "xs:redefine", new Dictionary<string, string>
        {
            ["b.xsd"] = $"""<xs:schema {Ns}><xs:group name="G"><xs:sequence><xs:element name="b" type="xs:string"/></xs:sequence></xs:group></xs:schema>""",
        });
    }

    [Fact]
    public void Self_referencing_and_fan_out_groups_do_not_hang_the_checker()
    {
        // Revisão (C4): 310 bytes travavam o verificador por mais de 120 s; leque 100×4 levava ~7 s.
        RefusedFast($"""
            <xs:schema {Ns}>
              <xs:group name="g"><xs:sequence><xs:group ref="g" minOccurs="0"/><xs:group ref="g" minOccurs="0"/></xs:sequence></xs:group>
              <xs:element name="R"><xs:complexType><xs:sequence><xs:group ref="g"/></xs:sequence></xs:complexType></xs:element>
            </xs:schema>
            """, "circular");

        string Level(int k) => k == 0
            ? "<xs:group name=\"n0\"><xs:sequence/></xs:group>"
            : $"<xs:group name=\"n{k}\"><xs:sequence>{string.Concat(Enumerable.Repeat($"<xs:group ref=\"n{k - 1}\"/>", 100))}</xs:sequence></xs:group>";
        var fan = $"""
            <xs:schema {Ns}>
              {string.Concat(Enumerable.Range(0, 5).Select(Level))}
              <xs:element name="R"><xs:complexType><xs:sequence><xs:element name="x" type="xs:string"/><xs:group ref="n4"/></xs:sequence></xs:complexType></xs:element>
            </xs:schema>
            """;
        var watch = Stopwatch.StartNew();
        Import(fan); // grupos vazios: aceito, e rápido
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"levou {watch.Elapsed}");
    }

    [Fact]
    public void Deep_anonymous_nesting_is_refused_before_it_can_overflow_the_stack()
    {
        // Revisão (C5): mil níveis estouravam a pilha (processo derrubado).
        const int depth = 3_000;
        var open = string.Concat(Enumerable.Range(0, depth).Select(i => $"<xs:element name=\"e{i}\"><xs:complexType><xs:sequence>"));
        var close = string.Concat(Enumerable.Repeat("</xs:sequence></xs:complexType></xs:element>", depth));
        RefusedFast($"<xs:schema {Ns}>{open}<xs:element name=\"fim\" type=\"xs:string\"/>{close}</xs:schema>", "aninhamento XML acima de 128");
    }

    [Fact]
    public void Undeclared_group_reports_the_real_schema_error()
    {
        // Revisão (A3): o erro do Compile era engolido e a mensagem final enganava.
        RefusedFast($"""
            <xs:schema {Ns}><xs:element name="R"><xs:complexType><xs:sequence><xs:group ref="Nope"/></xs:sequence></xs:complexType></xs:element></xs:schema>
            """, "Nope");
    }

    // ---- Revisão, rodada 2: cadeias que estouravam a pilha (o processo caía, sem 400/500) ----

    private static string Chain(int n, Func<int, string> link, string leaf, string root) =>
        $"<xs:schema {Ns}>{leaf}{string.Concat(Enumerable.Range(1, n).Select(link))}{root}</xs:schema>";

    public static TheoryData<string, string> ReferenceChains() => new()
    {
        { "simpleType restriction", Chain(8_000, k => $"<xs:simpleType name=\"s{k}\"><xs:restriction base=\"s{k - 1}\"/></xs:simpleType>",
            "<xs:simpleType name=\"s0\"><xs:restriction base=\"xs:string\"/></xs:simpleType>", "<xs:element name=\"R\"><xs:complexType><xs:sequence><xs:element name=\"v\" type=\"s8000\"/></xs:sequence></xs:complexType></xs:element>") },
        { "union", Chain(8_000, k => $"<xs:simpleType name=\"s{k}\"><xs:union memberTypes=\"s{k - 1}\"/></xs:simpleType>",
            "<xs:simpleType name=\"s0\"><xs:restriction base=\"xs:string\"/></xs:simpleType>", "<xs:element name=\"R\"><xs:complexType><xs:sequence><xs:element name=\"v\" type=\"s8000\"/></xs:sequence></xs:complexType></xs:element>") },
        { "complexContent restriction", Chain(6_000, k => $"<xs:complexType name=\"t{k}\"><xs:complexContent><xs:restriction base=\"t{k - 1}\"><xs:sequence><xs:element name=\"a\" type=\"xs:string\"/></xs:sequence></xs:restriction></xs:complexContent></xs:complexType>",
            "<xs:complexType name=\"t0\"><xs:sequence><xs:element name=\"a\" type=\"xs:string\"/></xs:sequence></xs:complexType>", "<xs:element name=\"R\" type=\"t6000\"/>") },
        { "simpleContent extension", Chain(6_000, k => $"<xs:complexType name=\"t{k}\"><xs:simpleContent><xs:extension base=\"t{k - 1}\"/></xs:simpleContent></xs:complexType>",
            "<xs:complexType name=\"t0\"><xs:simpleContent><xs:extension base=\"xs:string\"/></xs:simpleContent></xs:complexType>", "<xs:element name=\"R\"><xs:complexType><xs:sequence><xs:element name=\"v\" type=\"t6000\"/></xs:sequence></xs:complexType></xs:element>") },
        { "attributeGroup", Chain(8_000, k => $"<xs:attributeGroup name=\"ag{k}\"><xs:attributeGroup ref=\"ag{k - 1}\"/></xs:attributeGroup>",
            "<xs:attributeGroup name=\"ag0\"><xs:attribute name=\"x\" type=\"xs:string\"/></xs:attributeGroup>", "<xs:element name=\"R\"><xs:complexType><xs:sequence><xs:element name=\"a\" type=\"xs:string\"/></xs:sequence><xs:attributeGroup ref=\"ag8000\"/></xs:complexType></xs:element>") },
        { "tipos aninhados por elemento", Chain(8_000, k => $"<xs:complexType name=\"t{k}\"><xs:sequence><xs:element name=\"c\" type=\"t{k - 1}\"/></xs:sequence></xs:complexType>",
            "<xs:complexType name=\"t0\"><xs:sequence><xs:element name=\"a\" type=\"xs:string\"/></xs:sequence></xs:complexType>", "<xs:element name=\"R\" type=\"t8000\"/>") },
    };

    [Theory]
    [MemberData(nameof(ReferenceChains))]
    public void Long_reference_chains_of_any_kind_are_refused_before_compiling(string kind, string xsd)
    {
        Assert.NotEmpty(kind);
        RefusedFast(xsd, "mais de 256 níveis");
    }

    [Fact]
    public void Chain_height_does_not_depend_on_declaration_order_or_on_which_file_declares_it()
    {
        // Revisão (C2): folha primeiro em a.xsd e o começo da cadeia num incluído enganavam a profundidade.
        var leafFirst = string.Concat(Enumerable.Range(1, 4_000).Reverse().Select(k => $"<xs:group name=\"g{k}\"><xs:sequence><xs:group ref=\"g{k + 1}\" minOccurs=\"0\"/></xs:sequence></xs:group>"))
            .Replace("<xs:group ref=\"g4001\" minOccurs=\"0\"/>", "<xs:element name=\"fim\" type=\"xs:string\"/>");
        RefusedFast($"""<xs:schema {Ns}><xs:include schemaLocation="b.xsd"/>{leafFirst}</xs:schema>""", "mais de 256 níveis", new Dictionary<string, string>
        {
            ["b.xsd"] = $"""<xs:schema {Ns}><xs:group name="g0"><xs:sequence><xs:group ref="g1"/></xs:sequence></xs:group><xs:element name="R"><xs:complexType><xs:sequence><xs:group ref="g0"/></xs:sequence></xs:complexType></xs:element></xs:schema>""",
        });
    }

    [Fact]
    public void Recursive_destination_type_is_refused_but_source_still_accepts_it()
    {
        // Revisão (C3): 252 bytes derrubavam o processo na montagem da árvore de destino.
        var xsd = $"""<xs:schema {Ns}><xs:complexType name="T"><xs:sequence><xs:element name="a" type="xs:string"/><xs:element name="c" type="T" minOccurs="0"/></xs:sequence></xs:complexType><xs:element name="R" type="T"/></xs:schema>""";

        var error = Assert.Throws<NotSupportedException>(() => Import(xsd));
        Assert.Contains("tipo recursivo", error.Message, StringComparison.Ordinal);
        var source = XsdSchemaImporter.ImportSourceInMemory(new Dictionary<string, byte[]> { ["a.xsd"] = Encoding.UTF8.GetBytes(xsd) }, "a.xsd");
        Assert.Contains(source.Shape.Fields, f => f.Name == "c"); // origem: corta no 2º nível, como antes
    }

    [Fact]
    public void Attributes_count_per_type_and_in_total()
    {
        // Revisão (C4): 8.000 tipos × attributeGroup de 8.000 atributos levavam 25 s e 5,2 GB.
        string Attrs(int n) => string.Concat(Enumerable.Range(0, n).Select(i => $"<xs:attribute name=\"a{i}\" type=\"xs:string\"/>"));
        RefusedFast($"""<xs:schema {Ns}><xs:element name="R"><xs:complexType><xs:sequence><xs:element name="x" type="xs:string"/></xs:sequence>{Attrs(600)}</xs:complexType></xs:element></xs:schema>""",
            "mais de 500 atributos");
        RefusedFast($"""
            <xs:schema {Ns}>
              <xs:attributeGroup name="AG">{Attrs(400)}</xs:attributeGroup>
              {string.Concat(Enumerable.Range(0, 2_000).Select(i => $"<xs:complexType name=\"T{i}\"><xs:attributeGroup ref=\"AG\"/></xs:complexType>"))}
              <xs:element name="R"><xs:complexType><xs:sequence><xs:element name="x" type="T0"/></xs:sequence></xs:complexType></xs:element>
            </xs:schema>
            """, "somam mais de 20000 atributos");
    }
}

using System.Text.Json;
using System.Xml.Linq;
using Tradux.Application.Layers;
using Tradux.Application.Snapshots;
using Tradux.Domain.Mapping;

using Tradux.Application.Testing;
namespace Tradux.Tests.Shared;

/// <summary>
/// Fixtures de snapshot compartilhadas (arquivo linkado em mais de um
/// projeto de teste): XSDs reais de docs/schemas/, entradas JSON/XML e
/// cenários funcionais.
/// </summary>
internal static class SnapshotFixtures
{
    public const string Organization = "org-a";
    public const string OtherOrganization = "org-b";
    public const string Operation = "emissao";

    public static string SchemaPath(string fileName) => RepositoryPaths.Combine("docs", "schemas", fileName);

    /// <summary>XSDs de <c>docs/schemas</c> como upload do editor; o primeiro é o ponto de entrada.</summary>
    public static Contracts.Design.SchemaUpload UploadFile(params string[] fileNames) => new(
        fileNames.Select(n => new Contracts.Design.SchemaFileUpload(n, Convert.ToBase64String(File.ReadAllBytes(SchemaPath(n))))).ToList(),
        fileNames[0]);

    /// <summary>XSD inline como upload de um arquivo só.</summary>
    public static Contracts.Design.SchemaUpload Upload(string xsd, string fileName = "a.xsd") =>
        new([new Contracts.Design.SchemaFileUpload(fileName, Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(xsd)))], fileName);

    // ---- DPS simples (docs/schemas/exemplo-simples.xsd) ----

    /// <param name="numeroPrefix">Quando informado, <c>numero</c> vira <c>concat(prefixo, Numero)</c> — usado para distinguir versões.</param>
    public static SnapshotContent SimpleDps(string? numeroPrefix = null)
    {
        RuleNode numero = numeroPrefix is null
            ? new FieldReferenceNode("Numero")
            : OperatorNode.Of("concat", new LiteralNode(numeroPrefix), new FieldReferenceNode("Numero"));

        return new SnapshotContent
        {
            SchemaFiles = new Dictionary<string, byte[]> { ["exemplo-simples.xsd"] = File.ReadAllBytes(SchemaPath("exemplo-simples.xsd")) },
            SchemaEntryPoint = "exemplo-simples.xsd",
            Mapping = new MappingTemplate
            {
                Fields =
                [
                    new FieldMapping("numero", numero),
                    FieldMapping.Direct("Serie", "serie"),
                    FieldMapping.Direct("DataEmissao", "dataEmissao"),
                    FieldMapping.Direct("Competencia", "competencia"),
                    FieldMapping.Direct("ValorServico", "valorServico"),
                    FieldMapping.Direct("ValorDesconto", "valorDesconto"),
                    FieldMapping.Direct("CodigoServico", "codigoServico"),
                    FieldMapping.Direct("CnpjPrestador", "cnpjPrestador"),
                    FieldMapping.Direct("CnpjTomador", "cnpjTomador"),
                    new FieldMapping(
                        "discriminacaoServico",
                        OperatorNode.Of("lookup", new FieldReferenceNode("CodigoSituacao"), new LiteralNode("situacoes")),
                        Condition: OperatorNode.Of("notEmpty", new FieldReferenceNode("CodigoSituacao"))),
                    FieldMapping.Direct("CodigoSituacao", "codigoSituacao"),
                ],
            },
            LookupTables = new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["situacoes"] = new Dictionary<string, string> { ["1"] = "Situação normal", ["2"] = "Cancelada", ["3"] = "Substituída" },
            },
        };
    }

    /// <summary>
    /// DPS com regra sobre datas: a
    /// discriminação só é preenchida se <c>DataEmissao &gt; 2026-01-01</c>
    /// (literal em texto ISO) e a
    /// competência for <c>"2026-09"</c> (texto gYearMonth) e a data/hora de
    /// emissão com fuso (<c>10:00-03:00</c>) for posterior a <c>12:00Z</c> — o
    /// formato de emissão de documento fiscal. O literal persistido como texto continua
    /// comparado como data.
    /// </summary>
    public const string DateRuleExpectedText =
        "Emitida no exercício corrente: 28/09/2026 10:00 -03:00 | 2026 | dia do teste | 2026-09-28T10:00:00-03:00 | 2026-09-28T09:30:00";

    public static SnapshotContent SimpleDpsWithDateRule()
    {
        var content = SimpleDps();
        var condition = OperatorNode.Of(
            "and",
            OperatorNode.Of("greaterThan", new FieldReferenceNode("DataEmissao"), new LiteralNode("2026-01-01")),
            OperatorNode.Of("equals", new FieldReferenceNode("Competencia"), new LiteralNode("2026-09")),
            // Campos opcionais comparados com default: a suíte estrutural da
            // Fase 8 (caso "obrigatorios") reprovava a regra — comparar nulo é
            // erro crítico, e a entrada mínima válida não traz esses campos.
            OperatorNode.Of(
                "greaterThan",
                OperatorNode.Of("setDefault", new FieldReferenceNode("DataHoraEmissao"), new LiteralNode("1900-01-01T00:00:00+00:00")),
                new LiteralNode("2026-09-28T12:00:00Z")),
            OperatorNode.Of(
                "lessThan",
                OperatorNode.Of("setDefault", new FieldReferenceNode("DataHoraRegistro"), new LiteralNode("9999-12-31T00:00:00")),
                new LiteralNode("2026-09-28T10:00:00")));

        // Todo operador que produz texto a partir de data: formatDate,
        // substring, lookup por data e concat de DateTimeOffset/DateTime.
        RuleNode Lit(string s) => new LiteralNode(s);
        var text = OperatorNode.Of(
            "concat",
            Lit("Emitida no exercício corrente: "),
            OperatorNode.Of("formatDate", new FieldReferenceNode("DataEmissao"), Lit("dd/MM/yyyy")),
            Lit(" "),
            OperatorNode.Of("formatDate", new FieldReferenceNode("DataHoraEmissao"), Lit("HH:mm zzz")),
            Lit(" | "),
            OperatorNode.Of("substring", new FieldReferenceNode("DataEmissao"), new LiteralNode(0m), new LiteralNode(4m)),
            Lit(" | "),
            OperatorNode.Of("lookup", new FieldReferenceNode("DataEmissao"), Lit("dias")),
            Lit(" | "),
            new FieldReferenceNode("DataHoraEmissao"),
            Lit(" | "),
            new FieldReferenceNode("DataHoraRegistro"));

        var lookups = content.LookupTables.ToDictionary(t => t.Key, t => t.Value);
        lookups["dias"] = new Dictionary<string, string> { ["2026-09-28"] = "dia do teste" };

        return content with
        {
            Mapping = new MappingTemplate
            {
                Fields = content.Mapping.Fields
                    .Select(f => f.DestinationElementName == "discriminacaoServico"
                        ? new FieldMapping("discriminacaoServico", text, condition)
                        : f)
                    .ToList(),
            },
            LookupTables = lookups,
        };
    }

    /// <summary>DPS realista, como a API receberia (datas na forma canônica do XSD).</summary>
    public static JsonElement DpsJson(string cnpjPrestador = "12345678000199") => Json($$"""
        {
          "Numero": "000123",
          "Serie": "A1",
          "DataEmissao": "2026-09-28",
          "Competencia": "2026-09",
          "ValorServico": 1500.50,
          "ValorDesconto": null,
          "CodigoServico": "01.02.03",
          "CnpjPrestador": "{{cnpjPrestador}}",
          "CnpjTomador": "98765432000188",
          "DataHoraEmissao": "2026-09-28T10:00:00-03:00",
          "DataHoraRegistro": "2026-09-28T09:30:00",
          "CodigoSituacao": "1"
        }
        """);

    // ---- Pedido com coleção (docs/schemas/exemplo-colecao.xsd) ----

    public static SnapshotContent PedidoComItens() => new()
    {
        SchemaFiles = new Dictionary<string, byte[]> { ["exemplo-colecao.xsd"] = File.ReadAllBytes(SchemaPath("exemplo-colecao.xsd")) },
        SchemaEntryPoint = "exemplo-colecao.xsd",
        Mapping = new MappingTemplate
        {
            Fields = [FieldMapping.Direct("Numero", "numero"), FieldMapping.Direct("ValorTotal", "valorTotal")],
            Complex =
            [
                new ComplexMapping
                {
                    DestinationElementName = "itens",
                    Content = new MappingTemplate
                    {
                        Collections =
                        [
                            new CollectionMapping
                            {
                                DestinationElementName = "item",
                                SourceCollectionPath = "Itens",
                                Filter = OperatorNode.Of("greaterThan", new FieldReferenceNode("ValorUnitario"), new LiteralNode(0m)),
                                ItemContent = new MappingTemplate
                                {
                                    Fields =
                                    [
                                        new FieldMapping(
                                            "descricao",
                                            OperatorNode.Of("concat", new FieldReferenceNode("$index"), new LiteralNode(": "), new FieldReferenceNode("Descricao"))),
                                        FieldMapping.Direct("ValorUnitario", "valorUnitario"),
                                    ],
                                },
                            },
                        ],
                    },
                },
            ],
        },
    };

    public static JsonElement PedidoJson() => Json("""
        {
          "Numero": "PED-001",
          "ValorTotal": 150,
          "Itens": [
            { "Descricao": "Consultoria", "ValorUnitario": 100 },
            { "Descricao": "Cortesia", "ValorUnitario": 0 },
            { "Descricao": "Suporte", "ValorUnitario": 50 }
          ]
        }
        """);

    // ---- Contrato de origem XSD e entrada XML (Fase 12, ADR-0015) ----

    /// <summary>XSD de origem ilustrativo (export de ERP) — <c>docs/schemas/origem-exemplo.xsd</c>.</summary>
    public static SourceSchemaContent ErpSourceSchema() =>
        new(new Dictionary<string, byte[]> { ["origem-exemplo.xsd"] = File.ReadAllBytes(SchemaPath("origem-exemplo.xsd")) }, "origem-exemplo.xsd");

    /// <summary>XML de origem válido — <c>docs/schemas/origem-exemplo.xml</c>.</summary>
    public static string ErpSourceXml() => File.ReadAllText(SchemaPath("origem-exemplo.xml"));

    /// <summary>
    /// DPS de destino alimentada pelo XML do ERP: caminhos aninhados, texto de
    /// conteúdo simples (<c>Servico.Valor.$value</c>), atributo
    /// (<c>@situacao</c>) e elemento nulo (<c>Servico.Desconto</c>).
    /// </summary>
    public static SnapshotContent ErpToDps() => new()
    {
        SchemaFiles = new Dictionary<string, byte[]> { ["exemplo-simples.xsd"] = File.ReadAllBytes(SchemaPath("exemplo-simples.xsd")) },
        SchemaEntryPoint = "exemplo-simples.xsd",
        SourceSchema = ErpSourceSchema(),
        Mapping = new MappingTemplate
        {
            Fields =
            [
                FieldMapping.Direct("Identificacao.Numero", "numero"),
                FieldMapping.Direct("Identificacao.Serie", "serie"),
                FieldMapping.Direct("Identificacao.DataEmissao", "dataEmissao"),
                FieldMapping.Direct("Identificacao.Competencia", "competencia"),
                FieldMapping.Direct("Servico.Valor.$value", "valorServico"),
                FieldMapping.Direct("Servico.Desconto", "valorDesconto"),
                FieldMapping.Direct("Servico.Codigo", "codigoServico"),
                FieldMapping.Direct("Prestador.Cnpj", "cnpjPrestador"),
                FieldMapping.Direct("Tomador.Cnpj", "cnpjTomador"),
                FieldMapping.Direct("Servico.Discriminacao", "discriminacaoServico"),
                FieldMapping.Direct("@situacao", "codigoSituacao"),
            ],
        },
    };

    /// <summary>Cenário do autor com entrada em XML de origem.</summary>
    public static IReadOnlyList<FunctionalScenario> ErpScenarios() =>
    [
        new FunctionalScenario("Nota do ERP", default, Assertions:
        [
            new StructuralAssertion("/DPS/numero", AssertionKind.Equals, "000123"),
            new StructuralAssertion("/DPS/cnpjPrestador", AssertionKind.Equals, "12345678000199"),
            new StructuralAssertion("/DPS/valorServico", AssertionKind.Equals, "1500.50"),
            new StructuralAssertion("/DPS/codigoSituacao", AssertionKind.Equals, "1"),
        ], SourceXml: ErpSourceXml()),
    ];

    // ---- Cenários funcionais obrigatórios (Fase 8, ADR-0011 §4) ----

    /// <summary>Cenário do autor para a DPS: entrada realista + asserções estruturais.</summary>
    public static IReadOnlyList<FunctionalScenario> DpsScenarios() =>
    [
        new FunctionalScenario("DPS realista", DpsJson(), Assertions:
        [
            new StructuralAssertion("/DPS/numero", AssertionKind.Exists),
            new StructuralAssertion("/DPS/cnpjPrestador", AssertionKind.Equals, "12345678000199"),
            new StructuralAssertion("/DPS/@versao", AssertionKind.Exists),
        ]),
    ];

    /// <summary>Cenário do autor para o pedido: o filtro descarta o item de valor zero.</summary>
    public static IReadOnlyList<FunctionalScenario> PedidoScenarios() =>
    [
        new FunctionalScenario("Pedido com itens", PedidoJson(), Assertions:
        [
            new StructuralAssertion("/PedidoComItens/itens/item", AssertionKind.Count, "2"),
        ]),
    ];

    /// <summary>Cenário adequado ao XSD do conteúdo (DPS ou pedido).</summary>
    public static IReadOnlyList<FunctionalScenario> ScenariosFor(SnapshotContent content) =>
        content.SchemaEntryPoint == "exemplo-colecao.xsd" ? PedidoScenarios() : DpsScenarios();

    // ---- Destino com xs:choice repetível (Fase 15, ADR-0018) ----

    /// <summary>Lote no formato dos webservices municipais: Rps com conteúdo xs:choice repetível.</summary>
    public const string LoteRpsXsd = """
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
          <xs:element name="Lote">
            <xs:complexType>
              <xs:sequence>
                <xs:element name="NumeroLote" type="xs:string"/>
                <xs:element name="Rps" maxOccurs="unbounded">
                  <xs:complexType>
                    <xs:choice maxOccurs="99">
                      <xs:element name="Identificacao"><xs:complexType><xs:sequence><xs:element name="Numero" type="xs:string"/><xs:element name="Lote" type="xs:string"/></xs:sequence></xs:complexType></xs:element>
                      <xs:element name="Valores"><xs:complexType><xs:sequence><xs:element name="Valor" type="xs:decimal"/></xs:sequence></xs:complexType></xs:element>
                      <xs:element name="Tomador"><xs:complexType><xs:sequence><xs:element name="Cnpj" type="xs:string"/></xs:sequence></xs:complexType></xs:element>
                    </xs:choice>
                  </xs:complexType>
                </xs:element>
              </xs:sequence>
            </xs:complexType>
          </xs:element>
        </xs:schema>
        """;

    /// <summary>
    /// DE-PARA para o lote: um "for each" sobre <c>infDPS</c> (objeto único = 1 Rps),
    /// ramos do choice mapeados e a raiz da entrada (FieldScope.Root) dentro do item.
    /// </summary>
    public static SnapshotContent LoteRpsChoice() => new()
    {
        SchemaFiles = new Dictionary<string, byte[]> { ["lote-rps.xsd"] = System.Text.Encoding.UTF8.GetBytes(LoteRpsXsd) },
        SchemaEntryPoint = "lote-rps.xsd",
        Mapping = new MappingTemplate
        {
            Fields = [FieldMapping.Direct("lote", "NumeroLote")],
            Collections =
            [
                new CollectionMapping
                {
                    DestinationElementName = "Rps",
                    SourceCollectionPath = "infDPS",
                    SingleObjectAsItem = true, // um documento = um Rps (ADR-0020)
                    ItemContent = new MappingTemplate
                    {
                        Complex =
                        [
                            new ComplexMapping { DestinationElementName = "Identificacao", Content = new MappingTemplate { Fields = [FieldMapping.Direct("numero", "Numero"), new FieldMapping("Lote", new FieldReferenceNode("lote", FieldScope.Root))] } },
                            new ComplexMapping { DestinationElementName = "Valores", Content = new MappingTemplate { Fields = [FieldMapping.Direct("valor", "Valor")] } },
                            new ComplexMapping { DestinationElementName = "Tomador", Content = new MappingTemplate { Fields = [FieldMapping.Direct("tomador.cnpj", "Cnpj")] } },
                        ],
                    },
                },
            ],
        },
    };

    /// <summary>Entrada do lote: um documento só (objeto, não lista).</summary>
    public static JsonElement LoteRpsJson() => Json("""{"lote":"L-77","infDPS":{"numero":"14938","valor":"1500.50","tomador":{"cnpj":"12345678000199"}}}""");

    /// <summary>Cenário funcional do lote com choice.</summary>
    public static IReadOnlyList<FunctionalScenario> LoteRpsScenarios() =>
    [
        new FunctionalScenario("Lote com um RPS", LoteRpsJson(), Assertions:
        [
            new StructuralAssertion("/Lote/NumeroLote", AssertionKind.Equals, "L-77"),
            new StructuralAssertion("/Lote/Rps/Identificacao/Numero", AssertionKind.Equals, "14938"),
            new StructuralAssertion("/Lote/Rps/Identificacao/Lote", AssertionKind.Equals, "L-77"),
            new StructuralAssertion("/Lote/Rps/Valores/Valor", AssertionKind.Equals, "1500.50"),
            new StructuralAssertion("/Lote/Rps/Tomador/Cnpj", AssertionKind.Equals, "12345678000199"),
        ]),
    ];

    public static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>
    /// Comparação semântica de XML (.claude/rules/testing.md): ignora
    /// declarações de namespace/prefixo e whitespace de formatação.
    /// </summary>
    public static bool XmlSemanticallyEqual(string? expected, string? actual)
    {
        if (expected is null || actual is null)
        {
            return expected == actual;
        }

        return XNode.DeepEquals(Normalize(expected), Normalize(actual));
    }

    private static XElement Normalize(string xml)
    {
        var root = XDocument.Parse(xml, LoadOptions.None).Root!;
        foreach (var element in root.DescendantsAndSelf())
        {
            element.Attributes().Where(a => a.IsNamespaceDeclaration).Remove();
        }

        return root;
    }
}

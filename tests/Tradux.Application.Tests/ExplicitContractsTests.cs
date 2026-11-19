using System.Text;
using System.Text.Json;
using Tradux.Application.Quality;
using Tradux.Application.Snapshots;
using Tradux.Contracts.Quality;
using Tradux.Domain.Mapping;
using Tradux.Tests.Shared;

namespace Tradux.Application.Tests;

/// <summary>
/// Fase 16 (ADR-0020): contratos explícitos no lugar de convenções — escopo raiz
/// tipado, objeto único só por opção e um caso por ramo de choice.
/// </summary>
public class ExplicitContractsTests
{
    private const string ChoiceXsd = """
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
          <xs:element name="R">
            <xs:complexType>
              <xs:sequence>
                <xs:element name="a" type="xs:string"/>
                <xs:element name="tomador">
                  <xs:complexType>
                    <xs:choice>
                      <xs:element name="cpf" type="xs:string"/>
                      <xs:element name="cadastro"><xs:complexType><xs:sequence><xs:element name="im" type="xs:string"/><xs:element name="uf" type="xs:string"/></xs:sequence></xs:complexType></xs:element>
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

    // Choice único (tomador) com DOIS ramos mapeados: antes da Fase 16 a suíte preenchia
    // os dois, o motor recusava (CHOICE_MULTIPLE_BRANCHES) e a publicação ficava bloqueada.
    private static SnapshotContent ChoiceContent() => new()
    {
        SchemaFiles = new Dictionary<string, byte[]> { ["c.xsd"] = Encoding.UTF8.GetBytes(ChoiceXsd) },
        SchemaEntryPoint = "c.xsd",
        Mapping = new MappingTemplate
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
                        Complex = [new ComplexMapping { DestinationElementName = "cadastro", Content = new MappingTemplate { Fields = [FieldMapping.Direct("Im", "im"), FieldMapping.Direct("Uf", "uf")] } }],
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
        },
    };

    private static QualityReport RunSuite(SnapshotContent content)
    {
        var template = new SnapshotCompiler().CompileContent(content).Template;
        return StructuralTestRunner.Run(template, content.Mapping, content.LookupTables);
    }

    private static string Describe(QualityReport report) =>
        string.Join("\n", report.Cases.Where(c => !c.Passed).Select(c => $"{c.Name} [{c.Target}] {c.FailureCode} @ {c.DestinationXPath}"));

    [Fact]
    public void Structural_suite_runs_one_case_per_choice_branch_and_passes_with_two_branches_of_a_single_choice()
    {
        var report = RunSuite(ChoiceContent());

        Assert.True(report.Cases.All(c => c.Passed), Describe(report));
        var branches = report.Cases.Where(c => c.Category == StructuralCategory.Choice).Select(c => c.Target![(c.Target!.LastIndexOf('/') + 1)..]).ToList();
        Assert.Equal(["cpf", "cadastro", "x", "y"], branches);
    }

    [Fact]
    public void Removing_the_only_field_of_a_branch_is_still_an_expected_invalid_case()
    {
        // Só o ramo do campo fica no caso: sem "Cpf" o choice obrigatório fica vazio e o
        // template precisa reprovar — nenhum outro ramo "absorve" a falta.
        var report = RunSuite(ChoiceContent());

        var missingCpf = Assert.Single(report.Cases, c => c.Name == "invalido:obrigatorio-ausente:Cpf");
        Assert.False(missingCpf.ExpectedValid);
        Assert.True(missingCpf.Passed, Describe(report));
        var missingIm = Assert.Single(report.Cases, c => c.Name == "invalido:obrigatorio-ausente:Im");
        Assert.True(missingIm.Passed, Describe(report)); // ramo "cadastro" fica parcial: reprovado
    }

    [Fact]
    public void Field_scope_is_typed_and_the_default_keeps_the_serialized_content_and_hash()
    {
        var current = JsonSerializer.Serialize<RuleNode>(new FieldReferenceNode("lote"));
        var root = JsonSerializer.Serialize<RuleNode>(new FieldReferenceNode("lote", FieldScope.Root));

        Assert.Equal("""{"$type":"field","Path":"lote"}""", current); // igual ao de antes: hash de templates publicados não muda
        Assert.Contains("\"root\"", root, StringComparison.Ordinal);
        Assert.Equal(new FieldReferenceNode("lote", FieldScope.Root), JsonSerializer.Deserialize<RuleNode>(root));
    }

    [Fact]
    public void Single_object_as_item_is_an_explicit_option_kept_out_of_the_serialized_default()
    {
        var collection = new CollectionMapping { DestinationElementName = "Rps", SourceCollectionPath = "infDPS", ItemContent = new MappingTemplate() };

        Assert.DoesNotContain("SingleObjectAsItem", JsonSerializer.Serialize(collection), StringComparison.Ordinal);
        Assert.Contains("\"SingleObjectAsItem\":true", JsonSerializer.Serialize(new CollectionMapping
        {
            DestinationElementName = "Rps", SourceCollectionPath = "infDPS", ItemContent = new MappingTemplate(), SingleObjectAsItem = true,
        }), StringComparison.Ordinal);
    }

    // ---- Revisão da Fase 16 ----

    private const string TwoBranchXsd = """
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
          <xs:element name="R">
            <xs:complexType>
              <xs:sequence>
                <xs:element name="a" type="xs:string"/>
                <xs:element name="doc"><xs:complexType><xs:choice>
                  <xs:element name="cpf" type="xs:string"/>
                  <xs:element name="cnpj" type="xs:string"/>
                </xs:choice></xs:complexType></xs:element>
                <xs:element name="lista"><xs:complexType><xs:choice maxOccurs="5">
                  <xs:element name="x" type="xs:string"/>
                  <xs:element name="y"><xs:complexType><xs:sequence><xs:element name="v" type="xs:string"/><xs:element name="w" type="xs:string"/></xs:sequence></xs:complexType></xs:element>
                </xs:choice></xs:complexType></xs:element>
              </xs:sequence>
            </xs:complexType>
          </xs:element>
        </xs:schema>
        """;

    private static SnapshotContent TwoBranch(MappingTemplate doc, MappingTemplate lista) => new()
    {
        SchemaFiles = new Dictionary<string, byte[]> { ["t.xsd"] = Encoding.UTF8.GetBytes(TwoBranchXsd) },
        SchemaEntryPoint = "t.xsd",
        Mapping = new MappingTemplate
        {
            Fields = [FieldMapping.Direct("Doc", "a")],
            Complex =
            [
                new ComplexMapping { DestinationElementName = "doc", Content = doc },
                new ComplexMapping { DestinationElementName = "lista", Content = lista },
            ],
        },
    };

    private static readonly MappingTemplate ListaX = new() { Fields = [FieldMapping.Direct("X", "x")] };

    [Fact]
    public void Branch_forced_by_a_field_used_outside_the_choice_is_the_default_and_the_other_is_reported_unreachable()
    {
        // "Doc" alimenta /R/a e o ramo cnpj: cnpj aparece sempre. O ramo padrão é cnpj (não cpf),
        // e cpf — que nunca sai sozinho — reprova com motivo próprio, sem derrubar os casos base.
        var report = RunSuite(TwoBranch(new MappingTemplate { Fields = [FieldMapping.Direct("Doc", "cnpj"), FieldMapping.Direct("Cpf", "cpf")] }, ListaX));

        var failed = Assert.Single(report.Cases, c => !c.Passed);
        Assert.Equal(StructuralTestRunner.ChoiceBranchUnreachableCode, failed.FailureCode);
        Assert.EndsWith("/cpf", failed.Target, StringComparison.Ordinal);
        Assert.Contains(report.Cases, c => c.Name == "completo" && c.Passed);
    }

    [Fact]
    public void Branch_with_content_without_data_is_always_part_of_the_case()
    {
        // y.w é constante: o ramo y aparece em todo caso. "Só o ramo x" leva y junto, completo
        // (com v), como o motor faria — nunca um y parcial que reprova sem motivo.
        var lista = new MappingTemplate
        {
            Fields = [FieldMapping.Direct("X", "x")],
            Complex = [new ComplexMapping { DestinationElementName = "y", Content = new MappingTemplate { Fields = [FieldMapping.Direct("V", "v"), new FieldMapping("w", new LiteralNode("k"))] } }],
        };

        var report = RunSuite(TwoBranch(new MappingTemplate { Fields = [FieldMapping.Direct("Cpf", "cpf")] }, lista));

        Assert.True(report.Cases.All(c => c.Passed), Describe(report));
        Assert.Contains(report.Cases, c => c.Name.EndsWith("/x", StringComparison.Ordinal) && c.Category == StructuralCategory.Choice);
    }

    [Fact]
    public void Legacy_root_prefix_is_a_compilation_error_not_a_silently_missing_field()
    {
        var content = TwoBranch(new MappingTemplate { Fields = [FieldMapping.Direct("$root.Cpf", "cpf")] }, ListaX);

        var error = Assert.Throws<SnapshotCompilationException>(() => new SnapshotCompiler().CompileContent(content));
        Assert.Contains("scope", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Root_scope_works_in_nested_for_each_and_in_filters()
    {
        const string xsd = """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xs:element name="R"><xs:complexType><xs:sequence>
                <xs:element name="g" maxOccurs="unbounded"><xs:complexType><xs:sequence>
                  <xs:element name="i" minOccurs="0" maxOccurs="unbounded"><xs:complexType><xs:sequence><xs:element name="v" type="xs:string"/></xs:sequence></xs:complexType></xs:element>
                </xs:sequence></xs:complexType></xs:element>
              </xs:sequence></xs:complexType></xs:element>
            </xs:schema>
            """;
        var mapping = new MappingTemplate
        {
            Collections =
            [
                new CollectionMapping
                {
                    DestinationElementName = "g",
                    SourceCollectionPath = "grupos",
                    ItemContent = new MappingTemplate
                    {
                        Collections =
                        [
                            new CollectionMapping
                            {
                                DestinationElementName = "i",
                                SourceCollectionPath = "itens",
                                // Filtro com campo da raiz, dois níveis de "for each" abaixo.
                                Filter = OperatorNode.Of("equals", new FieldReferenceNode("tipo"), new FieldReferenceNode("tipoAlvo", FieldScope.Root)),
                                ItemContent = new MappingTemplate { Fields = [new FieldMapping("v", OperatorNode.Of("concat", new FieldReferenceNode("nome"), new FieldReferenceNode("sufixo", FieldScope.Root)))] },
                            },
                        ],
                    },
                },
            ],
        };
        var content = new SnapshotContent
        {
            SchemaFiles = new Dictionary<string, byte[]> { ["n.xsd"] = Encoding.UTF8.GetBytes(xsd) },
            SchemaEntryPoint = "n.xsd",
            Mapping = mapping,
        };
        var template = new SnapshotCompiler().CompileContent(content).Template;

        var outcome = Engine.XmlGenerationEngine.Generate(template, Domain.SourceData.SourceDataTree.FromJson(SnapshotFixtures.Json(
            """{"tipoAlvo":"A","sufixo":"!","grupos":[{"itens":[{"tipo":"A","nome":"um"},{"tipo":"B","nome":"dois"}]}]}""")));

        Assert.True(outcome.Success, string.Join("; ", outcome.Diagnostics.Select(d => d.Code)));
        Assert.Equal(["um!"], System.Xml.Linq.XDocument.Parse(outcome.Xml!).Descendants("v").Select(e => e.Value));
    }

    // ---- Revisão da Fase 16, rodada 2 ----

    private const string DocXsd = """
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
          <xs:element name="R"><xs:complexType><xs:sequence>
            <xs:element name="doc"><xs:complexType><xs:choice>
              <xs:element name="cpf" type="xs:string"/>
              <xs:element name="nif" type="xs:string"/>
              <xs:element name="it" maxOccurs="unbounded"><xs:complexType><xs:sequence><xs:element name="tipo" type="xs:string"/><xs:element name="v" type="xs:string"/></xs:sequence></xs:complexType></xs:element>
            </xs:choice></xs:complexType></xs:element>
          </xs:sequence></xs:complexType></xs:element>
        </xs:schema>
        """;

    private static SnapshotContent Doc(MappingTemplate doc) => new()
    {
        SchemaFiles = new Dictionary<string, byte[]> { ["d.xsd"] = Encoding.UTF8.GetBytes(DocXsd) },
        SchemaEntryPoint = "d.xsd",
        Mapping = new MappingTemplate { Complex = [new ComplexMapping { DestinationElementName = "doc", Content = doc }] },
    };

    [Fact]
    public void Conditional_constant_branch_is_a_data_dependent_alternative_not_a_forced_branch()
    {
        // Padrão da DPS: cNaoNIF/"NAO" só quando falta o CPF. Sem dado a condição vale, mas o
        // ramo depende do dado — não é "sempre presente", e o CPF continua alcançável.
        var report = RunSuite(Doc(new MappingTemplate
        {
            Fields =
            [
                FieldMapping.Direct("Cpf", "cpf"),
                new FieldMapping("nif", new LiteralNode("NAO"), OperatorNode.Of("isEmpty", new FieldReferenceNode("Cpf"))),
            ],
        }));

        Assert.True(report.Cases.All(c => c.Passed), Describe(report));
        Assert.DoesNotContain(report.Cases, c => c.FailureCode == StructuralTestRunner.ChoiceBranchUnreachableCode);
        Assert.Contains(report.NotCovered, g => g.Area == "choice" && g.Path.EndsWith("/nif", StringComparison.Ordinal));
    }

    [Fact]
    public void Constant_inside_a_collection_item_does_not_force_the_branch_of_the_collection()
    {
        // tipo = "K" em cada item: só aparece se houver item. Com lista vazia o ramo "it" não sai.
        var report = RunSuite(Doc(new MappingTemplate
        {
            Fields = [FieldMapping.Direct("Cpf", "cpf")],
            Collections =
            [
                new CollectionMapping
                {
                    DestinationElementName = "it",
                    SourceCollectionPath = "itens",
                    ItemContent = new MappingTemplate { Fields = [new FieldMapping("tipo", new LiteralNode("K")), FieldMapping.Direct("V", "v")] },
                },
            ],
        }));

        Assert.True(report.Cases.All(c => c.Passed), Describe(report));
        Assert.Contains(report.Cases, c => c.Name.EndsWith("/cpf", StringComparison.Ordinal) && c.Category == StructuralCategory.Choice);
    }
}

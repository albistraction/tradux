using System.Xml.Linq;
using Tradux.Domain.Mapping;
using Tradux.Domain.SourceData;
using Tradux.Engine.Compilation;
using Tradux.Xsd;
using Xunit;

namespace Tradux.Engine.Tests;

/// <summary>
/// Prova o bloco de iteração (seção 14 do requisito de origem) de ponta a
/// ponta contra docs/schemas/exemplo-colecao.xsd — gera zero, um e vários
/// nós repetíveis, aplica filtro, e valida cardinalidade.
/// </summary>
public class CollectionGenerationTests
{
    private static string FixturePath => RepositoryPaths.Combine("docs", "schemas", "exemplo-colecao.xsd");

    private static readonly XNamespace Ns = "http://exemplo.ilustrativo.tradux/pedido-colecao/v1";

    private static MappingTemplate BuildTemplate(RuleNode? filter = null) => new()
    {
        Fields =
        [
            FieldMapping.Direct("Numero", "numero"),
            FieldMapping.Direct("ValorTotal", "valorTotal"),
        ],
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
                            Filter = filter,
                            ItemContent = new MappingTemplate
                            {
                                Fields =
                                [
                                    FieldMapping.Direct("Descricao", "descricao"),
                                    FieldMapping.Direct("ValorUnitario", "valorUnitario"),
                                ],
                            },
                        },
                    ],
                },
            },
        ],
    };

    private static PedidoSourceData Pedido(params (string Descricao, decimal Valor)[] itens) => new()
    {
        Numero = "PED-001",
        ValorTotal = itens.Sum(i => i.Valor),
        Itens = itens.Select(i => new ItemPedidoSourceData { Descricao = i.Descricao, ValorUnitario = i.Valor }).ToList(),
    };

    private static CompiledTemplate Compile(RuleNode? filter = null)
    {
        var importedXsd = XsdFiles.Import(FixturePath);
        return TemplateCompiler.Compile(BuildTemplate(filter), importedXsd);
    }

    [Fact]
    public void Three_items_generate_three_repeated_elements_in_order()
    {
        var compiled = Compile();
        var outcome = XmlGenerationEngine.Generate(
            compiled, EngineHarness.Source(Pedido(("Consultoria", 100m), ("Suporte", 50m), ("Treinamento", 75m))));

        Assert.True(outcome.Success, string.Join("; ", outcome.Diagnostics.Select(d => d.Message)));
        var doc = XDocument.Parse(outcome.Xml!);
        var items = doc.Root!.Element(Ns + "itens")!.Elements(Ns + "item").ToList();

        Assert.Equal(3, items.Count);
        Assert.Equal(["Consultoria", "Suporte", "Treinamento"], items.Select(i => i.Element(Ns + "descricao")!.Value));
        Assert.Equal("100.00", items[0].Element(Ns + "valorUnitario")!.Value);
    }

    [Fact]
    public void Zero_items_generate_an_empty_but_valid_envelope()
    {
        var compiled = Compile();
        var outcome = XmlGenerationEngine.Generate(compiled, EngineHarness.Source(Pedido()));

        Assert.True(outcome.Success, string.Join("; ", outcome.Diagnostics.Select(d => d.Message)));
        var doc = XDocument.Parse(outcome.Xml!);
        var itensElement = doc.Root!.Element(Ns + "itens")!;

        Assert.Empty(itensElement.Elements(Ns + "item"));
    }

    [Fact]
    public void Filter_excludes_items_that_do_not_satisfy_the_condition()
    {
        var filter = OperatorNode.Of("greaterThan", new FieldReferenceNode("ValorUnitario"), new LiteralNode(0m));
        var compiled = Compile(filter);
        var pedido = Pedido(("Consultoria", 100m), ("Cortesia", 0m), ("Suporte", 50m));

        var outcome = XmlGenerationEngine.Generate(compiled, EngineHarness.Source(pedido));

        Assert.True(outcome.Success, string.Join("; ", outcome.Diagnostics.Select(d => d.Message)));
        var doc = XDocument.Parse(outcome.Xml!);
        var descricoes = doc.Root!.Element(Ns + "itens")!.Elements(Ns + "item").Select(i => i.Element(Ns + "descricao")!.Value);

        Assert.Equal(["Consultoria", "Suporte"], descricoes);
    }

    [Fact]
    public void Item_index_is_available_inside_the_local_context_as_dollar_index()
    {
        // "contexto local do item" (seção 14): $index permite, por exemplo,
        // numerar a descrição de cada item a partir dos dados originais.
        var template = new MappingTemplate
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
        };

        var importedXsd = XsdFiles.Import(FixturePath);
        var compiled = TemplateCompiler.Compile(template, importedXsd);
        var outcome = XmlGenerationEngine.Generate(compiled, EngineHarness.Source(Pedido(("Consultoria", 100m), ("Suporte", 50m))));

        Assert.True(outcome.Success, string.Join("; ", outcome.Diagnostics.Select(d => d.Message)));
        var doc = XDocument.Parse(outcome.Xml!);
        var descricoes = doc.Root!.Element(Ns + "itens")!.Elements(Ns + "item").Select(i => i.Element(Ns + "descricao")!.Value).ToList();

        Assert.Equal("0: Consultoria", descricoes[0]);
        Assert.Equal("1: Suporte", descricoes[1]);
    }

    [Fact]
    public void Unmapped_optional_collection_compiles_clean_and_generates_an_empty_envelope()
    {
        // "item" tem minOccurs=0 — não mapeá-lo não é erro (ao contrário de
        // um elemento/coleção obrigatório sem mapeamento), só produz uma
        // coleção sempre vazia.
        var templateWithoutCollection = new MappingTemplate
        {
            Fields = [FieldMapping.Direct("Numero", "numero"), FieldMapping.Direct("ValorTotal", "valorTotal")],
            Complex =
            [
                new ComplexMapping { DestinationElementName = "itens", Content = new MappingTemplate() },
            ],
        };

        var importedXsd = XsdFiles.Import(FixturePath);
        var compiled = TemplateCompiler.Compile(templateWithoutCollection, importedXsd);
        Assert.Empty(compiled.CompileTimeDiagnostics);

        var outcome = XmlGenerationEngine.Generate(compiled, EngineHarness.Source(Pedido(("Consultoria", 100m))));

        Assert.True(outcome.Success, string.Join("; ", outcome.Diagnostics.Select(d => d.Message)));
        var doc = XDocument.Parse(outcome.Xml!);
        Assert.Empty(doc.Root!.Element(Ns + "itens")!.Elements(Ns + "item"));
    }

    [Fact]
    public void Collection_source_path_that_is_not_enumerable_is_a_critical_diagnostic_not_a_crash()
    {
        var badTemplate = BuildTemplate();
        var importedXsd = XsdFiles.Import(FixturePath);
        var compiled = TemplateCompiler.Compile(badTemplate, importedXsd);

        // "Numero" é uma string, não uma coleção — usar seu caminho como
        // origem de uma coleção precisa falhar com diagnóstico, não lançar.
        var brokenSource = new { Numero = "PED-001", Itens = "não é uma coleção", ValorTotal = 10m };
        var outcome = XmlGenerationEngine.Generate(compiled, EngineHarness.Source(brokenSource));

        Assert.False(outcome.Success);
        Assert.Contains(outcome.Diagnostics, d => d.Code == "COLLECTION_SOURCE_NOT_ENUMERABLE");
    }
}

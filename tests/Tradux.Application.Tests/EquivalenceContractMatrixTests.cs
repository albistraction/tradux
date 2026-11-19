using System.Text.Json;
using Tradux.Application.Generation;
using Tradux.Application.Snapshots;
using Tradux.Contracts;
using Tradux.Domain.Mapping;
using Tradux.Domain.SourceData;
using Tradux.Engine;
using Tradux.Tests.Shared;

namespace Tradux.Application.Tests;

/// <summary>
/// Matriz de tipos de entrada: cada forma de valor que um JSON de origem traz
/// × expressões que produzem texto/número/data/condição × cada tipo escalar de
/// destino suportado. Nada lança exceção — erro sempre vira diagnóstico.
/// </summary>
public class EquivalenceContractMatrixTests
{
    private static readonly object Source = new
    {
        // Campos obrigatórios da DPS, válidos (o caso em teste sobrescreve um elemento).
        Numero = "000123",
        Serie = "A1",
        DataEmissao = new DateOnly(2026, 9, 28),
        Competencia = new DateOnly(2026, 9, 1),
        ValorServico = 1500.50m,
        ValorDesconto = (decimal?)null,
        CodigoServico = "01.02.03",
        CnpjPrestador = "12345678000199",
        CnpjTomador = "98765432000188",
        CodigoSituacao = "1",

        // Tipos em teste.
        Texto = "abc 123",
        TextoData = "2026-09-28",
        Decimal = 10.50m,
        Inteiro = 7,
        Longo = 9_000_000_000L,
        Booleano = true,
        Data = new DateOnly(2026, 9, 28),
        DataHoraUtc = new DateTime(2026, 9, 28, 10, 0, 0, DateTimeKind.Utc),
        DataHoraLocal = new DateTime(2026, 9, 28, 10, 0, 0, DateTimeKind.Local),
        DataHoraSemFuso = new DateTime(2026, 9, 28, 10, 0, 0),
        DataHoraFuso = new DateTimeOffset(2026, 9, 28, 10, 0, 0, TimeSpan.FromHours(-3)),
        Situacao = DayOfWeek.Monday,
        Lista = new List<string> { "a", "b" },
        Objeto = new { A = 1, B = "x" },
    };

    private static readonly string[] SourceFields =
    [
        "Texto", "TextoData", "Decimal", "Inteiro", "Longo", "Booleano", "Data", "DataHoraUtc", "DataHoraLocal",
        "DataHoraSemFuso", "DataHoraFuso", "Situacao", "Lista", "Objeto", "Ausente",
    ];

    // Elementos da DPS de cada tipo escalar suportado.
    private static readonly string[] Destinations = ["numero", "valorServico", "dataEmissao", "competencia"];

    private static readonly Func<string, RuleNode>[] Expressions =
    [
        f => new FieldReferenceNode(f),
        f => OperatorNode.Of("concat", new LiteralNode("x"), new FieldReferenceNode(f)),
        f => OperatorNode.Of("formatDate", new FieldReferenceNode(f), new LiteralNode("yyyy-MM-dd")),
        f => OperatorNode.Of("formatNumber", new FieldReferenceNode(f), new LiteralNode("0.00")),
        f => OperatorNode.Of("substring", new FieldReferenceNode(f), new LiteralNode(0m), new LiteralNode(2m)),
        f => OperatorNode.Of("trim", new FieldReferenceNode(f)),
        f => OperatorNode.Of("coalesce", new FieldReferenceNode(f), new LiteralNode("padrão")),
        f => OperatorNode.Of("lookup", new FieldReferenceNode(f), new LiteralNode("situacoes")),
        f => OperatorNode.Of("greaterThan", new FieldReferenceNode(f), new LiteralNode("2026-01-01")),
        f => OperatorNode.Of("equals", new FieldReferenceNode(f), new FieldReferenceNode("DataHoraUtc")),
        f => OperatorNode.Of("lessThan", new FieldReferenceNode(f), new LiteralNode(5m)),
    ];

    private static SourceDataTree Input => SourceDataTree.FromJson(JsonSerializer.SerializeToElement(Source, Source.GetType()));

    [Fact]
    public void Every_type_expression_and_destination_ends_in_a_result_never_an_exception()
    {
        var input = Input;
        var compiler = new SnapshotCompiler();
        var cases = 0;

        foreach (var destination in Destinations)
        {
            foreach (var expression in Expressions)
            {
                foreach (var field in SourceFields)
                {
                    var content = SnapshotJson.Rehydrate(WithMapping(destination, expression(field)));
                    var (_, template) = compiler.CompileContent(content);

                    var outcome = XmlGenerationEngine.Generate(template, input);
                    Assert.True(outcome.Success || outcome.Diagnostics.Count > 0, $"{destination} ← {expression(field)}");
                    cases++;
                }
            }
        }

        Assert.Equal(Destinations.Length * Expressions.Length * SourceFields.Length, cases);
    }

    [Theory]
    [InlineData("Objeto", "numero")]
    [InlineData("Lista", "numero")]
    [InlineData("Texto", "valorServico")]
    [InlineData("Inteiro", "dataEmissao")]
    public void Value_that_does_not_fit_the_element_type_is_a_critical_diagnostic(string field, string destination)
    {
        var content = SnapshotJson.Rehydrate(WithMapping(destination, new FieldReferenceNode(field)));
        var (_, template) = new SnapshotCompiler().CompileContent(content);

        var result = GenerationResultMapper.ToContract(XmlGenerationEngine.Generate(template, Input));

        Assert.False(result.Success);
        var diagnostic = Assert.Single(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Critical);
        Assert.Equal("VALUE_CONVERSION_FAILED", diagnostic.Code);
        Assert.Equal($"/DPS/{destination}", diagnostic.DestinationXPath);
        Assert.DoesNotContain("System.", diagnostic.Message);
    }

    [Fact]
    public void Dates_copied_to_a_string_element_use_the_iso_text_not_a_culture_format()
    {
        var content = SnapshotJson.Rehydrate(WithMapping("numero", new FieldReferenceNode("Data")));
        var (_, template) = new SnapshotCompiler().CompileContent(content);

        var result = XmlGenerationEngine.Generate(template, Input);

        Assert.Contains("<numero>2026-09-28</numero>", result.Xml);
    }

    [Fact]
    public void Scalar_collection_items_expose_their_value_as_dollar_value()
    {
        var tree = SourceDataTree.FromItem("2026-09-28", new Dictionary<string, object?> { ["$index"] = 0m }, Input);

        Assert.True(tree.TryGetValue("$value", out var value));
        Assert.Equal("2026-09-28", value);
        Assert.False(tree.TryGetValue("Length", out _));
    }

    private static SnapshotContent WithMapping(string destination, RuleNode value)
    {
        var content = SnapshotFixtures.SimpleDps();
        return content with
        {
            Mapping = new MappingTemplate
            {
                Fields = content.Mapping.Fields
                    .Select(f => f.DestinationElementName == destination ? new FieldMapping(destination, value) : f)
                    .ToList(),
            },
        };
    }
}

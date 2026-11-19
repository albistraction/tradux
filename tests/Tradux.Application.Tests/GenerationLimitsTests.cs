using System.Text.Json;
using Tradux.Application.Generation;
using Tradux.Contracts;
using Tradux.Domain.SourceData;
using Tradux.Tests.Shared;

namespace Tradux.Application.Tests;

/// <summary>Fase 11 (ADR-0014 §3): limites de entrada no ponto único de geração.</summary>
public class GenerationLimitsTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static JsonElement Nested(int depth)
    {
        var json = string.Concat(Enumerable.Repeat("{\"a\":", depth - 1)) + "1" + new string('}', depth - 1);
        return Json(json);
    }

    [Fact]
    public void Measure_reports_depth_nodes_and_longest_text_and_stops_at_the_budget()
    {
        var shape = SourceDataTree.FromJson(Json("""{ "a": { "b": ["xyz", "wxyz"] }, "c": 1 }""")).Measure(1000);

        // raiz(1) → a(2) → b(3) → itens(4)
        Assert.Equal(new SourceDataShape(Depth: 4, Nodes: 6, LongestText: 4, BudgetExceeded: false), shape);
        Assert.True(SourceDataTree.FromJson(Json("""{ "itens": [1,2,3,4,5,6,7,8,9,10] }""")).Measure(5).BudgetExceeded);
    }

    [Theory]
    [InlineData(GenerationLimits.DepthLimit)]
    [InlineData(GenerationLimits.NodesLimit)]
    [InlineData(GenerationLimits.TextLimit)]
    public void Each_limit_is_enforced_and_the_message_never_echoes_the_data(string limit)
    {
        var limits = new GenerationLimits { MaxDepth = 5, MaxNodes = 50, MaxTextLength = 20 };
        var secret = "CPF-12345678909-" + new string('x', 30);
        var data = limit switch
        {
            GenerationLimits.DepthLimit => Nested(6),
            GenerationLimits.NodesLimit => Json("{\"itens\":[" + string.Join(",", Enumerable.Range(0, 60)) + "]}"),
            _ => Json($$"""{ "Descricao": "{{secret}}" }"""),
        };

        var error = Assert.Throws<GenerationInputLimitException>(() => limits.Enforce(SourceDataTree.FromJson(data)));

        Assert.Equal(limit, error.Limit);
        Assert.DoesNotContain("CPF", error.Message);
        limits.Enforce(SourceDataTree.FromJson(Nested(5)));
    }

    [Theory]
    [InlineData(5, 3)]
    [InlineData(6, 3)]
    [InlineData(4, 12)]
    [InlineData(4, 13)]
    public void Json_prewalk_and_tree_measure_agree(int depth, int items)
    {
        // Mesma contagem nas duas formas: a verificação no JSON (antes de montar a árvore)
        // não pode aceitar o que a árvore recusaria, nem o contrário.
        var limits = new GenerationLimits { MaxDepth = 5, MaxNodes = 15, MaxTextLength = 100 };
        var json = Json("{\"lista\":[" + string.Join(",", Enumerable.Range(0, items)) + "],\"n\":" + (depth > 2 ? Nested(depth - 1).GetRawText() : "1") + "}");

        var byJson = Record.Exception(() => limits.Enforce(json)) as GenerationInputLimitException;
        var byTree = Record.Exception(() => limits.Enforce(SourceDataTree.FromJson(json))) as GenerationInputLimitException;

        Assert.Equal(byTree is null, byJson is null);
    }

    [Fact]
    public async Task Publication_gates_read_scenarios_with_the_configured_limits()
    {
        var services = new TestServices(limits: new GenerationLimits { MaxTextLength = 5 });

        var result = await services.Publication.PublishAsync(
            new TemplateKey(SnapshotFixtures.Organization, "dps", SnapshotFixtures.Operation), SnapshotFixtures.SimpleDps(), SnapshotFixtures.DpsScenarios(), "teste");

        Assert.False(result.Success);
        var failure = Assert.Single(result.Diagnostics, d => d.Code == Publishing.FunctionalScenarioGate.ScenarioFailedCode);
        Assert.Contains("limite 'text'", failure.Message);
    }

    [Fact]
    public void Draft_test_applies_the_same_limits()
    {
        var designer = new Design.DraftDesignService(limits: new GenerationLimits { MaxDepth = 5 });
        var request = new Contracts.Design.DraftTestRequest(
            SnapshotFixtures.UploadFile("exemplo-simples.xsd"),
            SnapshotFixtures.SimpleDps().Mapping,
            Nested(6));

        Assert.Throws<GenerationInputLimitException>(() => designer.Test(request));
    }

    [Fact]
    public void Defaults_accept_a_realistic_note_and_invalid_configuration_is_refused()
    {
        GenerationLimits.Default.Enforce(SourceDataTree.FromJson(SnapshotFixtures.DpsJson()));
        GenerationLimits.Default.Enforce(SourceDataTree.FromJson(SnapshotFixtures.PedidoJson()));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GenerationLimits { MaxNodes = 0 }.Validated());
    }

    [Fact]
    public async Task Oversized_input_is_refused_before_touching_the_repository()
    {
        var services = new TestServices();
        var repository = services.Repository;
        await services.PublishAsync(new TemplateKey("org-a", "dps", "emissao"), SnapshotFixtures.SimpleDps(), SnapshotFixtures.DpsScenarios());
        var readsBefore = repository.ContentReads + repository.PointerReads;

        var deep = new GenerateXmlRequest("org-a", "dps", "emissao", Nested(40));
        var error = await Assert.ThrowsAsync<GenerationInputLimitException>(() => services.Generation.GenerateAsync(deep));
        var fallback = new GenerateXmlRequest("org-a", "dps", "emissao", Nested(40)) { CandidateVersions = [1] };
        await Assert.ThrowsAsync<GenerationInputLimitException>(() => services.Generation.GenerateAsync(fallback));

        Assert.Equal(GenerationLimits.DepthLimit, error.Limit);
        Assert.Equal(readsBefore, repository.ContentReads + repository.PointerReads);
        Assert.True((await services.Generation.GenerateAsync(new GenerateXmlRequest("org-a", "dps", "emissao", SnapshotFixtures.DpsJson()))).Success);
    }
}

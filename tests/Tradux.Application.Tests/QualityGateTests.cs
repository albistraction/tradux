using System.Xml.Linq;
using Tradux.Application.Layers;
using Tradux.Application.Publishing;
using Tradux.Application.Snapshots;
using Tradux.Application.Testing;
using Tradux.Contracts;
using Tradux.Contracts.Quality;
using Tradux.Domain.Layers;
using Tradux.Domain.Mapping;
using Tradux.Tests.Shared;

namespace Tradux.Application.Tests;

/// <summary>
/// Gate da Fase 8 (seção 19, ADR-0011): "não é possível publicar com teste
/// obrigatório falhando" — na publicação de conteúdo e no vínculo por camadas.
/// </summary>
public class QualityGateTests
{
    private static readonly TemplateKey Key = new(SnapshotFixtures.Organization, "dps", SnapshotFixtures.Operation);

    private static SnapshotContent SerieOverflow()
    {
        // serie tem maxLength 5: o prefixo estoura com qualquer série válida.
        var content = SnapshotFixtures.SimpleDps();
        return content with
        {
            Mapping = new MappingTemplate
            {
                Fields = [.. content.Mapping.Fields.Where(f => f.DestinationElementName != "serie"),
                    new FieldMapping("serie", OperatorNode.Of("concat", new LiteralNode("SERIE-"), new FieldReferenceNode("Serie")))],
            },
        };
    }

    // ------------------------------------------------------------------ publicação de conteúdo

    [Fact]
    public async Task Content_publication_without_an_asserted_functional_scenario_is_refused()
    {
        var services = new TestServices();

        var onlyGenerates = new FunctionalScenario("só gera", SnapshotFixtures.DpsJson());
        var rejected = await services.Publication.PublishAsync(Key, SnapshotFixtures.SimpleDps(), [onlyGenerates], "autor");

        Assert.False(rejected.Success);
        Assert.Contains(rejected.Diagnostics, d => d.Code == FunctionalScenarioGate.ScenarioRequiredCode);
        Assert.Null(await services.Repository.GetActiveVersionAsync(Key));
        Assert.Contains(services.Audit.Events, e => e.Action == "template.publish" && e.Outcome == "rejected");
    }

    [Fact]
    public async Task Content_publication_with_a_failing_structural_test_is_refused_and_the_active_version_is_kept()
    {
        var services = new TestServices();
        await services.PublishAsync(Key, SnapshotFixtures.SimpleDps(), SnapshotFixtures.DpsScenarios(), "autor");

        // O cenário do autor (série "A1") ainda passaria: só o teste estrutural pega o defeito.
        var rejected = await services.Publication.PublishAsync(Key, SerieOverflow(), SnapshotFixtures.DpsScenarios(), "autor");

        Assert.False(rejected.Success);
        Assert.Contains(rejected.Diagnostics, d => d.Code == StructuralTestGate.StructuralTestFailedCode && d.DestinationXPath!.Contains("serie"));
        Assert.NotNull(rejected.Report);
        Assert.False(rejected.Report!.Passed);
        Assert.Equal(1, await services.Repository.GetActiveVersionAsync(Key));
    }

    [Fact]
    public async Task Content_publication_with_a_failing_assertion_is_refused()
    {
        var services = new TestServices();
        var wrong = new FunctionalScenario("CNPJ esperado errado", SnapshotFixtures.DpsJson(),
            Assertions: [new StructuralAssertion("/DPS/cnpjPrestador", AssertionKind.Equals, "00000000000000")]);

        var rejected = await services.Publication.PublishAsync(Key, SnapshotFixtures.SimpleDps(), [wrong], "autor");

        Assert.False(rejected.Success);
        var failure = Assert.Single(rejected.Diagnostics, d => d.Code == FunctionalScenarioGate.ScenarioFailedCode);
        Assert.Equal("/DPS/cnpjPrestador", failure.DestinationXPath);
        Assert.DoesNotContain("00000000000000", failure.Message);
        Assert.DoesNotContain("12345678000199", failure.Message);
        Assert.Null(await services.Repository.GetActiveVersionAsync(Key));
    }

    [Fact]
    public async Task Content_publication_that_passes_every_mandatory_test_is_published_and_audited()
    {
        var services = new TestServices();

        var result = await services.PublishAsync(Key, SnapshotFixtures.SimpleDps(), SnapshotFixtures.DpsScenarios(), "autor");

        Assert.Equal(1, result.SnapshotVersion);
        var published = Assert.Single(services.Audit.Events, e => e.Action == "template.publish" && e.Outcome == "success");
        Assert.Equal("autor", published.Actor);
        Assert.NotEqual("0", published.Details["structuralCases"]);
    }

    // ------------------------------------------------------------------ camadas

    private sealed class LayerFixture
    {
        private readonly TestServices _services = new();

        public InMemorySnapshotRepository Snapshots => _services.Repository;
        public LayeredTemplateService Admin => _services.Layered;

        public async Task<(LayerRef Base, LayerRef Client)> BaseAndClientAsync(IReadOnlyList<LayerOverride>? clientOverrides = null)
        {
            var baseRef = (await Admin.CreateLayerVersionAsync(LayeredTemplateServiceTests.BaseLayer(), "fornecedor")).Layer.Ref;
            var client = (await Admin.CreateLayerVersionAsync(new NewLayerVersion
            {
                OrganizationId = SnapshotFixtures.Organization,
                LayerId = "cliente-dps",
                Kind = LayerKind.Client,
                Parent = baseRef,
                Overrides = clientOverrides ?? [],
            }, "cliente")).Layer.Ref;
            return (baseRef, client);
        }
    }

    [Fact]
    public async Task Binding_without_mandatory_scenario_or_with_a_structural_defect_is_not_published()
    {
        var fixture = new LayerFixture();
        var (_, client) = await fixture.BaseAndClientAsync();

        var withoutScenario = await fixture.Admin.BindAndPublishAsync(Key, client, UpdatePolicy.Automatic, [], "cliente");
        Assert.False(withoutScenario.Success);
        Assert.Contains(withoutScenario.Diagnostics, d => d.Code == FunctionalScenarioGate.ScenarioRequiredCode);

        var (_, overflow) = await fixture.BaseAndClientAsync(
            [new LayerOverride("/DPS/serie", OverrideOperation.Set, MappingEntry.Scalar(OperatorNode.Of("concat", new LiteralNode("SERIE-"), new FieldReferenceNode("Serie"))))]);
        var defective = await fixture.Admin.BindAndPublishAsync(Key, overflow, UpdatePolicy.Automatic, SnapshotFixtures.DpsScenarios(), "cliente");
        Assert.False(defective.Success);
        Assert.Contains(defective.Diagnostics, d => d.Code == StructuralTestGate.StructuralTestFailedCode);
        Assert.False(defective.Report!.Passed);

        Assert.Null(await fixture.Snapshots.GetActiveVersionAsync(Key));
    }

    [Fact]
    public async Task Layer_update_runs_the_whole_suite_and_records_scenario_regressions()
    {
        var fixture = new LayerFixture();
        var (_, client) = await fixture.BaseAndClientAsync();
        var published = await fixture.Admin.BindAndPublishAsync(Key, client, UpdatePolicy.Manual, SnapshotFixtures.DpsScenarios(), "cliente");
        Assert.True(published.Success, string.Join("; ", published.Diagnostics.Select(d => d.Code)));
        Assert.True(published.Report!.Passed);

        // Nova base muda a série (compatível) → proposta com regressão por cenário.
        var compatible = LayeredTemplateServiceTests.BaseLayer() with
        {
            Overrides = [.. LayeredTemplateServiceTests.BaseLayer().Overrides.Where(o => o.Path != "/DPS/serie"),
                new LayerOverride("/DPS/serie", OverrideOperation.Set, MappingEntry.Scalar(OperatorNode.Of("concat", new LiteralNode("B"), new FieldReferenceNode("Serie"))))],
        };
        await fixture.Admin.CreateLayerVersionAsync(compatible, "fornecedor");
        var proposal = (await fixture.Admin.ListUpdatesAsync(Key)).Single();

        Assert.Equal(UpdateStatus.Proposed, proposal.Status);
        Assert.True(proposal.Report!.Passed);
        var regression = Assert.Single(proposal.Regressions);
        Assert.True(regression.Changed);
        Assert.Equal("/DPS/serie", regression.DestinationXPath);

        // Nova base que estoura a série: a suíte estrutural reprova a atualização.
        var breaking = LayeredTemplateServiceTests.BaseLayer() with
        {
            Overrides = [.. LayeredTemplateServiceTests.BaseLayer().Overrides.Where(o => o.Path != "/DPS/serie"),
                new LayerOverride("/DPS/serie", OverrideOperation.Set, MappingEntry.Scalar(OperatorNode.Of("concat", new LiteralNode("SERIE-"), new FieldReferenceNode("Serie"))))],
        };
        await fixture.Admin.CreateLayerVersionAsync(breaking, "fornecedor");
        var failed = (await fixture.Admin.ListUpdatesAsync(Key)).First();
        Assert.Contains(failed.GateFailures, d => d.Code == StructuralTestGate.StructuralTestFailedCode);
        Assert.Equal(1, await fixture.Snapshots.GetActiveVersionAsync(Key));
    }

    private static async Task<TestServices> PublishedWithBaseAsync(NewLayerVersion baseLayer, Engine.Operators.RuleOperatorCatalog? operators = null)
    {
        var services = new TestServices(operators: operators);
        var baseRef = (await services.Layered.CreateLayerVersionAsync(baseLayer, "fornecedor")).Layer.Ref;
        var client = (await services.Layered.CreateLayerVersionAsync(new NewLayerVersion
        {
            OrganizationId = SnapshotFixtures.Organization,
            LayerId = "cliente-dps",
            Kind = LayerKind.Client,
            Parent = baseRef,
        }, "cliente")).Layer.Ref;
        var published = await services.Layered.BindAndPublishAsync(Key, client, UpdatePolicy.Manual, SnapshotFixtures.DpsScenarios(), "cliente");
        Assert.True(published.Success, string.Join("; ", published.Diagnostics.Select(d => d.Code)));
        return services;
    }

    private static LayeredTemplateService SameStores(TestServices services, SnapshotCompiler compiler, Generation.GenerationLimits? limits = null) =>
        new(services.Layers, services.Bindings, services.Updates, services.Snapshots, services.Audit, compiler, limits);

    [Fact]
    public async Task Scenario_that_cannot_be_read_appears_in_the_regression_with_the_reason_and_no_data()
    {
        var services = await PublishedWithBaseAsync(LayeredTemplateServiceTests.BaseLayer());

        // Outra instância com limite de texto menor que o CNPJ do cenário: nenhum dos lados lê a entrada.
        var tight = SameStores(services, services.Compiler, new Generation.GenerationLimits { MaxTextLength = 5 });
        await tight.CreateLayerVersionAsync(LayeredTemplateServiceTests.BaseLayer(), "fornecedor");

        var regression = Assert.Single((await services.Layered.ListUpdatesAsync(Key)).Single().Regressions);
        Assert.True(regression.Changed);
        Assert.Contains("limite 'text'", regression.Problem);
        Assert.DoesNotContain("12345678000199", regression.Problem);
    }

    [Fact]
    public async Task Active_version_that_no_longer_compiles_marks_every_scenario_as_not_compared()
    {
        var withIdentity = LayeredTemplateServiceTests.BaseLayer() with
        {
            Overrides = [.. LayeredTemplateServiceTests.BaseLayer().Overrides.Where(o => o.Path != "/DPS/serie"),
                new LayerOverride("/DPS/serie", OverrideOperation.Set, MappingEntry.Scalar(OperatorNode.Of("identidade", new FieldReferenceNode("Serie"))))],
        };
        var services = await PublishedWithBaseAsync(withIdentity, SnapshotPublisherTests.CatalogWithIdentityOperator());

        // Instância sem o operador "identidade": a ativa não compila, a proposta (sem ele) compila.
        await SameStores(services, new SnapshotCompiler()).CreateLayerVersionAsync(LayeredTemplateServiceTests.BaseLayer(), "fornecedor");

        var regression = Assert.Single((await services.Layered.ListUpdatesAsync(Key)).Single().Regressions);
        Assert.Equal("DPS realista", regression.Scenario);
        Assert.Contains("versão ativa não compila", regression.Problem);
    }

    // ------------------------------------------------------------------ asserções e comparação semântica

    private static readonly XDocument Dps = XDocument.Parse("""
        <DPS xmlns="urn:x" versao="1.00"><numero>1</numero><itens><item>a</item><item>b</item></itens></DPS>
        """);

    [Theory]
    [InlineData("/DPS/numero", AssertionKind.Exists, null, true)]
    [InlineData("/DPS/serie", AssertionKind.Exists, null, false)]
    [InlineData("/DPS/serie", AssertionKind.Absent, null, true)]
    [InlineData("/DPS/numero", AssertionKind.Equals, "1", true)]
    [InlineData("/DPS/numero", AssertionKind.Equals, "2", false)]
    [InlineData("/DPS/itens/item", AssertionKind.Count, "2", true)]
    [InlineData("/DPS/itens/item", AssertionKind.Count, "3", false)]
    [InlineData("/DPS/@versao", AssertionKind.Equals, "1.00", true)]
    [InlineData("/Outro/numero", AssertionKind.Exists, null, false)]
    public void Structural_assertions_match_by_local_name(string path, AssertionKind kind, string? expected, bool satisfied)
    {
        var problem = StructuralAssertions.Check(Dps, new StructuralAssertion(path, kind, expected));

        Assert.Equal(satisfied, problem is null);
        if (problem is not null && expected is not null)
        {
            Assert.DoesNotContain($"'{expected}'", problem);
        }
    }

    [Theory]
    [InlineData("<a:R xmlns:a=\"urn:x\"><a:v>1</a:v></a:R>", "<R xmlns=\"urn:x\">\n  <v>1</v>\n</R>", true)]
    [InlineData("<R xmlns=\"urn:x\" p=\"1\" q=\"2\"><v/></R>", "<R xmlns=\"urn:x\" q=\"2\" p=\"1\"><v/></R>", true)]
    [InlineData("<R xmlns=\"urn:x\"><v>1</v><!-- comentário --></R>", "<R xmlns=\"urn:x\"><v><![CDATA[1]]></v></R>", true)]
    [InlineData("<R xmlns=\"urn:x\"><v>1</v></R>", "<R xmlns=\"urn:y\"><v>1</v></R>", false)]
    [InlineData("<R xmlns=\"urn:x\"><v>1</v><w>2</w></R>", "<R xmlns=\"urn:x\"><w>2</w><v>1</v></R>", false)]
    [InlineData("<R xmlns=\"urn:x\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\"><v xsi:nil=\"true\"/></R>", "<R xmlns=\"urn:x\"><v/></R>", false)]
    public void Semantic_comparison_ignores_prefix_formatting_attribute_order_and_comments_only(string expected, string actual, bool equal)
    {
        Assert.Equal(equal, XmlSemanticComparer.Compare(expected, actual).Equal);
    }
}

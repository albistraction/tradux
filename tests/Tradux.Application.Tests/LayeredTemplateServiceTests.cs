using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Tradux.Application.Layers;
using Tradux.Contracts;
using Tradux.Domain.Layers;
using Tradux.Domain.Mapping;
using Tradux.Tests.Shared;

using Tradux.Application.Testing;
using Tradux.Application.Publishing;
namespace Tradux.Application.Tests;

/// <summary>
/// Fase 7 (seções 16–17): base → produto → cliente com proveniência,
/// atualização manual/automática, conflitos, rollback, fallback e auditoria.
/// Gate: "uma atualização incompatível não altera a versão ativa; toda
/// decisão de resolução pode ser explicada."
/// </summary>
public class LayeredTemplateServiceTests
{
    private const string Vendor = "fornecedor";
    private static readonly TemplateKey Key = new(SnapshotFixtures.Organization, "dps", SnapshotFixtures.Operation);
    private static readonly XNamespace Ns = "http://exemplo.ilustrativo.tradux/dps/v1";

    private sealed class Fixture
    {
        private readonly TestServices _services = new();

        /// <summary>Repositório interno: publicações "por fora" do serviço e ganchos de concorrência.</summary>
        public InMemorySnapshotRepository Snapshots => _services.Repository;
        public InMemoryAuditSink Audit => _services.Audit;
        public LayeredTemplateService Admin => _services.Layered;
        public Generation.XmlGenerationService Generation => _services.Generation;
        public LayerRef Base { get; private set; } = null!;
        public LayerRef Product { get; private set; } = null!;
        public LayerRef Client { get; private set; } = null!;

        /// <summary>Base (fornecedor, compartilhada) → produto (fornecedor, compartilhado) → cliente (org-a).</summary>
        public async Task<PublicationResult> PublishChainAsync(UpdatePolicy policy, IReadOnlyList<FunctionalScenario>? scenarios = null)
        {
            Base = (await Admin.CreateLayerVersionAsync(BaseLayer(), "fornecedor-admin")).Layer.Ref;
            Product = (await Admin.CreateLayerVersionAsync(ProductLayer(Base, "P-"), "fornecedor-admin")).Layer.Ref;
            Client = (await Admin.CreateLayerVersionAsync(new NewLayerVersion
            {
                OrganizationId = SnapshotFixtures.Organization,
                LayerId = "cliente-dps",
                Kind = LayerKind.Client,
                Parent = Product,
                Overrides =
                [
                    new LayerOverride("/DPS/numero", OverrideOperation.Set,
                        MappingEntry.Scalar(OperatorNode.Of("concat", new LiteralNode("C-"), new FieldReferenceNode("Numero"))), "cliente numera com prefixo"),
                    new LayerOverride("/DPS/discriminacaoServico", OverrideOperation.Remove, Comment: "cliente não envia discriminação"),
                ],
            }, "cliente-admin")).Layer.Ref;

            return await Admin.BindAndPublishAsync(Key, Client, policy, scenarios ?? SnapshotFixtures.DpsScenarios(), "cliente-admin");
        }
    }

    internal static NewLayerVersion BaseLayer(LayerSchema? schema = null, Func<string, bool>? keepPath = null)
    {
        var content = SnapshotFixtures.SimpleDps();
        return new NewLayerVersion
        {
            OrganizationId = Vendor,
            LayerId = "base-dps",
            Kind = LayerKind.Base,
            Shared = true,
            Schema = schema ?? new LayerSchema(content.SchemaFiles, content.SchemaEntryPoint),
            Overrides = LayerOverrides.FromMappingTemplate(content.Mapping, "DPS").Where(o => keepPath?.Invoke(o.Path) ?? true).ToList(),
            LookupOverrides = LayerOverrides.FromLookupTables(content.LookupTables),
        };
    }

    internal static NewLayerVersion ProductLayer(LayerRef parent, string seriePrefix, IReadOnlyList<LayerOverride>? extra = null) => new()
    {
        OrganizationId = Vendor,
        LayerId = "produto-dps",
        Kind = LayerKind.Product,
        Parent = parent,
        Shared = true,
        Overrides =
        [
            new LayerOverride("/DPS/serie", OverrideOperation.Set,
                MappingEntry.Scalar(OperatorNode.Of("concat", new LiteralNode(seriePrefix), new FieldReferenceNode("Serie")))),
            .. extra ?? [],
        ],
    };

    /// <summary>
    /// A atualização que a camada lançada gerou no template de org-a, lida do
    /// histórico do template: quem lança a camada (fornecedor) não recebe as
    /// atualizações de outras organizações.
    /// </summary>
    private static async Task<TemplateUpdate> UpdateForAsync(Fixture fixture, LayerVersionResult released)
    {
        Assert.DoesNotContain(released.Updates, u => u.Key.OrganizationId != released.Layer.Ref.OrganizationId);
        var updates = await fixture.Admin.ListUpdatesAsync(Key);
        return Assert.Single(updates, u => u.Trigger == released.Layer.Ref);
    }

    private static async Task<XDocument> GenerateAsync(Fixture fixture)
    {
        var result = await fixture.Generation.GenerateAsync(new GenerateXmlRequest(Key.OrganizationId, Key.TemplateId, Key.Operation, SnapshotFixtures.DpsJson()));
        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Code)));
        return XDocument.Parse(result.Xml!);
    }

    [Fact]
    public async Task Chain_resolves_base_product_client_and_publishes()
    {
        var fixture = new Fixture();

        var result = await fixture.PublishChainAsync(UpdatePolicy.Automatic);

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var xml = await GenerateAsync(fixture);
        Assert.Equal("C-000123", xml.Root!.Element(Ns + "numero")!.Value);
        Assert.Equal("P-A1", xml.Root.Element(Ns + "serie")!.Value);
        Assert.Null(xml.Root.Element(Ns + "discriminacaoServico"));
    }

    [Fact]
    public async Task Every_published_rule_is_explained_with_its_full_history()
    {
        var fixture = new Fixture();
        await fixture.PublishChainAsync(UpdatePolicy.Automatic);

        var explanation = (await fixture.Admin.ExplainAsync(Key))!;
        var manifest = explanation.Manifest!;
        var active = (await fixture.Snapshots.GetActiveAsync(Key))!;

        Assert.Equal([fixture.Base, fixture.Product, fixture.Client], manifest.Layers.Select(l => l.Ref));
        foreach (var path in MappingPaths.Flatten(active.Content.Mapping, "DPS").Keys)
        {
            Assert.True(manifest.Paths.TryGetValue(path, out var trace), $"regra publicada sem explicação: {path}");
            Assert.NotNull(trace.FinalSource);
        }

        var numero = manifest.Paths["/DPS/numero"];
        Assert.Equal(fixture.Client, numero.FinalSource);
        Assert.Equal([LayerKind.Base, LayerKind.Client], numero.History.Select(s => s.Kind));
        Assert.Equal("cliente numera com prefixo", numero.History[^1].Comment);
        Assert.Equal(fixture.Product, manifest.Paths["/DPS/serie"].FinalSource);

        var removed = manifest.Paths["/DPS/discriminacaoServico"];
        Assert.Null(removed.FinalSource);
        Assert.Equal([OverrideOperation.Set, OverrideOperation.Remove], removed.History.Select(s => s.Operation));
        Assert.Equal(fixture.Base, manifest.LookupTables["situacoes"].FinalSource);
    }

    [Fact]
    public async Task Compatible_product_update_is_applied_automatically_rebasing_the_client()
    {
        var fixture = new Fixture();
        await fixture.PublishChainAsync(UpdatePolicy.Automatic);

        var released = await fixture.Admin.CreateLayerVersionAsync(ProductLayer(fixture.Base, "Q-"), "fornecedor-admin");

        var update = await UpdateForAsync(fixture, released);
        Assert.Equal(UpdateStatus.Applied, update.Status);
        Assert.Equal(2, update.AppliedSnapshotVersion);
        Assert.Contains(update.Changes, c => c.Path == "/DPS/serie" && c.Kind == ChangeKind.Changed);
        Assert.DoesNotContain(update.Changes, c => c.Path == "/DPS/numero");
        var xml = await GenerateAsync(fixture);
        Assert.Equal("Q-A1", xml.Root!.Element(Ns + "serie")!.Value);
        Assert.Equal("C-000123", xml.Root.Element(Ns + "numero")!.Value);

        var explanation = (await fixture.Admin.ExplainAsync(Key))!;
        var client = explanation.Manifest!.Layers[^1];
        Assert.Equal(("cliente-dps", 2), (client.Ref.LayerId, client.Ref.Version));
        Assert.Equal(released.Layer.Ref, explanation.Manifest.Layers[1].Ref);
    }

    [Fact]
    public async Task Update_that_breaks_a_functional_scenario_does_not_change_the_active_version()
    {
        var fixture = new Fixture();
        await fixture.PublishChainAsync(UpdatePolicy.Automatic);
        var before = (await GenerateAsync(fixture)).ToString();

        // Produto novo passa a mandar texto num xs:decimal: compila, mas o cenário reprova.
        var broken = ProductLayer(fixture.Base, "P-", [new LayerOverride("/DPS/valorServico", OverrideOperation.Set, MappingEntry.Scalar(new LiteralNode("abc")))]);
        var released = await fixture.Admin.CreateLayerVersionAsync(broken, "fornecedor-admin");

        var update = await UpdateForAsync(fixture, released);
        Assert.Equal(UpdateStatus.Failed, update.Status);
        Assert.Contains(update.GateFailures, d => d.Code == FunctionalScenarioGate.ScenarioFailedCode);
        Assert.Equal(1, await fixture.Snapshots.GetActiveVersionAsync(Key));
        Assert.Equal(before, (await GenerateAsync(fixture)).ToString());
        Assert.Contains(fixture.Audit.Events, e => e.Action == "template.update.apply" && e.Outcome == "rejected");
    }

    [Fact]
    public async Task Base_update_that_removes_a_path_the_client_overrides_is_a_conflict_and_keeps_the_active_version()
    {
        var fixture = new Fixture();
        // Cadeia sem produto: cliente direto sobre a base.
        var baseRef = (await fixture.Admin.CreateLayerVersionAsync(BaseLayer(), "fornecedor-admin")).Layer.Ref;
        var client = (await fixture.Admin.CreateLayerVersionAsync(new NewLayerVersion
        {
            OrganizationId = SnapshotFixtures.Organization,
            LayerId = "cliente-dps",
            Kind = LayerKind.Client,
            Parent = baseRef,
            Overrides = [new LayerOverride("/DPS/serie", OverrideOperation.Set, MappingEntry.Scalar(new LiteralNode("X1")))],
        }, "cliente-admin")).Layer.Ref;
        Assert.True((await fixture.Admin.BindAndPublishAsync(Key, client, UpdatePolicy.Automatic, SnapshotFixtures.DpsScenarios(), "cliente-admin")).Success);

        // Nova base: layout sem o elemento "serie".
        var xsd = Encoding.UTF8.GetString(File.ReadAllBytes(SnapshotFixtures.SchemaPath("exemplo-simples.xsd")));
        var withoutSerie = Regex.Replace(xsd, "<xs:element name=\"serie\"[\\s\\S]*?</xs:element>", "");
        Assert.NotEqual(xsd, withoutSerie);
        var newBase = BaseLayer(new LayerSchema(new Dictionary<string, byte[]> { ["exemplo-simples.xsd"] = Encoding.UTF8.GetBytes(withoutSerie) }, "exemplo-simples.xsd"),
            keepPath: p => p != "/DPS/serie");

        var released = await fixture.Admin.CreateLayerVersionAsync(newBase, "fornecedor-admin");

        var update = await UpdateForAsync(fixture, released);
        Assert.Equal(UpdateStatus.Failed, update.Status);
        var conflict = Assert.Single(update.Conflicts, c => c.Code == LayerResolver.PathNotInSchema);
        Assert.Equal("/DPS/serie", conflict.Path);
        Assert.Equal(client, conflict.IntroducedBy);
        Assert.Equal(1, await fixture.Snapshots.GetActiveVersionAsync(Key));
        Assert.Equal("X1", (await GenerateAsync(fixture)).Root!.Element(Ns + "serie")!.Value);
    }

    [Fact]
    public async Task Manual_policy_proposes_with_differences_and_publishes_only_on_approval()
    {
        var fixture = new Fixture();
        await fixture.PublishChainAsync(UpdatePolicy.Manual);

        var released = await fixture.Admin.CreateLayerVersionAsync(ProductLayer(fixture.Base, "Q-"), "fornecedor-admin");
        var proposal = await UpdateForAsync(fixture, released);

        Assert.Equal(UpdateStatus.Proposed, proposal.Status);
        var change = Assert.Single(proposal.Changes);
        Assert.Equal(("/DPS/serie", ChangeKind.Changed), (change.Path, change.Kind));
        Assert.Equal(fixture.Product, change.PreviousSource);
        Assert.Equal(released.Layer.Ref, change.NewSource);
        Assert.Empty(proposal.GateFailures);
        Assert.Equal(1, await fixture.Snapshots.GetActiveVersionAsync(Key));

        var applied = await fixture.Admin.ApproveUpdateAsync(Key, proposal.Id, "cliente-admin");

        Assert.Equal(UpdateStatus.Applied, applied.Status);
        Assert.Equal("Q-A1", (await GenerateAsync(fixture)).Root!.Element(Ns + "serie")!.Value);
        Assert.Equal("cliente-admin", applied.ResolvedBy);
    }

    [Fact]
    public async Task Rejected_and_superseded_proposals_never_publish()
    {
        var fixture = new Fixture();
        await fixture.PublishChainAsync(UpdatePolicy.Manual);

        var first = await UpdateForAsync(fixture, await fixture.Admin.CreateLayerVersionAsync(ProductLayer(fixture.Base, "Q-"), "f"));
        var second = await UpdateForAsync(fixture, await fixture.Admin.CreateLayerVersionAsync(ProductLayer(fixture.Base, "R-"), "f"));
        await fixture.Admin.RejectUpdateAsync(Key, second.Id, "cliente-admin");

        var updates = await fixture.Admin.ListUpdatesAsync(Key);
        Assert.Equal(UpdateStatus.Superseded, updates.Single(u => u.Id == first.Id).Status);
        Assert.Equal(UpdateStatus.Rejected, updates.Single(u => u.Id == second.Id).Status);
        Assert.Equal(1, await fixture.Snapshots.GetActiveVersionAsync(Key));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Admin.ApproveUpdateAsync(Key, second.Id, "cliente-admin"));
    }

    [Fact]
    public async Task Rollback_reactivates_the_previous_version_and_is_audited()
    {
        var fixture = new Fixture();
        await fixture.PublishChainAsync(UpdatePolicy.Automatic);
        await fixture.Admin.CreateLayerVersionAsync(ProductLayer(fixture.Base, "Q-"), "fornecedor-admin");
        Assert.Equal(2, await fixture.Snapshots.GetActiveVersionAsync(Key));

        var rollback = await fixture.Admin.RollbackAsync(Key, null, "cliente-admin");

        Assert.True(rollback.Success);
        Assert.Equal((1, 2), (rollback.ActiveVersion, rollback.PreviousVersion));
        Assert.Equal("P-A1", (await GenerateAsync(fixture)).Root!.Element(Ns + "serie")!.Value);
        Assert.Contains(fixture.Audit.Events, e => e.Action == "template.rollback" && e.Outcome == "success" && e.Details["to"] == "1");
        Assert.False((await fixture.Admin.RollbackAsync(Key, 99, "x")).Success);
    }

    [Fact]
    public async Task Fallback_uses_the_first_candidate_that_generates_and_groups_errors_by_version()
    {
        var fixture = new Fixture();
        await fixture.PublishChainAsync(UpdatePolicy.Automatic);
        await fixture.Admin.CreateLayerVersionAsync(ProductLayer(fixture.Base, "Q-"), "fornecedor-admin");
        var request = new GenerateXmlRequest(Key.OrganizationId, Key.TemplateId, Key.Operation, SnapshotFixtures.DpsJson());

        var fallback = await fixture.Generation.GenerateAsync(request with { CandidateVersions = [7, 1, 2] });
        Assert.True(fallback.Success);
        Assert.Equal("1", fallback.VersionUsed);
        Assert.Equal(["7", "1"], fallback.VersionsAttempted.Select(a => a.Version));
        Assert.False(fallback.VersionsAttempted[0].Success);

        var allFail = await fixture.Generation.GenerateAsync(request with
        {
            Data = SnapshotFixtures.DpsJson(cnpjPrestador: "123"),
            CandidateVersions = [2, 1],
        });
        Assert.False(allFail.Success);
        Assert.Null(allFail.VersionUsed);
        Assert.Equal(["2", "1"], allFail.VersionsAttempted.Select(a => a.Version));
        Assert.All(allFail.VersionsAttempted, a => Assert.Contains(a.Diagnostics, d => d.Code == "XSD_VALIDATION"));
    }

    [Fact]
    public async Task Base_and_product_are_protected_and_layers_follow_the_chain_rules()
    {
        var fixture = new Fixture();
        var privateBase = (await fixture.Admin.CreateLayerVersionAsync(BaseLayer() with { Shared = false, LayerId = "base-privada" }, "f")).Layer.Ref;

        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Admin.CreateLayerVersionAsync(new NewLayerVersion
        {
            OrganizationId = SnapshotFixtures.Organization, LayerId = "c", Kind = LayerKind.Client, Parent = privateBase,
        }, "x"));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Admin.CreateLayerVersionAsync(
            BaseLayer() with { Overrides = [new LayerOverride("/DPS/numero", OverrideOperation.Remove)] }, "x"));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Admin.CreateLayerVersionAsync(
            ProductLayer(privateBase, "P-") with { Schema = BaseLayer().Schema }, "x"));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Admin.CreateLayerVersionAsync(
            new NewLayerVersion { OrganizationId = Vendor, LayerId = "base-privada", Kind = LayerKind.Product, Parent = privateBase }, "x"));

        var otherOrgTop = await fixture.Admin.BindAndPublishAsync(new TemplateKey("org-c", "dps", "emissao"), privateBase, UpdatePolicy.Manual, [], "x");
        Assert.False(otherOrgTop.Success);
    }

    // ---------------------------------------------------------------- isolamento, concorrência e propostas

    [Fact]
    public async Task New_unshared_version_of_another_orgs_layer_is_not_propagated()
    {
        var fixture = new Fixture();
        await fixture.PublishChainAsync(UpdatePolicy.Automatic);

        // O fornecedor lança uma versão do produto que NÃO é compartilhada.
        var released = await fixture.Admin.CreateLayerVersionAsync(ProductLayer(fixture.Base, "Q-") with { Shared = false }, "fornecedor-admin");

        // Nem aplicada nem registrada no cliente: um registro revelaria que a
        // versão privada existe.
        var updates = await fixture.Admin.ListUpdatesAsync(Key);
        Assert.DoesNotContain(updates, u => u.Trigger == released.Layer.Ref);
        Assert.DoesNotContain(fixture.Audit.Events, e => e.OrganizationId == Key.OrganizationId && e.Details.Values.Any(v => v.Contains(released.Layer.Ref.ToString())));
        Assert.Equal(1, await fixture.Snapshots.GetActiveVersionAsync(Key));
        Assert.Equal("P-A1", (await GenerateAsync(fixture)).Root!.Element(Ns + "serie")!.Value);
    }

    [Fact]
    public async Task Private_base_version_under_another_orgs_client_leaves_no_trace_there()
    {
        // Caso do rebase: cliente da org-a direto sobre a base compartilhada do
        // fornecedor; fornecedor lança base v2 privada.
        var fixture = new Fixture();
        var baseRef = (await fixture.Admin.CreateLayerVersionAsync(BaseLayer(), "fornecedor-admin")).Layer.Ref;
        var client = (await fixture.Admin.CreateLayerVersionAsync(new NewLayerVersion
        {
            OrganizationId = SnapshotFixtures.Organization, LayerId = "cliente-dps", Kind = LayerKind.Client, Parent = baseRef,
        }, "cliente-admin")).Layer.Ref;
        Assert.True((await fixture.Admin.BindAndPublishAsync(Key, client, UpdatePolicy.Manual, SnapshotFixtures.DpsScenarios(), "cliente-admin")).Success);

        var privateV2 = await fixture.Admin.CreateLayerVersionAsync(BaseLayer() with { Shared = false }, "fornecedor-admin");

        Assert.Empty(await fixture.Admin.ListUpdatesAsync(Key));
        Assert.DoesNotContain(fixture.Audit.Events, e => e.OrganizationId == Key.OrganizationId && e.Details.Values.Any(v => v.Contains(privateV2.Layer.Ref.ToString())));

        // A versão compartilhada seguinte volta a propagar normalmente.
        var sharedV3 = await fixture.Admin.CreateLayerVersionAsync(BaseLayer(), "fornecedor-admin");
        Assert.Contains(await fixture.Admin.ListUpdatesAsync(Key), u => u.Trigger == sharedV3.Layer.Ref);
    }

    [Fact]
    public async Task Publication_and_rollback_supersede_pending_proposals()
    {
        var fixture = new Fixture();
        await fixture.PublishChainAsync(UpdatePolicy.Manual);
        var proposal = await UpdateForAsync(fixture, await fixture.Admin.CreateLayerVersionAsync(ProductLayer(fixture.Base, "Q-"), "fornecedor-admin"));

        // Cliente publica outra cadeia antes de aprovar: a proposta fica velha.
        var clientV2 = (await fixture.Admin.CreateLayerVersionAsync(new NewLayerVersion
        {
            OrganizationId = SnapshotFixtures.Organization, LayerId = "cliente-dps", Kind = LayerKind.Client, Parent = fixture.Product,
            Overrides = [new LayerOverride("/DPS/numero", OverrideOperation.Set, MappingEntry.Scalar(new LiteralNode("N2")))],
        }, "cliente-admin")).Layer.Ref;
        Assert.True((await fixture.Admin.PublishAsync(Key, clientV2, "cliente-admin")).Success);
        var activeAfterPublish = await fixture.Snapshots.GetActiveVersionAsync(Key);

        var updates = await fixture.Admin.ListUpdatesAsync(Key);
        Assert.Equal(UpdateStatus.Superseded, updates.Single(u => u.Id == proposal.Id).Status);
        Assert.DoesNotContain(updates, u => u.Status == UpdateStatus.Proposed);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Admin.ApproveUpdateAsync(Key, proposal.Id, "cliente-admin"));
        Assert.Equal(activeAfterPublish, await fixture.Snapshots.GetActiveVersionAsync(Key));
        Assert.Equal("N2", (await GenerateAsync(fixture)).Root!.Element(Ns + "numero")!.Value);

        // Rollback também invalida propostas feitas sobre a versão que saiu do ar.
        var another = await UpdateForAsync(fixture, await fixture.Admin.CreateLayerVersionAsync(ProductLayer(fixture.Base, "R-"), "fornecedor-admin"));
        Assert.Equal(UpdateStatus.Proposed, another.Status);
        Assert.True((await fixture.Admin.RollbackAsync(Key, null, "cliente-admin")).Success);
        updates = await fixture.Admin.ListUpdatesAsync(Key);
        Assert.Equal(UpdateStatus.Superseded, updates.Single(u => u.Id == another.Id).Status);
    }

    /// <summary>Grava uma versão sem passar pelo serviço de camadas (ex.: outra réplica, ou publicação de conteúdo).</summary>
    private static async Task PublishOutsideTheServiceAsync(Fixture fixture, Snapshots.SnapshotContent content, SnapshotManifest? manifest)
    {
        var publisher = new SnapshotPublisher(fixture.Snapshots, new Snapshots.SnapshotCompiler(), Generation.GenerationLimits.Default);
        var candidate = publisher.Prepare(content, out var failure) ?? throw new Xunit.Sdk.XunitException(failure!.Message);
        await publisher.WriteAsync(Key, candidate, manifest, await fixture.Snapshots.GetActiveVersionAsync(Key), [], CancellationToken.None);
    }

    [Fact]
    public async Task Approval_of_a_proposal_made_on_another_active_version_is_refused()
    {
        var fixture = new Fixture();
        await fixture.PublishChainAsync(UpdatePolicy.Manual);
        var proposal = await UpdateForAsync(fixture, await fixture.Admin.CreateLayerVersionAsync(ProductLayer(fixture.Base, "Q-"), "fornecedor-admin"));

        // O ativo muda por fora do serviço (ex.: outra réplica): a aprovação não pode reverter isso.
        var content = (await fixture.Snapshots.GetActiveAsync(Key))!;
        await PublishOutsideTheServiceAsync(fixture, content.Content, content.Manifest! with { Layers = [.. content.Manifest!.Layers.Take(2)] });
        var active = await fixture.Snapshots.GetActiveVersionAsync(Key);
        Assert.Equal(2, active);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Admin.ApproveUpdateAsync(Key, proposal.Id, "cliente-admin"));
        Assert.Equal(active, await fixture.Snapshots.GetActiveVersionAsync(Key));
        var stored = (await fixture.Admin.ListUpdatesAsync(Key)).Single(u => u.Id == proposal.Id);
        Assert.Equal(UpdateStatus.Superseded, stored.Status);
    }

    [Fact]
    public async Task Concurrent_propagation_never_leaves_an_older_layer_version_active()
    {
        var fixture = new Fixture();
        await fixture.PublishChainAsync(UpdatePolicy.Automatic);
        LayerVersionResult? newer = null;

        // Enquanto a propagação do produto v2 está para publicar, o produto v3
        // é lançado e propagado por inteiro — ele chega primeiro.
        fixture.Snapshots.BeforeGuardedPublish = async _ =>
            newer = await fixture.Admin.CreateLayerVersionAsync(ProductLayer(fixture.Base, "R-"), "fornecedor-admin");
        var older = await fixture.Admin.CreateLayerVersionAsync(ProductLayer(fixture.Base, "Q-"), "fornecedor-admin");

        Assert.NotNull(newer);
        Assert.Equal(3, newer!.Layer.Ref.Version);
        Assert.Equal(2, older.Layer.Ref.Version);
        var explanation = (await fixture.Admin.ExplainAsync(Key))!;
        Assert.Equal(newer.Layer.Ref, explanation.Manifest!.Layers[1].Ref);
        Assert.Equal("R-A1", (await GenerateAsync(fixture)).Root!.Element(Ns + "serie")!.Value);
        var updates = await fixture.Admin.ListUpdatesAsync(Key);
        Assert.DoesNotContain(updates, u => u.Trigger == older.Layer.Ref && u.Status == UpdateStatus.Applied);
    }

    [Fact]
    public async Task Unexpected_error_in_one_template_is_recorded_and_does_not_stop_the_others()
    {
        var fixture = new Fixture();
        await fixture.PublishChainAsync(UpdatePolicy.Automatic);
        var other = new TemplateKey(Key.OrganizationId, "dps-2", Key.Operation);
        Assert.True((await fixture.Admin.BindAndPublishAsync(other, fixture.Client, UpdatePolicy.Automatic, SnapshotFixtures.DpsScenarios(), "cliente-admin")).Success);
        fixture.Snapshots.FailReadsFor = other;

        var released = await fixture.Admin.CreateLayerVersionAsync(ProductLayer(fixture.Base, "Q-"), "fornecedor-admin");

        Assert.Equal(UpdateStatus.Applied, (await UpdateForAsync(fixture, released)).Status);
        var failed = Assert.Single(await fixture.Admin.ListUpdatesAsync(other));
        Assert.Equal(UpdateStatus.Failed, failed.Status);
        Assert.Contains(failed.GateFailures, d => d.Code == LayeredTemplateService.UnexpectedErrorCode);
    }

    [Fact]
    public async Task Bound_template_published_without_layers_records_why_it_was_not_updated()
    {
        var fixture = new Fixture();
        await fixture.PublishChainAsync(UpdatePolicy.Automatic);
        await PublishOutsideTheServiceAsync(fixture, SnapshotFixtures.SimpleDps(), manifest: null);

        var released = await fixture.Admin.CreateLayerVersionAsync(ProductLayer(fixture.Base, "Q-"), "fornecedor-admin");

        var update = await UpdateForAsync(fixture, released);
        Assert.Equal(UpdateStatus.Failed, update.Status);
        Assert.Contains(update.GateFailures, d => d.Code == LayeredTemplateService.ActiveWithoutLayersCode);
        Assert.Equal(2, await fixture.Snapshots.GetActiveVersionAsync(Key));
    }

    [Fact]
    public async Task New_client_version_uses_the_parent_it_declares()
    {
        var fixture = new Fixture();
        await fixture.PublishChainAsync(UpdatePolicy.Manual);
        var productV2 = (await fixture.Admin.CreateLayerVersionAsync(ProductLayer(fixture.Base, "Q-"), "fornecedor-admin")).Layer.Ref;

        // O cliente já escreve a nova versão sobre o produto v2, que ainda não está ativo.
        var released = await fixture.Admin.CreateLayerVersionAsync(new NewLayerVersion
        {
            OrganizationId = SnapshotFixtures.Organization, LayerId = "cliente-dps", Kind = LayerKind.Client, Parent = productV2,
            Overrides = [new LayerOverride("/DPS/numero", OverrideOperation.Set, MappingEntry.Scalar(new LiteralNode("N3")))],
        }, "cliente-admin");

        var proposal = Assert.Single(released.Updates);
        Assert.Equal(UpdateStatus.Proposed, proposal.Status);
        Assert.Empty(proposal.GateFailures);
        Assert.Equal([fixture.Base, productV2, released.Layer.Ref], proposal.ProposedChain);

        var applied = await fixture.Admin.ApproveUpdateAsync(Key, proposal.Id, "cliente-admin");
        Assert.Equal(UpdateStatus.Applied, applied.Status);
        var xml = await GenerateAsync(fixture);
        Assert.Equal(("Q-A1", "N3"), (xml.Root!.Element(Ns + "serie")!.Value, xml.Root.Element(Ns + "numero")!.Value));
    }

    [Fact]
    public async Task Client_version_on_an_older_parent_is_only_proposed_even_with_automatic_policy()
    {
        var fixture = new Fixture();
        await fixture.PublishChainAsync(UpdatePolicy.Automatic);
        await fixture.Admin.CreateLayerVersionAsync(ProductLayer(fixture.Base, "Q-"), "fornecedor-admin"); // ativo: produto v2
        var active = await fixture.Snapshots.GetActiveVersionAsync(Key);

        var released = await fixture.Admin.CreateLayerVersionAsync(new NewLayerVersion
        {
            OrganizationId = SnapshotFixtures.Organization, LayerId = "cliente-dps", Kind = LayerKind.Client, Parent = fixture.Product,
            Overrides = [new LayerOverride("/DPS/numero", OverrideOperation.Set, MappingEntry.Scalar(new LiteralNode("N4")))],
        }, "cliente-admin");

        var proposal = Assert.Single(released.Updates);
        Assert.Equal(UpdateStatus.Proposed, proposal.Status);
        Assert.Contains(proposal.Conflicts, c => c.Code == LayeredTemplateService.LayerDowngradeCode);
        Assert.Equal(active, await fixture.Snapshots.GetActiveVersionAsync(Key));
        Assert.Equal("Q-A1", (await GenerateAsync(fixture)).Root!.Element(Ns + "serie")!.Value);
    }

    [Fact]
    public async Task Failed_release_keeps_the_valid_proposal_and_automatic_apply_supersedes_pending()
    {
        var fixture = new Fixture();
        await fixture.PublishChainAsync(UpdatePolicy.Manual);
        var valid = await UpdateForAsync(fixture, await fixture.Admin.CreateLayerVersionAsync(ProductLayer(fixture.Base, "Q-"), "fornecedor-admin"));

        var broken = ProductLayer(fixture.Base, "R-", [new LayerOverride("/DPS/naoExiste", OverrideOperation.Set, MappingEntry.Scalar(new LiteralNode("x")))]);
        var failed = await UpdateForAsync(fixture, await fixture.Admin.CreateLayerVersionAsync(broken, "fornecedor-admin"));
        Assert.NotEmpty(failed.GateFailures);
        var updates = await fixture.Admin.ListUpdatesAsync(Key);
        Assert.Equal(UpdateStatus.Proposed, updates.Single(u => u.Id == valid.Id).Status);

        await fixture.Admin.ConfigureBindingAsync(Key, UpdatePolicy.Automatic, null, "cliente-admin");
        var applied = await UpdateForAsync(fixture, await fixture.Admin.CreateLayerVersionAsync(ProductLayer(fixture.Base, "S-"), "fornecedor-admin"));
        Assert.Equal(UpdateStatus.Applied, applied.Status);
        updates = await fixture.Admin.ListUpdatesAsync(Key);
        Assert.DoesNotContain(updates, u => u.Status == UpdateStatus.Proposed);
        Assert.Contains(fixture.Audit.Events, e => e.Action == "template.update.supersede");
    }

    [Fact]
    public async Task Scenario_failures_never_carry_scenario_values()
    {
        var fixture = new Fixture();
        var expected = "<DPS xmlns=\"http://exemplo.ilustrativo.tradux/dps/v1\"><numero>VALOR-SIGILOSO-999</numero></DPS>";
        var result = await fixture.PublishChainAsync(UpdatePolicy.Automatic,
            [new FunctionalScenario("esperado divergente", SnapshotFixtures.DpsJson(), expected)]);

        Assert.False(result.Success);
        var failure = Assert.Single(result.Diagnostics, d => d.Code == FunctionalScenarioGate.ScenarioFailedCode);
        Assert.DoesNotContain("VALOR-SIGILOSO-999", failure.Message);
        Assert.DoesNotContain("C-000123", failure.Message);
        Assert.NotNull(failure.DestinationXPath);
    }

    [Fact]
    public async Task Audit_events_record_actions_without_fiscal_payload()
    {
        var fixture = new Fixture();
        await fixture.PublishChainAsync(UpdatePolicy.Automatic);

        Assert.Contains(fixture.Audit.Events, e => e.Action == "layer.version.create" && e.Actor == "cliente-admin" && e.OrganizationId == SnapshotFixtures.Organization);
        Assert.Contains(fixture.Audit.Events, e => e.Action == "template.publish" && e.Outcome == "success" && e.Details["version"] == "1");
        var serialized = string.Join("\n", fixture.Audit.Events.SelectMany(e => e.Details.Values.Append(e.Target)));
        Assert.DoesNotContain("000123", serialized);
        Assert.DoesNotContain("12345678000199", serialized);
    }
}

/// <summary>Resolução pura: conflitos e avisos por tipo (sem publicação).</summary>
public class LayerResolverTests
{
    private static TemplateLayer Layer(LayerKind kind, string id, LayerRef? parent, IReadOnlyList<LayerOverride> overrides, LayerSchema? schema = null) => new()
    {
        Ref = new LayerRef("o", id, 1),
        Kind = kind,
        Parent = parent,
        Schema = schema,
        Overrides = overrides,
        ContentHash = id,
        CreatedAtUtc = DateTimeOffset.UnixEpoch,
        CreatedBy = "t",
    };

    private static TemplateLayer Base(IReadOnlyList<LayerOverride>? overrides = null)
    {
        var content = SnapshotFixtures.PedidoComItens();
        return Layer(LayerKind.Base, "b", null, overrides ?? LayerOverrides.FromMappingTemplate(content.Mapping, "PedidoComItens"),
            new LayerSchema(content.SchemaFiles, content.SchemaEntryPoint));
    }

    [Fact]
    public void Collection_base_round_trips_through_paths()
    {
        var resolved = new LayerResolver().Resolve([Base()]);

        Assert.Empty(resolved.Conflicts);
        Assert.Equal("Itens", resolved.Entries["/PedidoComItens/itens/item"].Collection!.SourceCollectionPath);
        Assert.Single(resolved.Content.Mapping.Complex.Single().Content.Collections);
    }

    [Fact]
    public void Entry_that_does_not_fit_the_node_kind_is_a_critical_conflict()
    {
        var client = Layer(LayerKind.Client, "c", new LayerRef("o", "b", 1),
        [
            new LayerOverride("/PedidoComItens/itens/item", OverrideOperation.Set, MappingEntry.Scalar(new LiteralNode("x"))),
            new LayerOverride("/PedidoComItens/itens", OverrideOperation.Set, MappingEntry.Scalar(new LiteralNode("x"))),
            new LayerOverride("/PedidoComItens/numero", OverrideOperation.Set, MappingEntry.ForEach(new CollectionEntry("Itens"))),
        ]);

        var resolved = new LayerResolver().Resolve([Base(), client]);

        Assert.Equal(3, resolved.Conflicts.Count(c => c.Code == LayerResolver.EntryKindMismatch && c.Severity == DiagnosticSeverity.Critical));
        Assert.True(resolved.HasBlockingConflicts);
    }

    [Fact]
    public void Removing_something_absent_and_orphan_rules_are_warnings_not_blockers()
    {
        var client = Layer(LayerKind.Client, "c", new LayerRef("o", "b", 1),
        [
            new LayerOverride("/PedidoComItens/itens/item", OverrideOperation.Remove),
            new LayerOverride("/PedidoComItens/itens/item", OverrideOperation.Remove),
        ]);

        var resolved = new LayerResolver().Resolve([Base(), client]);

        Assert.False(resolved.HasBlockingConflicts);
        Assert.Contains(resolved.Conflicts, c => c.Code == LayerResolver.RemoveOfMissing);
        Assert.Contains(resolved.Conflicts, c => c.Code == LayerResolver.EntryInactive && c.Path == "/PedidoComItens/itens/item/descricao");
    }

    [Fact]
    public void Malformed_chains_are_rejected()
    {
        var resolver = new LayerResolver();
        var baseLayer = Base();

        Assert.Throws<InvalidLayerChainException>(() => resolver.Resolve([]));
        Assert.Throws<InvalidLayerChainException>(() => resolver.Resolve([Layer(LayerKind.Client, "c", new LayerRef("o", "b", 1), []), baseLayer]));
        Assert.Throws<InvalidLayerChainException>(() => resolver.Resolve([baseLayer, Layer(LayerKind.Client, "c", new LayerRef("o", "outra", 1), [])]));
        Assert.Throws<InvalidLayerChainException>(() => resolver.Resolve([baseLayer,
            Layer(LayerKind.Client, "c", new LayerRef("o", "b", 1), []), Layer(LayerKind.Product, "p", new LayerRef("o", "c", 1), [])]));
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Tradux.Api.IntegrationTests.Infrastructure;
using Tradux.Application;
using Tradux.Application.Layers;
using Tradux.Application.Snapshots;
using Tradux.Contracts;
using Tradux.Domain.Layers;
using Tradux.Domain.Mapping;
using Tradux.Infrastructure.MongoDb;
using Tradux.Tests.Shared;

namespace Tradux.Api.IntegrationTests;

/// <summary>
/// Gate da Fase 7 contra a API real e MongoDB real: "uma atualização
/// incompatível não altera a versão ativa; toda decisão de resolução pode
/// ser explicada". A administração (escrita) usa o <see cref="LayeredTemplateService"/>
/// direto sobre os adaptadores MongoDB, com cliente próprio — como outra
/// instância; a API em teste é lida por HTTP.
/// </summary>
[Collection(MongoCollection.Name)]
public class Phase7GateTests(MongoFixture mongo)
{
    private const string Vendor = "fornecedor";
    private const string TemplateId = "dps";

    private static readonly TemplateKey Key = new(SnapshotFixtures.Organization, TemplateId, SnapshotFixtures.Operation);

    private static LayeredTemplateService Admin(ApiFactory factory, MongoFixture mongo)
    {
        var client = mongo.CreateClient();
        var options = factory.MongoOptions;
        return new LayeredTemplateService(
            new MongoTemplateLayerRepository(client, options),
            new MongoTemplateBindingRepository(client, options),
            new MongoTemplateUpdateRepository(client, options),
            new MongoTemplateSnapshotRepository(client, options),
            new MongoAuditSink(client, options),
            new SnapshotCompiler());
    }

    private static async Task<(LayerRef Base, LayerRef Product, LayerRef Client)> PublishChainAsync(LayeredTemplateService admin)
    {
        var content = SnapshotFixtures.SimpleDps();
        var baseRef = (await admin.CreateLayerVersionAsync(new NewLayerVersion
        {
            OrganizationId = Vendor, LayerId = "base-dps", Kind = LayerKind.Base, Shared = true,
            Schema = new LayerSchema(content.SchemaFiles, content.SchemaEntryPoint),
            Overrides = LayerOverrides.FromMappingTemplate(content.Mapping, "DPS"),
            LookupOverrides = LayerOverrides.FromLookupTables(content.LookupTables),
        }, "fornecedor-admin")).Layer.Ref;
        var productRef = (await admin.CreateLayerVersionAsync(Product(baseRef, "P-"), "fornecedor-admin")).Layer.Ref;
        var clientRef = (await admin.CreateLayerVersionAsync(new NewLayerVersion
        {
            OrganizationId = SnapshotFixtures.Organization, LayerId = "cliente-dps", Kind = LayerKind.Client, Parent = productRef,
            Overrides = [new LayerOverride("/DPS/numero", OverrideOperation.Set,
                MappingEntry.Scalar(OperatorNode.Of("concat", new LiteralNode("C-"), new FieldReferenceNode("Numero"))))],
        }, "cliente-admin")).Layer.Ref;

        var published = await admin.BindAndPublishAsync(Key, clientRef,
            UpdatePolicy.Automatic, SnapshotFixtures.DpsScenarios(), "cliente-admin");
        Assert.True(published.Success, string.Join("; ", published.Diagnostics.Select(d => d.Message)));
        return (baseRef, productRef, clientRef);
    }

    private static NewLayerVersion Product(LayerRef parent, string prefix, params LayerOverride[] extra) => new()
    {
        OrganizationId = Vendor, LayerId = "produto-dps", Kind = LayerKind.Product, Parent = parent, Shared = true,
        Overrides =
        [
            new LayerOverride("/DPS/serie", OverrideOperation.Set,
                MappingEntry.Scalar(OperatorNode.Of("concat", new LiteralNode(prefix), new FieldReferenceNode("Serie")))),
            .. extra,
        ],
    };

    private static async Task<GenerationResult> GenerateAsync(HttpClient client) =>
        await (await client.PostGenerateAsync(SnapshotFixtures.Organization, TemplateId, SnapshotFixtures.Operation, SnapshotFixtures.DpsJson())).ReadResultAsync();

    [Fact]
    public async Task Incompatible_update_does_not_change_the_active_version_served_by_the_api()
    {
        await using var factory = new ApiFactory(mongo);
        var admin = Admin(factory, mongo);
        var (baseRef, _, _) = await PublishChainAsync(admin);
        var client = factory.CreateClient();
        var before = await GenerateAsync(client);
        Assert.True(before.Success);

        var released = await admin.CreateLayerVersionAsync(
            Product(baseRef, "P-", new LayerOverride("/DPS/valorServico", OverrideOperation.Set, MappingEntry.Scalar(new LiteralNode("abc")))),
            "fornecedor-admin");

        Assert.Empty(released.Updates); // o fornecedor não recebe atualizações de outras organizações
        var update = Assert.Single(await admin.ListUpdatesAsync(Key));
        Assert.Equal(UpdateStatus.Failed, update.Status);
        var after = await GenerateAsync(client);
        Assert.Equal("1", after.VersionUsed);
        Assert.True(SnapshotFixtures.XmlSemanticallyEqual(before.Xml, after.Xml));

        // Histórico da tentativa, pela API (leitura).
        var updates = await client.GetFromJsonAsync<JsonElement>(
            $"/v1/organizations/{SnapshotFixtures.Organization}/templates/{TemplateId}/operations/{SnapshotFixtures.Operation}/updates");
        Assert.Equal("Failed", updates[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task Explanation_endpoint_explains_every_published_rule()
    {
        await using var factory = new ApiFactory(mongo);
        var admin = Admin(factory, mongo);
        var (baseRef, productRef, clientRef) = await PublishChainAsync(admin);
        var client = factory.CreateClient();

        var response = await client.GetAsync($"/v1/organizations/{SnapshotFixtures.Organization}/templates/{TemplateId}/operations/{SnapshotFixtures.Operation}/versions/1/explanation");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var explanation = (await response.Content.ReadFromJsonAsync<ResolutionExplanation>(JsonSerializerOptions.Web))!;

        Assert.Equal([baseRef, productRef, clientRef], explanation.Manifest!.Layers.Select(l => l.Ref));
        var active = (await new MongoTemplateSnapshotRepository(mongo.CreateClient(), factory.MongoOptions)
            .GetActiveAsync(Key))!;
        Assert.All(MappingPaths.Flatten(active.Content.Mapping, "DPS").Keys,
            path => Assert.NotNull(explanation.Manifest.Paths[path].FinalSource));
        Assert.Equal(clientRef, explanation.Manifest.Paths["/DPS/numero"].FinalSource);

        var missing = await client.GetAsync($"/v1/organizations/{SnapshotFixtures.Organization}/templates/{TemplateId}/operations/{SnapshotFixtures.Operation}/versions/9/explanation");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Compatible_update_and_rollback_reach_the_warm_api_cache()
    {
        await using var factory = new ApiFactory(mongo);
        var admin = Admin(factory, mongo);
        var (baseRef, _, _) = await PublishChainAsync(admin);
        var client = factory.CreateClient();
        Assert.Equal("1", (await GenerateAsync(client)).VersionUsed);

        var released = await admin.CreateLayerVersionAsync(Product(baseRef, "Q-"), "fornecedor-admin");
        Assert.Equal(UpdateStatus.Applied, Assert.Single(await admin.ListUpdatesAsync(Key), u => u.Trigger == released.Layer.Ref).Status);
        await WaitForVersionAsync(client, "2");

        var rollback = await admin.RollbackAsync(Key, null, "cliente-admin");
        Assert.True(rollback.Success);
        await WaitForVersionAsync(client, "1");
        Assert.Contains("<serie>P-A1</serie>", (await GenerateAsync(client)).Xml);
    }

    [Fact]
    public async Task Layers_bindings_updates_and_audit_round_trip_through_mongodb()
    {
        await using var factory = new ApiFactory(mongo);
        var admin = Admin(factory, mongo);
        var (baseRef, _, clientRef) = await PublishChainAsync(admin);
        var mongoClient = mongo.CreateClient();

        var layers = new MongoTemplateLayerRepository(mongoClient, factory.MongoOptions);
        var stored = (await layers.GetAsync(clientRef))!;
        Assert.Equal(clientRef, stored.Ref);
        var numero = Assert.IsType<OperatorNode>(stored.Overrides.Single().Entry!.Value);
        Assert.Equal("concat", numero.OperatorName);
        Assert.Equal(SnapshotFixtures.SimpleDps().SchemaFiles["exemplo-simples.xsd"], (await layers.GetAsync(baseRef))!.Schema!.Files["exemplo-simples.xsd"]);

        var bindings = new MongoTemplateBindingRepository(mongoClient, factory.MongoOptions);
        var byLineage = await bindings.FindByLineageAsync(new LayerIdentity(Vendor, "produto-dps"));
        var binding = Assert.Single(byLineage);
        Assert.Equal("DPS realista", Assert.Single(binding.Scenarios).Name);

        var audit = await new MongoAuditSink(mongoClient, factory.MongoOptions).ListAsync(SnapshotFixtures.Organization);
        Assert.Contains(audit, e => e.Action == "template.publish" && e.Outcome == "success");
    }

    private static async Task WaitForVersionAsync(HttpClient client, string version)
    {
        var served = await Eventually.PollAsync(() => GenerateAsync(client), r => r.VersionUsed == version);
        Assert.True(served.VersionUsed == version, $"A API não passou a servir a versão {version} (Change Stream).");
    }
}

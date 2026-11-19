using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Tradux.Api.IntegrationTests.Infrastructure;
using Tradux.Application.Layers;
using Tradux.Application.Packaging;
using Tradux.Tests.Shared;

namespace Tradux.Api.IntegrationTests;

/// <summary>
/// Gate da Fase 10 (ADR-0013), parte do pacote: "um pacote passa entre
/// ambientes com hashes preservados" — dois ambientes = duas APIs com
/// bancos MongoDB distintos.
/// </summary>
[Collection(MongoCollection.Name)]
public class Phase10GateTests(MongoFixture mongo)
{
    private const string Dps = "dps";
    private static readonly JsonSerializerOptions Web = JsonSerializerOptions.Web;

    private static async Task PublishLayeredOverHttpAsync(HttpClient admin)
    {
        var org = $"/v1/organizations/{SnapshotFixtures.Organization}";
        var content = SnapshotFixtures.SimpleDps();
        var baseLayer = await admin.PostAsJsonAsync($"{org}/layers", new
        {
            layerId = "base-dps",
            kind = "Base",
            schema = new { files = content.SchemaFiles.Select(f => new { name = f.Key, contentBase64 = Convert.ToBase64String(f.Value) }), entryPoint = content.SchemaEntryPoint },
            overrides = LayerOverrides.FromMappingTemplate(content.Mapping, "DPS"),
            lookupOverrides = LayerOverrides.FromLookupTables(content.LookupTables),
        }, Web);
        var baseRef = (await baseLayer.Content.ReadFromJsonAsync<JsonElement>(Web)).GetProperty("ref");
        var client = await admin.PostAsJsonAsync($"{org}/layers", new { layerId = "cliente", kind = "Client", parent = baseRef }, Web);
        var clientRef = (await client.Content.ReadFromJsonAsync<JsonElement>(Web)).GetProperty("ref");
        var bound = await admin.PutAsJsonAsync($"{org}/templates/{Dps}/operations/{SnapshotFixtures.Operation}/binding",
            new { top = clientRef, policy = "Manual", scenarios = SnapshotFixtures.DpsScenarios() }, Web);
        Assert.Equal(HttpStatusCode.OK, bound.StatusCode);
    }

    private static IReadOnlyList<PackageEntry> EntriesOf(byte[] package)
    {
        using var zip = new ZipArchive(new MemoryStream(package));
        using var stream = zip.GetEntry(PackagePaths.Manifest)!.Open();
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return CanonicalJson.Deserialize<PackageManifest>(copy.ToArray()).Entries;
    }

    [Fact]
    public async Task Package_moves_between_environments_over_http_with_hashes_preserved()
    {
        await using var environmentA = new ApiFactory(mongo);
        await using var environmentB = new ApiFactory(mongo);
        var adminA = environmentA.CreateClient();
        var adminB = environmentB.CreateClient();
        var template = $"/v1/organizations/{SnapshotFixtures.Organization}/templates/{Dps}/operations/{SnapshotFixtures.Operation}";
        await PublishLayeredOverHttpAsync(adminA);

        var export = await adminA.GetAsync($"{template}/package");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.Equal("application/zip", export.Content.Headers.ContentType!.MediaType);
        var packageA = await export.Content.ReadAsByteArrayAsync();

        var body = new ByteArrayContent(packageA);
        body.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        var import = await adminB.PostAsync($"/v1/organizations/{SnapshotFixtures.Organization}/packages?templateId={Dps}&operation={SnapshotFixtures.Operation}", body);
        Assert.Equal(HttpStatusCode.OK, import.StatusCode);
        var result = (await import.Content.ReadFromJsonAsync<JsonElement>(Web));
        Assert.True(result.GetProperty("hashPreserved").GetBoolean());

        var packageB = await (await adminB.GetAsync($"{template}/package")).Content.ReadAsByteArrayAsync();
        Assert.Equal(EntriesOf(packageA), EntriesOf(packageB));

        // Mesmo XML servido pelas duas APIs.
        var request = new { data = SnapshotFixtures.DpsJson() };
        var xmlA = (await (await adminA.PostAsJsonAsync($"{template}/xml", request, Web)).Content.ReadFromJsonAsync<JsonElement>(Web)).GetProperty("xml").GetString();
        var xmlB = (await (await adminB.PostAsJsonAsync($"{template}/xml", request, Web)).Content.ReadFromJsonAsync<JsonElement>(Web)).GetProperty("xml").GetString();
        Assert.Equal(xmlA, xmlB);

        // Auditoria nos dois lados, sem conteúdo.
        var auditB = await (await adminB.GetAsync($"/v1/organizations/{SnapshotFixtures.Organization}/audit")).Content.ReadAsStringAsync();
        Assert.Contains("template.import", auditB);
        Assert.DoesNotContain("12345678000199", auditB);
    }

    [Fact]
    public async Task Knowledge_search_returns_the_right_sources_with_version_and_respects_access()
    {
        await using var factory = new ApiFactory(mongo);
        var editor = factory.CreateClient(ApiFactory.EditorA);

        var hits = await editor.GetFromJsonAsync<List<JsonElement>>("/v1/knowledge/search?q=" + Uri.EscapeDataString("como fazer rollback para a versão anterior"), Web);

        Assert.NotEmpty(hits!);
        Assert.Contains(hits!, h => h.GetProperty("sourceId").GetString() == "runbook:ROLLBACK_E_ATUALIZACOES");
        Assert.All(hits!, h =>
        {
            Assert.False(string.IsNullOrEmpty(h.GetProperty("version").GetString()));
            Assert.False(string.IsNullOrEmpty(h.GetProperty("anchor").GetString()));
        });

        // Modelo de ameaças é restrito: editor não vê; admin (audit.read) vê.
        var question = "/v1/knowledge/search?limit=20&q=" + Uri.EscapeDataString("modelo de ameaças STRIDE");
        Assert.DoesNotContain(await editor.GetFromJsonAsync<List<JsonElement>>(question, Web) ?? [], h => h.GetProperty("sourceId").GetString() == "threat-model");
        Assert.Contains(await factory.CreateClient().GetFromJsonAsync<List<JsonElement>>(question, Web) ?? [], h => h.GetProperty("sourceId").GetString() == "threat-model");

        Assert.Equal(HttpStatusCode.Forbidden, (await factory.CreateClient(ApiFactory.RuntimeA).GetAsync("/v1/knowledge/search?q=rollback")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await editor.GetAsync("/v1/knowledge/search")).StatusCode);
    }

    [Fact]
    public async Task Package_permissions_and_invalid_packages_are_enforced()
    {
        await using var factory = new ApiFactory(mongo);
        await factory.CreateDirectPublisher().PublishAsync(SnapshotFixtures.Organization, Dps, SnapshotFixtures.Operation, SnapshotFixtures.SimpleDps(), SnapshotFixtures.DpsScenarios(), "teste");
        var template = $"/v1/organizations/{SnapshotFixtures.Organization}/templates/{Dps}/operations/{SnapshotFixtures.Operation}";

        Assert.Equal(HttpStatusCode.Forbidden, (await factory.CreateClient(ApiFactory.RuntimeA).GetAsync($"{template}/package")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await factory.CreateClient(ApiFactory.EditorA).GetAsync($"{template}/package")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await factory.CreateClient(ApiFactory.AdminB).GetAsync($"{template}/package")).StatusCode);

        var importRoute = $"/v1/organizations/{SnapshotFixtures.Organization}/packages?templateId=x&operation=y";
        Assert.Equal(HttpStatusCode.Forbidden, (await factory.CreateClient(ApiFactory.EditorA).PostAsync(importRoute, new ByteArrayContent([1]))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await factory.CreateClient(ApiFactory.AuditorA).PostAsync(importRoute, new ByteArrayContent([1]))).StatusCode);

        var garbage = new ByteArrayContent("isto não é zip"u8.ToArray());
        var invalid = await factory.CreateClient().PostAsync($"/v1/organizations/{SnapshotFixtures.Organization}/packages?templateId=x&operation=y", garbage);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Tradux.Api.IntegrationTests.Infrastructure;
using Tradux.Tests.Shared;

namespace Tradux.Api.IntegrationTests;

/// <summary>
/// Gate da Fase 14 (ADR-0017): nova versão do XSD de destino sobre um DE-PARA
/// publicado → o que mudou e o que remapear, pela API (rascunho e template ativo).
/// </summary>
[Collection(MongoCollection.Name)]
public sealed class Phase14GateTests(MongoFixture mongo)
{
    private const string TemplateId = "dps-xsd-v1";
    private static readonly JsonSerializerOptions Web = JsonSerializerOptions.Web;

    private static string Template(string organization = SnapshotFixtures.Organization) =>
        $"/v1/organizations/{organization}/templates/{TemplateId}/operations/{SnapshotFixtures.Operation}";

    private static object Upload(IReadOnlyDictionary<string, byte[]> files, string entryPoint) =>
        new { files = files.Select(f => new { name = f.Key, contentBase64 = Convert.ToBase64String(f.Value) }), entryPoint };

    private static object Upload(string xsd) => Upload(new Dictionary<string, byte[]> { ["exemplo-simples.xsd"] = Encoding.UTF8.GetBytes(xsd) }, "exemplo-simples.xsd");

    // "Prefeitura" publica a v2: cnpjPrestador renomeado; elemento novo obrigatório.
    private static string V2() => File.ReadAllText(SnapshotFixtures.SchemaPath("exemplo-simples.xsd"))
        .Replace("name=\"cnpjPrestador\"", "name=\"cnpjDoPrestador\"", StringComparison.Ordinal)
        .Replace("<xs:element name=\"numero\"", "<xs:element name=\"codigoMunicipio\" type=\"xs:string\"/>\n      <xs:element name=\"numero\"", StringComparison.Ordinal);

    private static async Task PublishV1Async(HttpClient admin)
    {
        var content = SnapshotFixtures.ErpToDps();
        var published = await admin.PostAsJsonAsync($"{Template()}/publications", new
        {
            schema = Upload(content.SchemaFiles, content.SchemaEntryPoint),
            sourceSchema = Upload(content.SourceSchema!.Files, content.SourceSchema.EntryPoint),
            mapping = content.Mapping,
            scenarios = SnapshotFixtures.ErpScenarios(),
        }, Web);
        Assert.True(published.StatusCode == HttpStatusCode.OK, await published.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task New_destination_xsd_over_a_published_template_shows_what_to_remap()
    {
        await using var factory = new ApiFactory(mongo);
        await PublishV1Async(factory.CreateClient());

        var response = await factory.CreateClient(ApiFactory.AuditorA).PostAsJsonAsync($"{Template()}/schema-changes", new { after = Upload(V2()) }, Web);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var report = await response.Content.ReadFromJsonAsync<JsonElement>(Web);
        var changes = report.GetProperty("changes").EnumerateArray().ToList();

        var removed = changes.Single(c => c.GetProperty("path").GetString() == "/DPS/cnpjPrestador");
        Assert.Equal("Remap", removed.GetProperty("impact").GetString());
        Assert.Equal("/DPS/cnpjDoPrestador", removed.GetProperty("suggestions")[0].GetString());
        Assert.Contains(changes, c => c.GetProperty("path").GetString() == "/DPS/codigoMunicipio" && c.GetProperty("impact").GetString() == "Map");
        Assert.Equal(1, report.GetProperty("summary").GetProperty("remap").GetInt32());

        // Mesma resposta pelo endpoint do editor (rascunho com o mesmo mapeamento).
        var content = SnapshotFixtures.ErpToDps();
        var draft = await factory.CreateClient(ApiFactory.EditorA).PostAsJsonAsync("/v1/design/schema-changes", new
        {
            before = Upload(content.SchemaFiles, content.SchemaEntryPoint),
            after = Upload(V2()),
            mapping = content.Mapping,
        }, Web);
        var draftReport = await draft.Content.ReadFromJsonAsync<JsonElement>(Web);
        Assert.Equal(report.GetProperty("changes").ToString(), draftReport.GetProperty("changes").ToString());
    }

    [Fact]
    public async Task Permissions_isolation_and_invalid_input_are_enforced()
    {
        await using var factory = new ApiFactory(mongo);
        await PublishV1Async(factory.CreateClient());
        var body = new { after = Upload(V2()) };

        // Credencial de runtime (só template.use) não lê o template nem desenha.
        Assert.Equal(HttpStatusCode.Forbidden, (await factory.CreateClient(ApiFactory.RuntimeA).PostAsJsonAsync($"{Template()}/schema-changes", body, Web)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await factory.CreateClient(ApiFactory.RuntimeA).PostAsJsonAsync("/v1/design/schema-changes",
            new { before = Upload(V2()), after = Upload(V2()) }, Web)).StatusCode);
        // Outra organização: 404 (pela rota e pelo template dela, que não existe).
        Assert.Equal(HttpStatusCode.NotFound, (await factory.CreateClient(ApiFactory.AdminB).PostAsJsonAsync($"{Template()}/schema-changes", body, Web)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await factory.CreateClient(ApiFactory.AdminB).PostAsJsonAsync($"{Template("org-b")}/schema-changes", body, Web)).StatusCode);

        var invalid = await factory.CreateClient().PostAsJsonAsync($"{Template()}/schema-changes", new { after = Upload("<xs:schema") }, Web);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Contains("XSD novo", await invalid.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, (await factory.CreateClient().PostAsJsonAsync($"{Template()}/schema-changes", new { }, Web)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await factory.CreateClient(ApiFactory.EditorA).PostAsJsonAsync("/v1/design/schema-changes", new { after = Upload(V2()) }, Web)).StatusCode);
    }
}

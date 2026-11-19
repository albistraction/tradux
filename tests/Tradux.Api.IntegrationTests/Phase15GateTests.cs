using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Tradux.Api.IntegrationTests.Infrastructure;
using Tradux.Tests.Shared;

namespace Tradux.Api.IntegrationTests;

/// <summary>
/// Gate da Fase 15 (ADR-0018) pela API: layout com xs:choice repetível publica
/// pelos gates e gera.
/// </summary>
[Collection(MongoCollection.Name)]
public sealed class Phase15GateTests(MongoFixture mongo)
{
    private const string TemplateId = "lote-rps";
    private static readonly JsonSerializerOptions Web = JsonSerializerOptions.Web;

    private static string Template(string organization = SnapshotFixtures.Organization) =>
        $"/v1/organizations/{organization}/templates/{TemplateId}/operations/{SnapshotFixtures.Operation}";

    private static async Task PublishAsync(HttpClient admin)
    {
        var content = SnapshotFixtures.LoteRpsChoice();
        var published = await admin.PostAsJsonAsync($"{Template()}/publications", new
        {
            schema = new { files = content.SchemaFiles.Select(f => new { name = f.Key, contentBase64 = Convert.ToBase64String(f.Value) }), entryPoint = content.SchemaEntryPoint },
            mapping = content.Mapping,
            scenarios = SnapshotFixtures.LoteRpsScenarios(),
        }, Web);
        Assert.True(published.StatusCode == HttpStatusCode.OK, await published.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Repeated_choice_template_publishes_and_generates()
    {
        await using var factory = new ApiFactory(mongo);
        await PublishAsync(factory.CreateClient());

        var generated = await factory.CreateClient(ApiFactory.RuntimeA).PostAsJsonAsync($"{Template()}/xml", new { data = SnapshotFixtures.LoteRpsJson() }, Web);
        var body = await generated.Content.ReadAsStringAsync();
        Assert.True(generated.StatusCode == HttpStatusCode.OK, body);
        var rps = System.Xml.Linq.XDocument.Parse(JsonDocument.Parse(body).RootElement.GetProperty("xml").GetString()!).Root!.Element("Rps")!;
        Assert.Equal(["Identificacao", "Valores", "Tomador"], rps.Elements().Select(e => e.Name.LocalName));
        Assert.Equal("L-77", rps.Element("Identificacao")!.Element("Lote")!.Value);
    }
}

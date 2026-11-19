using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Tradux.Api.IntegrationTests.Infrastructure;
using Tradux.Application.Generation;
using Tradux.Tests.Shared;

namespace Tradux.Api.IntegrationTests;

/// <summary>
/// Gate da Fase 12 (ADR-0015): o autor escolhe o XSD de origem, publica o
/// template com cenário em XML e o integrador envia o XML de origem e recebe
/// o XML de destino — por HTTP, com MongoDB real.
/// </summary>
[Collection(MongoCollection.Name)]
public sealed class Phase12GateTests(MongoFixture mongo)
{
    private const string TemplateId = "dps-erp";
    private static readonly JsonSerializerOptions Web = JsonSerializerOptions.Web;

    private static string Template(string organization = SnapshotFixtures.Organization) =>
        $"/v1/organizations/{organization}/templates/{TemplateId}/operations/{SnapshotFixtures.Operation}";

    private static object Upload(IReadOnlyDictionary<string, byte[]> files, string entryPoint) =>
        new { files = files.Select(f => new { name = f.Key, contentBase64 = Convert.ToBase64String(f.Value) }), entryPoint };

    private static async Task PublishOverHttpAsync(HttpClient admin)
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

    private static Task<HttpResponseMessage> TransformAsync(HttpClient client, string xml, string query = "", string? organization = null, Encoding? encoding = null) =>
        client.PostAsync($"{Template(organization ?? SnapshotFixtures.Organization)}/transform{query}",
            new ByteArrayContent((encoding ?? Encoding.UTF8).GetBytes(xml)) { Headers = { ContentType = new("application/xml") } });

    [Fact]
    public async Task Author_publishes_with_source_xsd_and_integrator_transforms_xml_into_the_destination_xml()
    {
        await using var factory = new ApiFactory(mongo);
        await PublishOverHttpAsync(factory.CreateClient());
        var runtime = factory.CreateClient(ApiFactory.RuntimeA);

        using var response = await TransformAsync(runtime, SnapshotFixtures.ErpSourceXml(), "?correlationId=pedido-42");
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(Web);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(result.GetProperty("success").GetBoolean(), result.ToString());
        var xml = result.GetProperty("xml").GetString()!;
        Assert.Contains(">000123<", xml, StringComparison.Ordinal);
        Assert.Contains(">1500.50<", xml, StringComparison.Ordinal);
        Assert.Equal("pedido-42", result.GetProperty("correlationId").GetString());

        // Instância nova (cache frio) lê o XSD de origem persistido no MongoDB.
        await using var restarted = new ApiFactory(mongo, sharedDatabase: factory.MongoOptions);
        using var again = await TransformAsync(restarted.CreateClient(ApiFactory.RuntimeA), SnapshotFixtures.ErpSourceXml());
        Assert.Equal(xml, (await again.Content.ReadFromJsonAsync<JsonElement>(Web)).GetProperty("xml").GetString());
    }

    [Fact]
    public async Task Declared_encoding_invalid_xml_permissions_and_limits_are_enforced()
    {
        await using var factory = new ApiFactory(mongo);
        await PublishOverHttpAsync(factory.CreateClient());
        var runtime = factory.CreateClient(ApiFactory.RuntimeA);

        // ISO-8859-1 declarado no XML: acentos preservados.
        var latin = SnapshotFixtures.ErpSourceXml().Replace("UTF-8", "ISO-8859-1").Replace("Consultoria em sistemas", "Manutenção elétrica");
        var latinResult = await (await TransformAsync(runtime, latin, encoding: Encoding.Latin1)).Content.ReadFromJsonAsync<JsonElement>(Web);
        Assert.Contains("Manutenção elétrica", latinResult.GetProperty("xml").GetString(), StringComparison.Ordinal);

        // Fora do XSD de origem: 200 com diagnóstico e posição, sem XML.
        var invalid = await (await TransformAsync(runtime, SnapshotFixtures.ErpSourceXml().Replace("situacao=\"1\"", "situacao=\"9\""))).Content.ReadFromJsonAsync<JsonElement>(Web);
        Assert.False(invalid.GetProperty("success").GetBoolean());
        Assert.Contains(invalid.GetProperty("diagnostics").EnumerateArray(), d => d.GetProperty("code").GetString() == XmlGenerationService.SourceXmlInvalidCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await TransformAsync(runtime, "")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await TransformAsync(runtime, SnapshotFixtures.ErpSourceXml(), "?versions=abc")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await TransformAsync(factory.CreateClient(ApiFactory.EditorA), SnapshotFixtures.ErpSourceXml())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await TransformAsync(factory.CreateClient(ApiFactory.AdminB), SnapshotFixtures.ErpSourceXml())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await TransformAsync(factory.CreateClient(secret: null), SnapshotFixtures.ErpSourceXml())).StatusCode);

        // Limite de corpo pelo endpoint (inclusive grafia alternativa do caminho).
        var huge = SnapshotFixtures.ErpSourceXml().Replace("Consultoria em sistemas", new string('x', 1_100_000));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await TransformAsync(runtime, huge)).StatusCode);
        using var upper = await runtime.PostAsync($"{Template()}/TRANSFORM", new StringContent(huge, Encoding.UTF8, "application/xml"));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, upper.StatusCode);
    }

    private sealed record LayerCreatedDto(Tradux.Domain.Layers.LayerRef Ref, string ContentHash);

    [Fact]
    public async Task Base_layer_with_source_xsd_goes_through_http_and_mongodb_and_transforms()
    {
        // Revisão da Fase 12, aviso 6: camada base com XSD de origem persistida no MongoDB real.
        await using var factory = new ApiFactory(mongo);
        var admin = factory.CreateClient();
        var content = SnapshotFixtures.ErpToDps();
        var created = await admin.PostAsJsonAsync($"/v1/organizations/{SnapshotFixtures.Organization}/layers", new
        {
            layerId = "base-erp",
            kind = "Base",
            schema = Upload(content.SchemaFiles, content.SchemaEntryPoint),
            sourceSchema = Upload(content.SourceSchema!.Files, content.SourceSchema.EntryPoint),
            overrides = LayerOverrides.FromMappingTemplate(content.Mapping, "DPS"),
        }, Web);
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        var layer = (await created.Content.ReadFromJsonAsync<LayerCreatedDto>(Web))!;

        var bound = await admin.PutAsJsonAsync($"{Template()}/binding", new { top = layer.Ref, policy = "Manual", scenarios = SnapshotFixtures.ErpScenarios() }, Web);
        Assert.True(bound.IsSuccessStatusCode, await bound.Content.ReadAsStringAsync());

        // Cache frio numa instância nova: camada e snapshot lidos do MongoDB.
        await using var other = new ApiFactory(mongo, sharedDatabase: factory.MongoOptions);
        var result = await (await TransformAsync(other.CreateClient(ApiFactory.RuntimeA), SnapshotFixtures.ErpSourceXml())).Content.ReadFromJsonAsync<JsonElement>(Web);
        Assert.True(result.GetProperty("success").GetBoolean(), result.ToString());
        var stored = await new Tradux.Infrastructure.MongoDb.MongoTemplateLayerRepository(mongo.CreateClient(), factory.MongoOptions).GetAsync(layer.Ref);
        Assert.Equal("origem-exemplo.xsd", stored!.SourceSchema!.EntryPoint);
        Assert.Equal(layer.ContentHash, stored.ContentHash);
    }

    [Fact]
    public async Task Editor_facades_describe_the_source_xsd_and_test_a_draft_with_xml()
    {
        await using var factory = new ApiFactory(mongo);
        var editor = factory.CreateClient(ApiFactory.EditorA);
        var content = SnapshotFixtures.ErpToDps();
        var sourceUpload = Upload(content.SourceSchema!.Files, content.SourceSchema.EntryPoint);

        var tree = await (await editor.PostAsJsonAsync("/v1/design/source-schema", sourceUpload, Web)).Content.ReadFromJsonAsync<JsonElement>(Web);
        Assert.Equal("NotaErp", tree.GetProperty("rootElementName").GetString());
        Assert.Contains(tree.GetProperty("root").GetProperty("children").EnumerateArray(), f => f.GetProperty("path").GetString() == "@situacao");

        var tested = await (await editor.PostAsJsonAsync("/v1/design/test", new
        {
            schema = Upload(content.SchemaFiles, content.SchemaEntryPoint),
            mapping = content.Mapping,
            sourceSchema = sourceUpload,
            sourceXml = SnapshotFixtures.ErpSourceXml(),
        }, Web)).Content.ReadFromJsonAsync<JsonElement>(Web);
        Assert.True(tested.GetProperty("publishable").GetBoolean(), tested.ToString());

        var badXsd = await editor.PostAsJsonAsync("/v1/design/source-schema", new { files = new[] { new { name = "x.xsd", contentBase64 = Convert.ToBase64String("<nao-xsd"u8.ToArray()) } }, entryPoint = "x.xsd" }, Web);
        Assert.Equal(HttpStatusCode.BadRequest, badXsd.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await factory.CreateClient(ApiFactory.RuntimeA).PostAsJsonAsync("/v1/design/source-schema", sourceUpload, Web)).StatusCode);
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Xml.Linq;
using Tradux.Api.IntegrationTests.Infrastructure;
using Tradux.Application.Generation;
using Tradux.Contracts;
using Tradux.Tests.Shared;

namespace Tradux.Api.IntegrationTests;

/// <summary>Contrato HTTP v1 da API contra MongoDB real: códigos de status, formato JSON, versões e OpenAPI.</summary>
[Collection(MongoCollection.Name)]
public class GenerationApiTests(MongoFixture mongo)
{
    private const string Dps = "dps";
    private static readonly XNamespace DpsNs = "http://exemplo.ilustrativo.tradux/dps/v1";

    private static async Task<(ApiFactory Factory, HttpClient Client)> StartWithPublishedDpsAsync(MongoFixture mongo)
    {
        var factory = new ApiFactory(mongo);
        await factory.CreateDirectPublisher().PublishAsync(SnapshotFixtures.Organization, Dps, SnapshotFixtures.Operation, SnapshotFixtures.SimpleDps(), SnapshotFixtures.ScenariosFor(SnapshotFixtures.SimpleDps()), "teste");
        return (factory, factory.CreateClient());
    }

    [Fact]
    public async Task Generate_returns_200_with_valid_xml_and_the_documented_json_shape()
    {
        var (factory, client) = await StartWithPublishedDpsAsync(mongo);
        await using var factoryScope = factory;

        var response = await client.PostGenerateAsync(SnapshotFixtures.Organization, Dps, SnapshotFixtures.Operation, SnapshotFixtures.DpsJson());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal("1", root.GetProperty("versionUsed").GetString());
        Assert.Equal(JsonValueKind.Number, root.GetProperty("durationMs").ValueKind);
        Assert.Equal(JsonValueKind.Number, root.GetProperty("versionsAttempted")[0].GetProperty("durationMs").ValueKind);
        Assert.False(root.TryGetProperty("warnings", out _));

        var xml = XDocument.Parse(root.GetProperty("xml").GetString()!);
        Assert.Equal("000123", xml.Root!.Element(DpsNs + "numero")!.Value);
    }

    [Fact]
    public async Task Generation_failure_is_200_with_critical_diagnostics_serialized_as_text()
    {
        var (factory, client) = await StartWithPublishedDpsAsync(mongo);
        await using var factoryScope = factory;

        var response = await client.PostGenerateAsync(
            SnapshotFixtures.Organization, Dps, SnapshotFixtures.Operation, SnapshotFixtures.DpsJson(cnpjPrestador: "123"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"severity\":\"Critical\"", body);
        var result = await response.ReadResultAsync();
        Assert.False(result.Success);
        Assert.Null(result.Xml);
        Assert.Contains(result.Diagnostics, d => d.Code == "XSD_VALIDATION");
    }

    [Theory]
    [InlineData("\"abc\"")]
    [InlineData("\"1500,50\"")]
    [InlineData("{ \"a\": 1 }")]
    public async Task Value_that_does_not_convert_is_200_with_a_critical_diagnostic_never_500(string valorServicoJson)
    {
        // Revisão da Fase 5 (B7): antes era exceção não tratada (HTTP 500);
        // "1500,50" também não pode virar 150050 em silêncio.
        var (factory, client) = await StartWithPublishedDpsAsync(mongo);
        await using var factoryScope = factory;
        var json = SnapshotFixtures.DpsJson().GetRawText().Replace("\"ValorServico\": 1500.50", $"\"ValorServico\": {valorServicoJson}");
        Assert.Contains(valorServicoJson, json);

        var response = await client.PostGenerateAsync(SnapshotFixtures.Organization, Dps, SnapshotFixtures.Operation, SnapshotFixtures.Json(json));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.ReadResultAsync();
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Code == "VALUE_CONVERSION_FAILED" && d.DestinationXPath == "/DPS/valorServico");
    }

    [Theory]
    [InlineData("{ \"data\": ")]
    [InlineData("{ \"data\": {}, \"version\": \"a\" }")]
    public async Task Malformed_body_is_400_even_in_development(string body)
    {
        var (factory, client) = await StartWithPublishedDpsAsync(mongo);
        await using var factoryScope = factory;

        var response = await client.PostAsync(
            ApiTestHelpers.XmlRoute(SnapshotFixtures.Organization, Dps, SnapshotFixtures.Operation),
            new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_template_or_other_organization_is_404_with_the_report()
    {
        var (factory, client) = await StartWithPublishedDpsAsync(mongo);
        await using var factoryScope = factory;

        var otherOrg = await client.PostGenerateAsync(SnapshotFixtures.OtherOrganization, Dps, SnapshotFixtures.Operation, SnapshotFixtures.DpsJson());
        var unknownVersion = await client.PostGenerateAsync(SnapshotFixtures.Organization, Dps, SnapshotFixtures.Operation, SnapshotFixtures.DpsJson(), version: 42);

        // Fase 9: credencial da org-a pedindo a org-b → 404 sem corpo (não revela nada).
        Assert.Equal(HttpStatusCode.NotFound, otherOrg.StatusCode);
        Assert.Empty(await otherOrg.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, unknownVersion.StatusCode);
        Assert.Equal(XmlGenerationService.SnapshotNotFoundCode, Assert.Single((await unknownVersion.ReadResultAsync()).Diagnostics).Code);
    }

    [Fact]
    public async Task Data_that_is_not_a_json_object_is_400_problem_details()
    {
        var (factory, client) = await StartWithPublishedDpsAsync(mongo);
        await using var factoryScope = factory;

        var arrayData = await client.PostGenerateAsync(SnapshotFixtures.Organization, Dps, SnapshotFixtures.Operation, SnapshotFixtures.Json("[1]"));
        var missingData = await client.PostAsJsonAsync(ApiTestHelpers.XmlRoute(SnapshotFixtures.Organization, Dps, SnapshotFixtures.Operation), new { version = 1 });

        Assert.Equal(HttpStatusCode.BadRequest, arrayData.StatusCode);
        Assert.Equal("application/problem+json", arrayData.Content.Headers.ContentType!.MediaType);
        Assert.Equal(HttpStatusCode.BadRequest, missingData.StatusCode);
    }

    [Fact]
    public async Task Versions_endpoint_lists_newest_first_and_pinned_generation_uses_the_old_version()
    {
        var (factory, client) = await StartWithPublishedDpsAsync(mongo);
        await using var factoryScope = factory;
        await factory.CreateDirectPublisher().PublishAsync(SnapshotFixtures.Organization, Dps, SnapshotFixtures.Operation, SnapshotFixtures.SimpleDps("V2-"), SnapshotFixtures.ScenariosFor(SnapshotFixtures.SimpleDps("V2-")), "teste");

        var versions = await client.GetFromJsonAsync<List<TemplateVersionInfo>>(
            ApiTestHelpers.VersionsRoute(SnapshotFixtures.Organization, Dps, SnapshotFixtures.Operation), JsonSerializerOptions.Web);
        var pinned = await (await client.PostGenerateAsync(
            SnapshotFixtures.Organization, Dps, SnapshotFixtures.Operation, SnapshotFixtures.DpsJson(), version: 1)).ReadResultAsync();

        Assert.Equal([2, 1], Assert.IsType<List<TemplateVersionInfo>>(versions).Select(v => v.Version));
        Assert.Equal(["Active", "Superseded"], versions!.Select(v => v.Status));
        Assert.Equal("1", pinned.VersionUsed);
        Assert.Equal("000123", XDocument.Parse(pinned.Xml!).Root!.Element(DpsNs + "numero")!.Value);
    }

    [Fact]
    public async Task OpenApi_document_describes_both_endpoints()
    {
        await using var factory = new ApiFactory(mongo);
        var client = factory.CreateClient();

        using var openApi = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var paths = openApi.RootElement.GetProperty("paths");

        Assert.True(paths.TryGetProperty("/v1/organizations/{organizationId}/templates/{templateId}/operations/{operation}/xml", out var xml));
        Assert.True(xml.TryGetProperty("post", out _));
        Assert.True(paths.TryGetProperty("/v1/organizations/{organizationId}/templates/{templateId}/operations/{operation}/versions", out var versions));
        Assert.True(versions.TryGetProperty("get", out _));
    }
}

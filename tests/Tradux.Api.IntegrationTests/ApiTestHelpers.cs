using System.Net.Http.Json;
using System.Text.Json;
using Tradux.Contracts;

namespace Tradux.Api.IntegrationTests;

internal static class ApiTestHelpers
{
    public static string XmlRoute(string organization, string template, string operation) =>
        $"/v1/organizations/{organization}/templates/{template}/operations/{operation}/xml";

    public static string VersionsRoute(string organization, string template, string operation) =>
        $"/v1/organizations/{organization}/templates/{template}/operations/{operation}/versions";

    public static Task<HttpResponseMessage> PostGenerateAsync(
        this HttpClient client, string organization, string template, string operation, JsonElement data, int? version = null, string? correlationId = null) =>
        client.PostAsJsonAsync(XmlRoute(organization, template, operation), new { data, version, correlationId });

    public static async Task<GenerationResult> ReadResultAsync(this HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<GenerationResult>(JsonSerializerOptions.Web))!;
}

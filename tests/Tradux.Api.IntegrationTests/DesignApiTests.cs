using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Tradux.Api.IntegrationTests.Infrastructure;
using Tradux.Contracts.Design;
using Tradux.Tests.Shared;

namespace Tradux.Api.IntegrationTests;

/// <summary>
/// Endpoints de desenho do editor (Fase 6) pela API real. O corpo do teste
/// de rascunho é montado como JSON cru — exatamente o formato que o editor
/// (JavaScript) envia — para provar o contrato do lado do cliente.
/// </summary>
[Collection(MongoCollection.Name)]
public class DesignApiTests(MongoFixture mongo)
{
    private static object SchemaBody(params string[] files) => new
    {
        files = files.Select(f => new { name = f, contentBase64 = Convert.ToBase64String(File.ReadAllBytes(SnapshotFixtures.SchemaPath(f))) }),
        entryPoint = files[0],
    };

    [Fact]
    public async Task Operators_endpoint_lists_the_catalog()
    {
        await using var factory = new ApiFactory(mongo);
        var operators = await factory.CreateClient().GetFromJsonAsync<List<OperatorInfo>>("/v1/design/operators", JsonSerializerOptions.Web);

        Assert.Equal(26, operators!.Count); // 24 da seção 13 + ifElse e truncate (Fase 21)
        Assert.Contains(operators, o => o.Name == "formatDate" && o.Category == "Formatação");
    }

    [Fact]
    public async Task Schema_endpoint_returns_the_destination_tree_and_400_for_bad_xsd()
    {
        await using var factory = new ApiFactory(mongo);
        var client = factory.CreateClient();

        var ok = await client.PostAsJsonAsync("/v1/design/schema", SchemaBody("exemplo-colecao.xsd"));
        var bad = await client.PostAsJsonAsync("/v1/design/schema", new
        {
            files = new[] { new { name = "x.xsd", contentBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("<nao-xsd")) } },
            entryPoint = "x.xsd",
        });

        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        using var tree = JsonDocument.Parse(await ok.Content.ReadAsStringAsync());
        Assert.Equal("PedidoComItens", tree.RootElement.GetProperty("rootElementName").GetString());
        Assert.Equal("Complex", tree.RootElement.GetProperty("root").GetProperty("kind").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task Test_endpoint_accepts_the_mapping_json_the_editor_produces()
    {
        await using var factory = new ApiFactory(mongo);
        var client = factory.CreateClient();

        // Formato do editor: nós com "$type" (field/literal/operator), nomes camelCase.
        var body = $$"""
            {
              "schema": {{JsonSerializer.Serialize(SchemaBody("exemplo-colecao.xsd"))}},
              "mapping": {
                "fields": [
                  { "destinationElementName": "numero", "value": { "$type": "field", "path": "Numero" } },
                  { "destinationElementName": "valorTotal", "value": { "$type": "field", "path": "ValorTotal" } }
                ],
                "complex": [
                  {
                    "destinationElementName": "itens",
                    "content": {
                      "collections": [
                        {
                          "destinationElementName": "item",
                          "sourceCollectionPath": "Itens",
                          "filter": { "$type": "operator", "operatorName": "greaterThan",
                                      "arguments": [ { "$type": "field", "path": "ValorUnitario" }, { "$type": "literal", "value": 0 } ] },
                          "itemContent": {
                            "fields": [
                              { "destinationElementName": "descricao",
                                "value": { "$type": "operator", "operatorName": "concat",
                                           "arguments": [ { "$type": "field", "path": "$index" }, { "$type": "literal", "value": ": " },
                                                          { "$type": "field", "path": "Descricao" } ] } },
                              { "destinationElementName": "valorUnitario", "value": { "$type": "field", "path": "ValorUnitario" } }
                            ]
                          }
                        }
                      ]
                    }
                  }
                ]
              },
              "data": {{SnapshotFixtures.PedidoJson().GetRawText()}}
            }
            """;

        var response = await client.PostAsync("/v1/design/test", new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<DraftTestResult>(JsonSerializerOptions.Web))!;
        Assert.True(result.Publishable, string.Join("; ", result.Generation?.Diagnostics.Select(d => d.Code) ?? []));
        Assert.Contains("<descricao>0: Consultoria</descricao>", result.Generation!.Xml);
        Assert.DoesNotContain("Cortesia", result.Generation.Xml);
    }

    [Fact]
    public async Task Test_endpoint_rejects_a_body_without_mapping_with_400()
    {
        await using var factory = new ApiFactory(mongo);

        var response = await factory.CreateClient().PostAsJsonAsync("/v1/design/test", new { schema = SchemaBody("exemplo-simples.xsd"), data = new { } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Design_body_above_the_configured_limit_is_413()
    {
        await using var factory = new ApiFactory(mongo).WithWebHostBuilder(b => b.UseSetting("Tradux:Design:MaxRequestBytes", "2000"));
        var client = factory.CreateClient();

        var big = await client.PostAsJsonAsync("/v1/design/schema", SchemaBody("exemplo-simples.xsd"));
        var small = await client.GetAsync("/v1/design/operators");

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, big.StatusCode);
        Assert.Equal(HttpStatusCode.OK, small.StatusCode);
    }

    [Fact]
    public async Task Every_route_that_receives_xsd_has_the_design_body_limit()
    {
        await using var factory = new ApiFactory(mongo).WithWebHostBuilder(b => b.UseSetting("Tradux:Design:MaxRequestBytes", "2000"));
        var client = factory.CreateClient();
        var body = new { schema = SchemaBody("exemplo-simples.xsd"), mapping = new { fields = Array.Empty<object>() }, scenarios = Array.Empty<object>() };
        var template = $"/v1/organizations/{SnapshotFixtures.Organization}/templates/limite/operations/{SnapshotFixtures.Operation}";

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await client.PutAsJsonAsync("/v1/templates/limite", body)).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await client.PostAsJsonAsync($"{template}/publications", body)).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await client.PostAsJsonAsync($"{template}/schema-changes", new { after = SchemaBody("exemplo-simples.xsd") })).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await client.PostAsJsonAsync($"/v1/organizations/{SnapshotFixtures.Organization}/layers",
            new { layerId = "base", kind = "Base", schema = SchemaBody("exemplo-simples.xsd") })).StatusCode);
    }

    [Fact]
    public async Task Cors_is_limited_to_the_design_endpoints()
    {
        await using var factory = new ApiFactory(mongo).WithWebHostBuilder(b =>
            b.UseSetting("Tradux:Cors:AllowedOrigins:0", "http://editor.local"));
        var request = new HttpRequestMessage(HttpMethod.Get, "/v1/organizations/o/templates/t/operations/e/versions");
        request.Headers.Add("Origin", "http://editor.local");

        var response = await factory.CreateClient().SendAsync(request);

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Cors_allows_only_the_configured_editor_origin()
    {
        await using var factory = new ApiFactory(mongo).WithWebHostBuilder(b =>
            b.UseSetting("Tradux:Cors:AllowedOrigins:0", "http://editor.local"));
        var client = factory.CreateClient();

        var allowed = new HttpRequestMessage(HttpMethod.Get, "/v1/design/operators");
        allowed.Headers.Add("Origin", "http://editor.local");
        var other = new HttpRequestMessage(HttpMethod.Get, "/v1/design/operators");
        other.Headers.Add("Origin", "http://evil.local");

        var allowedResponse = await client.SendAsync(allowed);
        var otherResponse = await client.SendAsync(other);

        Assert.Equal("http://editor.local", Assert.Single(allowedResponse.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.False(otherResponse.Headers.Contains("Access-Control-Allow-Origin"));
    }
}

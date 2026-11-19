using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Tradux.Api.IntegrationTests.Infrastructure;
using Tradux.Application.Snapshots;
using Tradux.Domain.Mapping;
using Tradux.Tests.Shared;

namespace Tradux.Api.IntegrationTests;

/// <summary>
/// Gate da Fase 22 (ADR-0025): o produto é o microsserviço tradutor. O autor
/// cadastra o template pelo nome (<c>PUT /v1/templates/{nome}</c>), o serviço
/// versiona, e o integrador envia o XML de origem e recebe o XML convertido
/// (<c>POST /v1/templates/{nome}/convert</c>) — organização sempre a da
/// credencial, por HTTP, com MongoDB real.
/// </summary>
[Collection(MongoCollection.Name)]
public sealed class Phase22GateTests(MongoFixture mongo)
{
    private const string Name = "dps-erp";
    private static readonly JsonSerializerOptions Web = JsonSerializerOptions.Web;

    private static object Upload(IReadOnlyDictionary<string, byte[]> files, string entryPoint) =>
        new { files = files.Select(f => new { name = f.Key, contentBase64 = Convert.ToBase64String(f.Value) }), entryPoint };

    private static object Body(SnapshotContent content) => new
    {
        schema = Upload(content.SchemaFiles, content.SchemaEntryPoint),
        sourceSchema = Upload(content.SourceSchema!.Files, content.SourceSchema.EntryPoint),
        mapping = content.Mapping,
        scenarios = SnapshotFixtures.ErpScenarios(),
    };

    /// <summary>v2: mesma estrutura, discriminação vira constante — dá para ver qual versão converteu.</summary>
    private static SnapshotContent Version2() => SnapshotFixtures.ErpToDps() with
    {
        Mapping = new MappingTemplate
        {
            Fields =
            [
                .. SnapshotFixtures.ErpToDps().Mapping.Fields.Where(f => f.DestinationElementName != "discriminacaoServico"),
                new FieldMapping("discriminacaoServico", new LiteralNode("versao 2")),
            ],
        },
    };

    private static Task<HttpResponseMessage> RegisterAsync(HttpClient client, SnapshotContent content, string name = Name) =>
        client.PutAsJsonAsync($"/v1/templates/{name}", Body(content), Web);

    private static Task<HttpResponseMessage> ConvertAsync(HttpClient client, string xml, string query = "", string name = Name) =>
        client.PostAsync($"/v1/templates/{name}/convert{query}",
            new ByteArrayContent(Encoding.UTF8.GetBytes(xml)) { Headers = { ContentType = new("application/xml") } });

    private static string Element(string xml, string localName) =>
        XDocument.Parse(xml).Root!.Elements().Single(e => e.Name.LocalName == localName).Value;

    private static string Discriminacao(string xml) => Element(xml, "discriminacaoServico");

    [Fact]
    public async Task Template_is_registered_by_name_and_the_service_returns_the_converted_xml()
    {
        await using var factory = new ApiFactory(mongo);
        var admin = factory.CreateClient();
        var runtime = factory.CreateClient(ApiFactory.RuntimeA);

        using var registered = await RegisterAsync(admin, SnapshotFixtures.ErpToDps());
        Assert.True(registered.StatusCode == HttpStatusCode.OK, await registered.Content.ReadAsStringAsync());
        Assert.Equal(1, (await registered.Content.ReadFromJsonAsync<JsonElement>(Web)).GetProperty("snapshotVersion").GetInt32());

        using var converted = await ConvertAsync(runtime, SnapshotFixtures.ErpSourceXml(), "?correlationId=pedido-7");
        var xml = await converted.Content.ReadAsStringAsync();
        Assert.True(converted.StatusCode == HttpStatusCode.OK, xml);
        Assert.Equal("application/xml", converted.Content.Headers.ContentType!.MediaType);
        Assert.Equal("1", converted.Headers.GetValues("X-Template-Version").Single());
        Assert.Equal("pedido-7", converted.Headers.GetValues("X-Correlation-Id").Single());
        Assert.Equal("000123", Element(xml, "numero"));
        Assert.Equal("1500.50", Element(xml, "valorServico"));
        Assert.Equal("Consultoria em sistemas", Discriminacao(xml));

        // format=json: o resultado completo, com o mesmo XML.
        var json = await (await ConvertAsync(runtime, SnapshotFixtures.ErpSourceXml(), "?format=json")).Content.ReadFromJsonAsync<JsonElement>(Web);
        Assert.True(json.GetProperty("success").GetBoolean());
        Assert.Equal(xml, json.GetProperty("xml").GetString());

        // Lista da tela inicial: só o ponteiro ativo.
        var list = await admin.GetFromJsonAsync<JsonElement>("/v1/templates", Web);
        var entry = Assert.Single(list.EnumerateArray());
        Assert.Equal(Name, entry.GetProperty("templateId").GetString());
        Assert.Equal("default", entry.GetProperty("operation").GetString());
        Assert.Equal(1, entry.GetProperty("activeVersion").GetInt32());
    }

    [Fact]
    public async Task Source_xml_outside_the_source_xsd_is_422_with_diagnostics_and_unknown_template_is_404()
    {
        await using var factory = new ApiFactory(mongo);
        await RegisterAsync(factory.CreateClient(), SnapshotFixtures.ErpToDps());
        var runtime = factory.CreateClient(ApiFactory.RuntimeA);

        using var invalid = await ConvertAsync(runtime, SnapshotFixtures.ErpSourceXml().Replace("situacao=\"1\"", "situacao=\"9\""));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        var result = await invalid.Content.ReadFromJsonAsync<JsonElement>(Web);
        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Contains(result.GetProperty("diagnostics").EnumerateArray(), d => d.GetProperty("code").GetString() == "SOURCE_XML_INVALID");

        Assert.Equal(HttpStatusCode.NotFound, (await ConvertAsync(runtime, SnapshotFixtures.ErpSourceXml(), name: "nao-existe")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ConvertAsync(runtime, "")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ConvertAsync(runtime, SnapshotFixtures.ErpSourceXml(), "?versions=abc")).StatusCode);
    }

    [Fact]
    public async Task The_service_versions_the_template_opens_any_version_and_rolls_back()
    {
        await using var factory = new ApiFactory(mongo);
        var admin = factory.CreateClient();
        var runtime = factory.CreateClient(ApiFactory.RuntimeA);
        await RegisterAsync(admin, SnapshotFixtures.ErpToDps());

        using var second = await RegisterAsync(admin, Version2());
        Assert.Equal(2, (await second.Content.ReadFromJsonAsync<JsonElement>(Web)).GetProperty("snapshotVersion").GetInt32());
        Assert.Equal("versao 2", Discriminacao(await (await ConvertAsync(runtime, SnapshotFixtures.ErpSourceXml())).Content.ReadAsStringAsync()));
        Assert.Equal("Consultoria em sistemas", Discriminacao(await (await ConvertAsync(runtime, SnapshotFixtures.ErpSourceXml(), "?version=1")).Content.ReadAsStringAsync()));

        var versions = await admin.GetFromJsonAsync<JsonElement>($"/v1/templates/{Name}/versions", Web);
        Assert.Equal([2, 1], versions.EnumerateArray().Select(v => v.GetProperty("version").GetInt32()));
        Assert.Equal(["Active", "Superseded"], versions.EnumerateArray().Select(v => v.GetProperty("status").GetString()));

        // A versão 1 volta inteira (é o que o editor abre para fazer a próxima).
        var v1 = await admin.GetFromJsonAsync<JsonElement>($"/v1/templates/{Name}/versions/1", Web);
        Assert.Equal(1, v1.GetProperty("version").GetInt32());
        Assert.Equal("exemplo-simples.xsd", v1.GetProperty("schema").GetProperty("entryPoint").GetString());
        Assert.Equal("origem-exemplo.xsd", v1.GetProperty("sourceSchema").GetProperty("entryPoint").GetString());
        Assert.Equal(SnapshotFixtures.ErpToDps().Mapping.Fields.Count, v1.GetProperty("mapping").GetProperty("fields").GetArrayLength());
        Assert.NotEmpty(v1.GetProperty("scenarios").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/v1/templates/{Name}/versions/9")).StatusCode);

        // Republicar o conteúdo aberto (o que o editor faz) gera a v3 com o mesmo resultado da v1.
        var reopened = new
        {
            schema = v1.GetProperty("schema"),
            sourceSchema = v1.GetProperty("sourceSchema"),
            mapping = v1.GetProperty("mapping"),
            lookupTables = v1.GetProperty("lookupTables"),
            scenarios = v1.GetProperty("scenarios"),
        };
        using var third = await admin.PutAsJsonAsync($"/v1/templates/{Name}", reopened, Web);
        Assert.True(third.StatusCode == HttpStatusCode.OK, await third.Content.ReadAsStringAsync());
        Assert.Equal(3, (await third.Content.ReadFromJsonAsync<JsonElement>(Web)).GetProperty("snapshotVersion").GetInt32());

        using var rollback = await admin.PostAsJsonAsync($"/v1/templates/{Name}/rollback", new { version = 2 }, Web);
        Assert.Equal(HttpStatusCode.OK, rollback.StatusCode);
        Assert.Equal("versao 2", Discriminacao(await (await ConvertAsync(runtime, SnapshotFixtures.ErpSourceXml())).Content.ReadAsStringAsync()));
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/v1/templates/{Name}/rollback", new { version = 42 }, Web)).StatusCode);
    }

    [Fact]
    public async Task Failing_gate_keeps_the_active_version_and_names_are_validated()
    {
        await using var factory = new ApiFactory(mongo);
        var admin = factory.CreateClient();
        await RegisterAsync(admin, SnapshotFixtures.ErpToDps());

        // Cenário que não bate com o mapeamento: recusado, a v1 continua ativa.
        var wrong = new
        {
            schema = Upload(SnapshotFixtures.ErpToDps().SchemaFiles, "exemplo-simples.xsd"),
            sourceSchema = Upload(SnapshotFixtures.ErpSourceSchema().Files, "origem-exemplo.xsd"),
            mapping = Version2().Mapping,
            scenarios = new[] { new { name = "errado", sourceXml = SnapshotFixtures.ErpSourceXml(), assertions = new[] { new { xPath = "/DPS/discriminacaoServico", kind = "Equals", expected = "outra coisa" } } } },
        };
        using var refused = await admin.PutAsJsonAsync($"/v1/templates/{Name}", wrong, Web);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        var list = await admin.GetFromJsonAsync<JsonElement>("/v1/templates", Web);
        Assert.Equal(1, list.EnumerateArray().Single().GetProperty("activeVersion").GetInt32());

        Assert.Equal(HttpStatusCode.BadRequest, (await RegisterAsync(admin, SnapshotFixtures.ErpToDps(), name: "nome%20com%20espa%C3%A7o")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await RegisterAsync(admin, SnapshotFixtures.ErpToDps(), name: "-comeca-com-hifen")).StatusCode);
    }

    [Fact]
    public async Task Permissions_and_organization_isolation_are_enforced()
    {
        await using var factory = new ApiFactory(mongo);
        await RegisterAsync(factory.CreateClient(), SnapshotFixtures.ErpToDps());

        // Credencial de runtime só converte; editor não converte; sem credencial, 401.
        Assert.Equal(HttpStatusCode.Forbidden, (await RegisterAsync(factory.CreateClient(ApiFactory.RuntimeA), SnapshotFixtures.ErpToDps())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await factory.CreateClient(ApiFactory.RuntimeA).PostAsJsonAsync($"/v1/templates/{Name}/rollback", new { version = 1 }, Web)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ConvertAsync(factory.CreateClient(ApiFactory.EditorA), SnapshotFixtures.ErpSourceXml())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ConvertAsync(factory.CreateClient(secret: null), SnapshotFixtures.ErpSourceXml())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient(secret: null).GetAsync("/v1/templates")).StatusCode);

        // Outra organização: o mesmo nome não existe lá, e a lista dela é vazia.
        var otherOrg = factory.CreateClient(ApiFactory.AdminB);
        Assert.Equal(HttpStatusCode.NotFound, (await ConvertAsync(otherOrg, SnapshotFixtures.ErpSourceXml())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await otherOrg.GetAsync($"/v1/templates/{Name}/versions/1")).StatusCode);
        Assert.Empty((await otherOrg.GetFromJsonAsync<JsonElement>("/v1/templates", Web)).EnumerateArray());
        Assert.Empty((await otherOrg.GetFromJsonAsync<JsonElement>($"/v1/templates/{Name}/versions", Web)).EnumerateArray());
    }
}

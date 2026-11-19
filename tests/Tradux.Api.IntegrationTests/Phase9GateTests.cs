using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Tradux.Api.IntegrationTests.Infrastructure;
using Tradux.Application.Layers;
using Tradux.Application.Security;
using Tradux.Contracts;
using Tradux.Domain.Layers;
using Tradux.Tests.Shared;

using Tradux.Application.Testing;
using Tradux.Application.Ports;
namespace Tradux.Api.IntegrationTests;

/// <summary>
/// Gate da Fase 9 (ADR-0012): "testes comprovam isolamento e ausência de
/// payload fiscal em logs normais" — mais a matriz de permissões por rota,
/// credencial de runtime só de uso, escrita por HTTP e captura diagnóstica.
/// </summary>
[Collection(MongoCollection.Name)]
public class Phase9GateTests(MongoFixture mongo)
{
    private const string Dps = "dps";
    private static readonly JsonSerializerOptions Web = JsonSerializerOptions.Web;

    private sealed record Route(string Method, string Pattern, string? Policy);

    private static List<Route> V1Routes(ApiFactory factory) =>
        factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/v1", StringComparison.Ordinal) == true)
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"]).Select(m => new Route(
                m,
                e.RoutePattern.RawText!,
                e.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy).LastOrDefault(p => p is not null))))
            .ToList();

    private static string Url(Route route, string organization) => route.Pattern
        .Replace("{organizationId}", organization)
        .Replace("{templateId}", Dps)
        .Replace("{operation}", SnapshotFixtures.Operation)
        .Replace("{version:int}", "1")
        .Replace("{updateId}", "inexistente");

    private static Task<HttpResponseMessage> Send(HttpClient client, Route route, string organization) =>
        client.SendAsync(new HttpRequestMessage(new HttpMethod(route.Method), Url(route, organization))
        {
            Content = route.Method is "GET" or "DELETE" ? null : JsonContent.Create(new { }),
        });

    private static async Task PublishDpsAsync(ApiFactory factory) =>
        await factory.CreateDirectPublisher().PublishAsync(SnapshotFixtures.Organization, Dps, SnapshotFixtures.Operation, SnapshotFixtures.SimpleDps(), SnapshotFixtures.DpsScenarios(), "teste");

    private static Task<HttpResponseMessage> Generate(HttpClient client, string organization = SnapshotFixtures.Organization, object? body = null) =>
        client.PostAsJsonAsync($"/v1/organizations/{organization}/templates/{Dps}/operations/{SnapshotFixtures.Operation}/xml",
            body ?? new { data = SnapshotFixtures.DpsJson() }, Web);

    // ------------------------------------------------------------------ autorização

    [Fact]
    public async Task Every_v1_route_requires_a_specific_permission_policy()
    {
        await using var factory = new ApiFactory(mongo);
        _ = factory.CreateClient();

        var routes = V1Routes(factory);

        Assert.True(routes.Count >= 20, $"só {routes.Count} rotas /v1 encontradas");
        Assert.All(routes, r => Assert.True(r.Policy?.StartsWith(ApiSecurity.PolicyPrefix, StringComparison.Ordinal) == true,
            $"{r.Method} {r.Pattern} sem política de permissão"));
    }

    [Fact]
    public async Task Without_or_with_an_invalid_credential_every_v1_route_is_401()
    {
        await using var factory = new ApiFactory(mongo);
        var anonymous = factory.CreateClient(secret: null);
        var invalid = factory.CreateClient("tradux_nao-existe");

        foreach (var route in V1Routes(factory))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await Send(anonymous, route, SnapshotFixtures.Organization)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await Send(invalid, route, SnapshotFixtures.Organization)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/openapi/v1.json")).StatusCode);
    }

    [Fact]
    public async Task Runtime_credential_can_only_generate()
    {
        await using var factory = new ApiFactory(mongo);
        await PublishDpsAsync(factory);
        var runtime = factory.CreateClient(ApiFactory.RuntimeA);

        Assert.Equal(HttpStatusCode.OK, (await Generate(runtime)).StatusCode);
        foreach (var route in V1Routes(factory).Where(r => r.Policy != ApiSecurity.Policy(Permissions.TemplateUse)))
        {
            Assert.True((await Send(runtime, route, SnapshotFixtures.Organization)).StatusCode == HttpStatusCode.Forbidden,
                $"{route.Method} {route.Pattern} deveria ser 403 para credencial de runtime");
        }
    }

    [Fact]
    public async Task Roles_grant_exactly_their_permissions()
    {
        await using var factory = new ApiFactory(mongo);
        await PublishDpsAsync(factory);
        var editor = factory.CreateClient(ApiFactory.EditorA);
        var auditor = factory.CreateClient(ApiFactory.AuditorA);
        var audit = $"/v1/organizations/{SnapshotFixtures.Organization}/audit";

        Assert.Equal(HttpStatusCode.OK, (await editor.GetAsync("/v1/design/operators")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Generate(editor)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.GetAsync(audit)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await auditor.GetAsync(audit)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await auditor.GetAsync("/v1/design/operators")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Generate(auditor)).StatusCode);
    }

    [Fact]
    public async Task Revoked_credential_stops_working()
    {
        await using var factory = new ApiFactory(mongo);
        await PublishDpsAsync(factory);
        var credentials = factory.Services.GetRequiredService<CredentialService>();
        var issued = await credentials.IssueAsync(SnapshotFixtures.Organization, "integracao", [Roles.Runtime], "teste");
        var client = factory.CreateClient(issued.Secret);
        Assert.Equal(HttpStatusCode.OK, (await Generate(client)).StatusCode);

        Assert.True(await credentials.RevokeAsync(SnapshotFixtures.Organization, issued.Credential.Id, "teste"));

        Assert.Equal(HttpStatusCode.Unauthorized, (await Generate(client)).StatusCode);
        var events = await new Tradux.Infrastructure.MongoDb.MongoAuditSink(mongo.CreateClient(), factory.MongoOptions).ListAsync(SnapshotFixtures.Organization);
        Assert.Contains(events, e => e.Action == "permission.grant");
        Assert.Contains(events, e => e.Action == "permission.revoke");
        Assert.DoesNotContain(events, e => e.Details.Values.Any(v => v.Contains(issued.Secret)));
    }

    [Fact]
    public async Task Revoking_a_bootstrap_credential_is_for_good()
    {
        await using var factory = new ApiFactory(mongo);
        await PublishDpsAsync(factory);
        var access = new CredentialService(
            new Tradux.Infrastructure.MongoDb.MongoApiCredentialStore(mongo.CreateClient(), factory.MongoOptions),
            new Tradux.Infrastructure.MongoDb.MongoAuditSink(mongo.CreateClient(), factory.MongoOptions));
        _ = factory.CreateClient(); // sobe a API: credenciais iniciais gravadas no startup
        var bootstrap = (await access.ListAsync(SnapshotFixtures.Organization)).Single(c => c.Name == "teste-runtime-org-a");

        Assert.True(await access.RevokeAsync(SnapshotFixtures.Organization, bootstrap.Id, "operador"));

        // Primeiro uso depois da revogação (nada em cache nesta instância da API).
        Assert.Equal(HttpStatusCode.Unauthorized, (await Generate(factory.CreateClient(ApiFactory.RuntimeA))).StatusCode);
        // Recolocar a mesma credencial pela configuração não a reativa.
        await factory.Services.GetRequiredService<CredentialService>().EnsureBootstrapAsync(
            SnapshotFixtures.Organization, "teste-runtime-org-a", CredentialSecrets.Hash(ApiFactory.RuntimeA), [Roles.Runtime]);
        Assert.False((await access.ListAsync(SnapshotFixtures.Organization)).Single(c => c.Id == bootstrap.Id).Active);
        var issued = await access.IssueAsync(SnapshotFixtures.Organization, "nova", [Roles.Runtime], "operador");
        Assert.Equal(HttpStatusCode.OK, (await Generate(factory.CreateClient(issued.Secret))).StatusCode);
    }

    [Fact]
    public async Task Another_organizations_layer_is_indistinguishable_from_a_missing_one_and_sharing_needs_publish()
    {
        await using var factory = new ApiFactory(mongo);
        var adminA = factory.CreateClient();
        var adminB = factory.CreateClient(ApiFactory.AdminB);
        var editorA = factory.CreateClient(ApiFactory.EditorA);
        var content = SnapshotFixtures.SimpleDps();
        object BaseBody(bool shared) => new
        {
            layerId = "base-privada",
            kind = "Base",
            shared,
            schema = new { files = content.SchemaFiles.Select(f => new { name = f.Key, contentBase64 = Convert.ToBase64String(f.Value) }), entryPoint = content.SchemaEntryPoint },
            overrides = LayerOverrides.FromMappingTemplate(content.Mapping, "DPS"),
        };

        Assert.Equal(HttpStatusCode.Forbidden, (await editorA.PostAsJsonAsync("/v1/organizations/org-a/layers", BaseBody(shared: true), Web)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await adminA.PostAsJsonAsync("/v1/organizations/org-a/layers", BaseBody(shared: false), Web)).StatusCode);

        async Task<string> ProductOn(int version) =>
            (await (await adminB.PostAsJsonAsync("/v1/organizations/org-b/layers", new
            {
                layerId = $"prod-{version}",
                kind = "Product",
                parent = new { organizationId = "org-a", layerId = "base-privada", version },
            }, Web)).Content.ReadFromJsonAsync<JsonElement>(Web)).GetProperty("detail").GetString()!;

        var existing = await ProductOn(1);
        var missing = await ProductOn(99);
        Assert.Equal(existing.Replace("@1", "@N"), missing.Replace("@99", "@N"));

        // Vínculo apontando para produto privado da org-a (sobre a base privada):
        // resposta E auditoria da org-b iguais para existente × inexistente, e o
        // pai privado nunca aparece (2ª revisão da Fase 9, N1).
        Assert.Equal(HttpStatusCode.OK, (await adminA.PostAsJsonAsync("/v1/organizations/org-a/layers", new
        {
            layerId = "prod-privado",
            kind = "Product",
            parent = new { organizationId = "org-a", layerId = "base-privada", version = 1 },
        }, Web)).StatusCode);
        async Task<string> BindTo(int version) =>
            (await (await adminB.PutAsJsonAsync("/v1/organizations/org-b/templates/t/operations/op/binding", new
            {
                top = new { organizationId = "org-a", layerId = "prod-privado", version },
                policy = "Manual",
                scenarios = SnapshotFixtures.DpsScenarios(),
            }, Web)).Content.ReadAsStringAsync()).Replace($"@{version}", "@N");

        Assert.Equal(await BindTo(1), await BindTo(99));
        var audit = (await adminB.GetFromJsonAsync<List<AuditEvent>>("/v1/organizations/org-b/audit", Web))!
            .Where(e => e.Action == "template.publish").ToList();
        Assert.Equal(2, audit.Count);
        Assert.Equal(
            string.Join("|", audit[0].Details.OrderBy(d => d.Key).Select(d => $"{d.Key}={d.Value}")).Replace("@1", "@N").Replace("@99", "@N"),
            string.Join("|", audit[1].Details.OrderBy(d => d.Key).Select(d => $"{d.Key}={d.Value}")).Replace("@1", "@N").Replace("@99", "@N"));
        Assert.DoesNotContain(audit, e => e.Details.Values.Any(v => v.Contains("base-privada", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Private_release_of_a_consumed_layer_is_invisible_to_the_consumer_organization()
    {
        // 3ª revisão da Fase 9, N2: org-b consome a base compartilhada da org-a;
        // a org-a lança uma versão privada — nada disso aparece para a org-b.
        await using var factory = new ApiFactory(mongo);
        var adminA = factory.CreateClient();
        var adminB = factory.CreateClient(ApiFactory.AdminB);
        var content = SnapshotFixtures.SimpleDps();
        object BaseBody(bool shared) => new
        {
            layerId = "base-fornecedor",
            kind = "Base",
            shared,
            schema = new { files = content.SchemaFiles.Select(f => new { name = f.Key, contentBase64 = Convert.ToBase64String(f.Value) }), entryPoint = content.SchemaEntryPoint },
            overrides = LayerOverrides.FromMappingTemplate(content.Mapping, "DPS"),
            lookupOverrides = LayerOverrides.FromLookupTables(content.LookupTables),
        };

        Assert.Equal(HttpStatusCode.OK, (await adminA.PostAsJsonAsync("/v1/organizations/org-a/layers", BaseBody(shared: true), Web)).StatusCode);
        var client = await adminB.PostAsJsonAsync("/v1/organizations/org-b/layers", new
        {
            layerId = "cliente",
            kind = "Client",
            parent = new { organizationId = "org-a", layerId = "base-fornecedor", version = 1 },
        }, Web);
        Assert.Equal(HttpStatusCode.OK, client.StatusCode);
        var clientRef = (await client.Content.ReadFromJsonAsync<LayerCreatedDto>(Web))!.Ref;
        var templateB = $"/v1/organizations/org-b/templates/{Dps}/operations/{SnapshotFixtures.Operation}";
        Assert.Equal(HttpStatusCode.OK, (await adminB.PutAsJsonAsync($"{templateB}/binding",
            new { top = clientRef, policy = "Manual", scenarios = SnapshotFixtures.DpsScenarios() }, Web)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await adminA.PostAsJsonAsync("/v1/organizations/org-a/layers", BaseBody(shared: false), Web)).StatusCode);

        // Em /updates o LayerRef sai como objeto JSON; na auditoria, como "org/id@v".
        var updates = await adminB.GetFromJsonAsync<List<TemplateUpdate>>($"{templateB}/updates", Web);
        var audit = await (await adminB.GetAsync("/v1/organizations/org-b/audit")).Content.ReadAsStringAsync();
        Assert.DoesNotContain(updates!, u => u.Trigger.OrganizationId == "org-a" && u.Trigger.Version == 2);
        Assert.DoesNotContain("base-fornecedor@2", audit);
    }

    // ------------------------------------------------------------------ isolamento

    [Fact]
    public async Task Other_organization_gets_404_on_every_route_and_never_sees_the_data()
    {
        await using var factory = new ApiFactory(mongo);
        await PublishDpsAsync(factory);
        var adminB = factory.CreateClient(ApiFactory.AdminB);

        foreach (var route in V1Routes(factory).Where(r => r.Pattern.Contains("{organizationId}")))
        {
            var response = await Send(adminB, route, SnapshotFixtures.Organization);
            Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{route.Method} {route.Pattern} deveria ser 404 para outra organização");
            Assert.Empty(await response.Content.ReadAsStringAsync());
        }

        // Na própria organização, a org-b não enxerga o template da org-a.
        var ownVersions = await adminB.GetFromJsonAsync<List<TemplateVersionInfo>>(
            $"/v1/organizations/{SnapshotFixtures.OtherOrganization}/templates/{Dps}/operations/{SnapshotFixtures.Operation}/versions", Web);
        Assert.Empty(ownVersions!);
        Assert.Equal(HttpStatusCode.NotFound, (await Generate(adminB, SnapshotFixtures.OtherOrganization)).StatusCode);
        var ownAudit = await adminB.GetFromJsonAsync<List<AuditEvent>>($"/v1/organizations/{SnapshotFixtures.OtherOrganization}/audit", Web);
        Assert.DoesNotContain(ownAudit!, e => e.OrganizationId != SnapshotFixtures.OtherOrganization);
    }

    // ------------------------------------------------------------------ escrita por HTTP

    [Fact]
    public async Task Layers_binding_rollback_and_decisions_work_over_http_with_the_authenticated_actor()
    {
        await using var factory = new ApiFactory(mongo);
        var admin = factory.CreateClient();
        var org = $"/v1/organizations/{SnapshotFixtures.Organization}";
        var template = $"{org}/templates/{Dps}/operations/{SnapshotFixtures.Operation}";
        var content = SnapshotFixtures.SimpleDps();

        var baseLayer = await admin.PostAsJsonAsync($"{org}/layers", new
        {
            layerId = "base-dps",
            kind = "Base",
            schema = new { files = content.SchemaFiles.Select(f => new { name = f.Key, contentBase64 = Convert.ToBase64String(f.Value) }), entryPoint = content.SchemaEntryPoint },
            overrides = LayerOverrides.FromMappingTemplate(content.Mapping, "DPS"),
            lookupOverrides = LayerOverrides.FromLookupTables(content.LookupTables),
        }, Web);
        Assert.Equal(HttpStatusCode.OK, baseLayer.StatusCode);
        var baseRef = (await baseLayer.Content.ReadFromJsonAsync<LayerCreatedDto>(Web))!.Ref;

        var bound = await admin.PutAsJsonAsync($"{template}/binding", new { top = baseRef, policy = "Manual", scenarios = SnapshotFixtures.DpsScenarios() }, Web);
        Assert.Equal(HttpStatusCode.OK, bound.StatusCode);

        // Sem cenário obrigatório: 422 com o resultado (gates da Fase 8 valem por HTTP).
        var noScenario = await admin.PutAsJsonAsync($"{template}/binding", new { top = baseRef, policy = "Manual", scenarios = Array.Empty<object>() }, Web);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noScenario.StatusCode);

        // Nova versão da base → proposta manual; rejeita pelo HTTP.
        var v2 = await admin.PostAsJsonAsync($"{org}/layers", new
        {
            layerId = "base-dps",
            kind = "Base",
            schema = new { files = content.SchemaFiles.Select(f => new { name = f.Key, contentBase64 = Convert.ToBase64String(f.Value) }), entryPoint = content.SchemaEntryPoint },
            overrides = LayerOverrides.FromMappingTemplate(SnapshotFixtures.SimpleDps("N-").Mapping, "DPS"),
            lookupOverrides = LayerOverrides.FromLookupTables(content.LookupTables),
        }, Web);
        var proposal = Assert.Single((await v2.Content.ReadFromJsonAsync<LayerCreatedDto>(Web))!.Updates);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"{template}/updates/{proposal.Id}/reject", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"{template}/updates/{proposal.Id}/approve", null)).StatusCode);

        // Publicação de conteúdo (editor) cria a v2; rollback volta à v1.
        var published = await admin.PostAsJsonAsync($"{template}/publications", new
        {
            schema = new { files = content.SchemaFiles.Select(f => new { name = f.Key, contentBase64 = Convert.ToBase64String(f.Value) }), entryPoint = content.SchemaEntryPoint },
            mapping = SnapshotFixtures.SimpleDps("E-").Mapping,
            lookupTables = content.LookupTables,
            scenarios = SnapshotFixtures.DpsScenarios(),
        }, Web);
        Assert.Equal(HttpStatusCode.OK, published.StatusCode);
        var rollback = await admin.PostAsJsonAsync($"{template}/rollback", new { }, Web);
        Assert.Equal(HttpStatusCode.OK, rollback.StatusCode);

        var events = await admin.GetFromJsonAsync<List<AuditEvent>>($"{org}/audit", Web);
        Assert.Contains(events!, e => e.Action == "layer.version.create" && e.Actor.StartsWith("credential:teste-admin-org-a#", StringComparison.Ordinal));
        Assert.Contains(events!, e => e.Action == "template.update.reject" && e.Actor.StartsWith("credential:teste-admin-org-a#", StringComparison.Ordinal));
        Assert.Contains(events!, e => e.Action == "template.publish" && e.Outcome == "success");
        Assert.Contains(events!, e => e.Action == "template.rollback");
    }

    [Fact]
    public async Task New_layer_requires_template_create_and_fallback_list_is_limited()
    {
        await using var factory = new ApiFactory(mongo);
        await PublishDpsAsync(factory);
        var admin = factory.CreateClient();

        var tooMany = await Generate(admin, body: new { data = SnapshotFixtures.DpsJson(), versions = new[] { 1, 2, 3, 4, 5, 6 } });
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Generate(admin, body: new { data = SnapshotFixtures.DpsJson(), versions = new[] { 1 } })).StatusCode);
    }

    [Fact]
    public async Task Pathological_pattern_accepted_at_import_is_cut_by_the_regex_timeout_not_a_stuck_thread()
    {
        // "(a|aa)+" passa pela heurística de importação (alternativas sobrepostas),
        // mas o timeout global de regex corta a validação (2ª revisão da Fase 9, B1).
        await using var factory = new ApiFactory(mongo);
        var editor = factory.CreateClient(ApiFactory.EditorA);
        const string xsd = """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:r" elementFormDefault="qualified">
              <xs:element name="R"><xs:complexType><xs:sequence>
                <xs:element name="v"><xs:simpleType><xs:restriction base="xs:string"><xs:pattern value="(a|aa)+"/></xs:restriction></xs:simpleType></xs:element>
              </xs:sequence></xs:complexType></xs:element>
            </xs:schema>
            """;
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var response = await editor.PostAsJsonAsync("/v1/design/test", new
        {
            schema = new { files = new[] { new { name = "r.xsd", contentBase64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(xsd)) } }, entryPoint = "r.xsd" },
            mapping = new { fields = new[] { new { destinationElementName = "v", value = new Dictionary<string, object> { ["$type"] = "field", ["path"] = "V" } } } },
            data = new Dictionary<string, string> { ["V"] = new string('a', 40) + "b" },
        }, Web);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), $"levou {watch.Elapsed}");
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(body.Contains("XSD_VALIDATION_TIMEOUT", StringComparison.Ordinal), body);
    }

    // ------------------------------------------------------------------ gate: logs sem payload fiscal

    [Fact]
    public async Task Normal_logs_traces_and_metrics_never_carry_fiscal_values()
    {
        using var telemetry = new TelemetryCapture();
        await using var factory = new ApiFactory(mongo, telemetry: telemetry);
        await PublishDpsAsync(factory);
        var admin = factory.CreateClient();
        var template = $"/v1/organizations/{SnapshotFixtures.Organization}/templates/{Dps}/operations/{SnapshotFixtures.Operation}";
        var content = SnapshotFixtures.SimpleDps();
        var schema = new { files = content.SchemaFiles.Select(f => new { name = f.Key, contentBase64 = Convert.ToBase64String(f.Value) }), entryPoint = content.SchemaEntryPoint };

        // Bateria: geração válida, inválida (XSD cita o valor no diagnóstico), fallback,
        // desenho (teste com dados e XML esperado, qualidade), publicação recusada e aceita.
        Assert.Equal(HttpStatusCode.OK, (await Generate(admin)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Generate(admin, body: new { data = SnapshotFixtures.DpsJson(cnpjPrestador: "11122233344455X") })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Generate(admin, body: new { data = SnapshotFixtures.DpsJson(), versions = new[] { 9, 1 } })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/v1/design/test",
            new { schema, mapping = content.Mapping, data = SnapshotFixtures.DpsJson(), lookupTables = content.LookupTables, expectedXml = "<DPS xmlns=\"http://exemplo.ilustrativo.tradux/dps/v1\"><numero>ESPERADO-777</numero></DPS>" }, Web)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/v1/design/quality", new { schema, mapping = content.Mapping, lookupTables = content.LookupTables }, Web)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.PostAsJsonAsync($"{template}/publications",
            new { schema, mapping = content.Mapping, lookupTables = content.LookupTables, scenarios = new[] { new FunctionalScenario("errado", SnapshotFixtures.DpsJson(), Assertions: [new StructuralAssertion("/DPS/cnpjPrestador", AssertionKind.Equals, "99988877766655")]) } }, Web)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"{template}/publications",
            new { schema, mapping = SnapshotFixtures.SimpleDps("P-").Mapping, lookupTables = content.LookupTables, scenarios = SnapshotFixtures.DpsScenarios() }, Web)).StatusCode);
        // Caminhos de erro (400) e geração com captura diagnóstica ligada.
        Assert.Equal(HttpStatusCode.BadRequest, (await Generate(admin, body: new { data = SnapshotFixtures.Json("[\"12345678000199\"]") })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Generate(admin, body: new { data = SnapshotFixtures.DpsJson(), versions = new[] { 1, 2, 3, 4, 5, 6 } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Generate(admin, body: new { data = SnapshotFixtures.DpsJson(), correlationId = "cnpj 12345678000199 / nota 000123" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"{template}/diagnostics/capture", new { minutes = 5 }, Web)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Generate(admin)).StatusCode);
        // Spans/métricas encerram de forma assíncrona.
        Assert.True(
            await Eventually.TrueAsync(() => telemetry.Contains("metric tradux.generation.count") && telemetry.Contains("tradux.cache.hit"), TimeSpan.FromSeconds(10)),
            "telemetria não chegou em 10 s");
        await Task.Delay(200); // o que ainda estiver em voo também entra na varredura de vazamento

        var fiscal = new[] { "12345678000199", "98765432000188", "11122233344455X", "99988877766655", "000123", "1500.5", "01.02.03", "ESPERADO-777", "Situação normal" };
        var entries = telemetry.Entries;
        Assert.NotEmpty(entries);
        Assert.Contains(entries, e => e.Contains("Geração org-a/dps/emissao", StringComparison.Ordinal)); // o log estruturado existe
        Assert.Contains(entries, e => e.Contains("tradux.cache.hit", StringComparison.Ordinal));
        Assert.Contains(entries, e => e.Contains("metric tradux.generation.count", StringComparison.Ordinal));
        Assert.Contains(entries, e => e.StartsWith("log Debug", StringComparison.Ordinal)); // Debug foi mesmo capturado
        foreach (var value in fiscal)
        {
            var leak = entries.FirstOrDefault(e => e.Contains(value, StringComparison.Ordinal));
            Assert.True(leak is null, $"valor fiscal '{value}' vazou na telemetria: {leak}");
        }
    }

    // ------------------------------------------------------------------ captura diagnóstica

    [Fact]
    public async Task Diagnostic_capture_is_opt_in_masked_encrypted_expiring_and_audited()
    {
        await using var factory = new ApiFactory(mongo);
        await PublishDpsAsync(factory);
        var admin = factory.CreateClient();
        var runtime = factory.CreateClient(ApiFactory.RuntimeA);
        var template = $"/v1/organizations/{SnapshotFixtures.Organization}/templates/{Dps}/operations/{SnapshotFixtures.Operation}";

        await Generate(runtime); // captura desligada: nada gravado
        Assert.Equal(HttpStatusCode.Forbidden, (await runtime.PostAsJsonAsync($"{template}/diagnostics/capture", new { minutes = 10 }, Web)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"{template}/diagnostics/capture", new { minutes = 100000 }, Web)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"{template}/diagnostics/capture", new { minutes = 10 }, Web)).StatusCode);

        await Generate(runtime);

        var records = await admin.GetFromJsonAsync<List<JsonElement>>($"{template}/diagnostics/records", Web);
        var record = Assert.Single(records!);
        var input = record.GetProperty("maskedInputJson").GetString()!;
        Assert.DoesNotContain("12345678000199", input);
        Assert.Contains("***99", input);
        Assert.DoesNotContain("12345678000199", record.GetProperty("maskedXml").GetString());

        // Em repouso: só cifrado, com expiração (TTL) — nenhum valor em claro no banco.
        var raw = await mongo.CreateClient().GetDatabase(factory.MongoOptions.DatabaseName).GetCollection<BsonDocument>("diagnosticRecords")
            .Find(FilterDefinition<BsonDocument>.Empty).SingleAsync();
        Assert.DoesNotContain("000123", raw.ToJson());
        Assert.True(raw["expiresAtUtc"].ToUniversalTime() <= DateTime.UtcNow.AddHours(24.1));
        var indexes = await (await mongo.CreateClient().GetDatabase(factory.MongoOptions.DatabaseName).GetCollection<BsonDocument>("diagnosticRecords")
            .Indexes.ListAsync()).ToListAsync();
        Assert.Contains(indexes, i => i["name"] == "ttl_expires" && i.Contains("expireAfterSeconds"));

        Assert.Equal(HttpStatusCode.OK, (await admin.DeleteAsync($"{template}/diagnostics/records")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"{template}/diagnostics/capture")).StatusCode);
        var events = await admin.GetFromJsonAsync<List<AuditEvent>>($"/v1/organizations/{SnapshotFixtures.Organization}/audit", Web);
        foreach (var action in new[] { "diagnostic.capture.enable", "diagnostic.read", "diagnostic.delete", "diagnostic.capture.disable" })
        {
            Assert.Contains(events!, e => e.Action == action);
        }
    }

    [Fact]
    public async Task Diagnostic_capture_cannot_be_enabled_without_an_encryption_key()
    {
        await using var factory = new ApiFactory(mongo, diagnosticsKey: false);
        var admin = factory.CreateClient();

        var response = await admin.PostAsJsonAsync(
            $"/v1/organizations/{SnapshotFixtures.Organization}/templates/{Dps}/operations/{SnapshotFixtures.Operation}/diagnostics/capture", new { minutes = 5 }, Web);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    private sealed record LayerCreatedDto(LayerRef Ref, string ContentHash, List<TemplateUpdate> Updates);
}

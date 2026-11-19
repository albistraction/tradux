using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Tradux.Api.IntegrationTests.Infrastructure;
using Tradux.Application.Layers;
using Tradux.Application.Publishing;
using Tradux.Application.Snapshots;
using Tradux.Contracts.Design;
using Tradux.Contracts.Quality;
using Tradux.Domain.Mapping;
using Tradux.Infrastructure.MongoDb;
using Tradux.Tests.Shared;

namespace Tradux.Api.IntegrationTests;

/// <summary>
/// Gate da Fase 8 contra a API real e MongoDB real: "não é possível publicar
/// com teste obrigatório falhando" — a versão servida pela API não muda e a
/// recusa fica na trilha de auditoria; o editor roda a mesma suíte estrutural
/// por <c>POST /v1/design/quality</c>.
/// </summary>
[Collection(MongoCollection.Name)]
public class Phase8GateTests(MongoFixture mongo)
{
    private const string TemplateId = "dps";

    private static SnapshotContent SerieOverflow()
    {
        var content = SnapshotFixtures.SimpleDps();
        return content with
        {
            Mapping = new MappingTemplate
            {
                Fields = [.. content.Mapping.Fields.Where(f => f.DestinationElementName != "serie"),
                    new FieldMapping("serie", OperatorNode.Of("concat", new LiteralNode("SERIE-"), new FieldReferenceNode("Serie")))],
            },
        };
    }

    private static object DraftBody(SnapshotContent content) => new
    {
        schema = new
        {
            files = content.SchemaFiles.Select(f => new { name = f.Key, contentBase64 = Convert.ToBase64String(f.Value) }),
            entryPoint = content.SchemaEntryPoint,
        },
        mapping = content.Mapping,
        lookupTables = content.LookupTables,
    };

    [Fact]
    public async Task Publication_with_a_failing_mandatory_test_never_reaches_the_api_and_is_audited()
    {
        await using var factory = new ApiFactory(mongo);
        var publisher = factory.CreateDirectPublisher();
        var client = factory.CreateClient();
        await publisher.PublishAsync(SnapshotFixtures.Organization, TemplateId, SnapshotFixtures.Operation, SnapshotFixtures.SimpleDps(), SnapshotFixtures.DpsScenarios(), "autor");
        var before = await (await client.PostGenerateAsync(SnapshotFixtures.Organization, TemplateId, SnapshotFixtures.Operation, SnapshotFixtures.DpsJson())).ReadResultAsync();
        Assert.Equal("1", before.VersionUsed);

        var structural = await publisher.TryPublishAsync(SnapshotFixtures.Organization, TemplateId, SnapshotFixtures.Operation, SerieOverflow(), SnapshotFixtures.DpsScenarios(), "autor");
        var functional = await publisher.TryPublishAsync(SnapshotFixtures.Organization, TemplateId, SnapshotFixtures.Operation, SnapshotFixtures.SimpleDps("V2-"), [], "autor");

        Assert.False(structural.Success);
        Assert.False(functional.Success);
        Assert.Contains(structural.Diagnostics, d => d.Code == StructuralTestGate.StructuralTestFailedCode);
        Assert.Contains(functional.Diagnostics, d => d.Code == FunctionalScenarioGate.ScenarioRequiredCode);
        var after = await (await client.PostGenerateAsync(SnapshotFixtures.Organization, TemplateId, SnapshotFixtures.Operation, SnapshotFixtures.DpsJson())).ReadResultAsync();
        Assert.Equal("1", after.VersionUsed);
        Assert.True(SnapshotFixtures.XmlSemanticallyEqual(before.Xml, after.Xml));

        var audit = await new MongoAuditSink(mongo.CreateClient(), factory.MongoOptions).ListAsync(SnapshotFixtures.Organization);
        Assert.Equal(2, audit.Count(e => e.Action == "template.publish" && e.Outcome == "rejected"));
        Assert.Contains(audit, e => e.Action == "template.publish" && e.Outcome == "success");
        Assert.DoesNotContain(audit, e => e.Details.Values.Any(v => v.Contains("12345678000199")));
    }

    [Fact]
    public async Task Quality_endpoint_runs_the_structural_suite_without_exposing_generated_data()
    {
        await using var factory = new ApiFactory(mongo);
        var client = factory.CreateClient();

        var ok = await client.PostAsJsonAsync("/v1/design/quality", DraftBody(SnapshotFixtures.PedidoComItens()), JsonSerializerOptions.Web);
        var defective = await client.PostAsJsonAsync("/v1/design/quality", DraftBody(SerieOverflow()), JsonSerializerOptions.Web);
        var bad = await client.PostAsJsonAsync("/v1/design/quality", new { schema = (object?)null }, JsonSerializerOptions.Web);

        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var body = await ok.Content.ReadAsStringAsync();
        var passed = JsonSerializer.Deserialize<DraftQualityResult>(body, JsonSerializerOptions.Web)!;
        Assert.True(passed.Passed);
        Assert.Contains(passed.Report.Cases, c => c.Category == StructuralCategory.Repetition);
        Assert.Contains(passed.Report.Cases, c => c.Category == StructuralCategory.Invalid && c.Passed);
        Assert.DoesNotContain("\"data\"", body);
        Assert.Contains("\"Repetition\"", body); // categorias como texto no contrato

        var failed = (await defective.Content.ReadFromJsonAsync<DraftQualityResult>(JsonSerializerOptions.Web))!;
        Assert.False(failed.Passed);
        Assert.Contains(failed.Report.Cases, c => !c.Passed && c.DestinationXPath!.Contains("serie"));

        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }
}

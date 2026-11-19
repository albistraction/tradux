using Tradux.Application.Generation;
using Tradux.Contracts;
using Tradux.Domain.Mapping;
using Tradux.Tests.Shared;

namespace Tradux.Application.Tests;

/// <summary>
/// <see cref="XmlGenerationService"/> sobre snapshots publicados, sem MongoDB:
/// falha de compilação fora do cache, entrada inválida e geração concorrente.
/// </summary>
public class XmlGenerationServiceTests
{
    private const string Dps = "dps";
    private const string Pedido = "pedido";

    private static TemplateKey KeyOf(string templateId) => new(SnapshotFixtures.Organization, templateId, SnapshotFixtures.Operation);

    private static async Task<TestServices> ServicesWithPublishedAsync()
    {
        var services = new TestServices();
        await services.PublishAsync(KeyOf(Dps), SnapshotFixtures.SimpleDps(), SnapshotFixtures.ScenariosFor(SnapshotFixtures.SimpleDps()));
        await services.PublishAsync(KeyOf(Pedido), SnapshotFixtures.PedidoComItens(), SnapshotFixtures.ScenariosFor(SnapshotFixtures.PedidoComItens()));
        return services;
    }

    private static GenerateXmlRequest JsonRequest(string templateId, System.Text.Json.JsonElement data) =>
        new(SnapshotFixtures.Organization, templateId, SnapshotFixtures.Operation, data) { CorrelationId = "corr-1" };

    [Fact]
    public async Task Json_object_where_a_collection_is_expected_is_a_diagnostic_not_bogus_items()
    {
        // ADR-0020: objeto no lugar da lista só vale com SingleObjectAsItem; sem ele, erro claro que diz como ligar.
        var services = await ServicesWithPublishedAsync();
        var data = SnapshotFixtures.Json("""{ "Numero": "PED-001", "ValorTotal": 1, "Itens": { "Descricao": "x", "ValorUnitario": 1 } }""");

        var result = await services.Generation.GenerateAsync(JsonRequest(Pedido, data));

        Assert.False(result.Success);
        var diagnostic = Assert.Single(result.Diagnostics, d => d.Code == "COLLECTION_SOURCE_NOT_ENUMERABLE");
        Assert.Contains("1 item", diagnostic.Suggestion, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Date_literals_persisted_as_text_still_compare_as_dates_after_publication()
    {
        var services = new TestServices();
        await services.PublishAsync(KeyOf("dps-datas"), SnapshotFixtures.SimpleDpsWithDateRule(), SnapshotFixtures.ScenariosFor(SnapshotFixtures.SimpleDpsWithDateRule()));

        var result = await services.Generation.GenerateAsync(JsonRequest("dps-datas", SnapshotFixtures.DpsJson()));

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Code)));
        Assert.Equal(SnapshotFixtures.DateRuleExpectedText,
            System.Xml.Linq.XDocument.Parse(result.Xml!).Root!.Elements().Single(e => e.Name.LocalName == "discriminacaoServico").Value);
    }

    [Fact]
    public async Task Non_object_json_data_is_rejected_as_argument_error()
    {
        var services = await ServicesWithPublishedAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => services.Generation.GenerateAsync(JsonRequest(Dps, SnapshotFixtures.Json("[1, 2]"))));
    }

    [Fact]
    public async Task Missing_correlation_id_is_generated()
    {
        var services = await ServicesWithPublishedAsync();

        var result = await services.Generation.GenerateAsync(JsonRequest(Dps, SnapshotFixtures.DpsJson()) with { CorrelationId = null });

        Assert.False(string.IsNullOrWhiteSpace(result.CorrelationId));
    }

    [Fact]
    public async Task Process_without_a_custom_operator_reports_compilation_failure_instead_of_crashing()
    {
        var repository = new InMemorySnapshotRepository();
        var publisher = new TestServices(repository, SnapshotPublisherTests.CatalogWithIdentityOperator());
        var content = SnapshotFixtures.SimpleDps() with
        {
            Mapping = new MappingTemplate
            {
                Fields = SnapshotFixtures.SimpleDps().Mapping.Fields
                    .Select(f => f.DestinationElementName == "numero" ? new FieldMapping("numero", OperatorNode.Of("identidade", new FieldReferenceNode("Numero"))) : f)
                    .ToList(),
            },
        };
        await publisher.PublishAsync(KeyOf(Dps), content, SnapshotFixtures.ScenariosFor(content));
        Assert.True((await publisher.Generation.GenerateAsync(JsonRequest(Dps, SnapshotFixtures.DpsJson()))).Success);

        var withDefaultCatalog = new TestServices(repository).Generation;
        var first = await withDefaultCatalog.GenerateAsync(JsonRequest(Dps, SnapshotFixtures.DpsJson()));
        var readsAfterFirst = repository.ContentReads;
        var second = await withDefaultCatalog.GenerateAsync(JsonRequest(Dps, SnapshotFixtures.DpsJson()));

        Assert.False(first.Success);
        Assert.Equal(XmlGenerationService.SnapshotCompilationFailedCode, Assert.Single(first.Diagnostics).Code);
        Assert.False(second.Success);
        Assert.True(repository.ContentReads > readsAfterFirst, "Falha de compilação não pode ficar em cache.");
    }

    [Fact]
    public async Task Concurrent_generations_on_one_cached_snapshot_give_independent_correct_results()
    {
        var services = await ServicesWithPublishedAsync();
        var valid = SnapshotFixtures.DpsJson();
        var invalid = SnapshotFixtures.DpsJson(cnpjPrestador: "123");
        var expectedXml = (await services.Generation.GenerateAsync(JsonRequest(Dps, valid))).Xml;

        var results = await Task.WhenAll(Enumerable.Range(0, 200).Select(i => Task.Run(async () =>
            (IsValid: i % 2 == 0, Result: await services.Generation.GenerateAsync(JsonRequest(Dps, i % 2 == 0 ? valid : invalid))))));

        Assert.All(results, r =>
        {
            Assert.Equal(r.IsValid, r.Result.Success);
            if (r.IsValid)
            {
                Assert.True(SnapshotFixtures.XmlSemanticallyEqual(expectedXml, r.Result.Xml));
                Assert.DoesNotContain(r.Result.Diagnostics, d => d.Code == "XSD_VALIDATION");
            }
            else
            {
                Assert.Contains(r.Result.Diagnostics, d => d.Code == "XSD_VALIDATION");
            }
        });
    }
}

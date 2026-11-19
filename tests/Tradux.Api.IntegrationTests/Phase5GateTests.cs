using Tradux.Api.IntegrationTests.Infrastructure;
using Tradux.Tests.Shared;

namespace Tradux.Api.IntegrationTests;

/// <summary>
/// Gate da Fase 5 (docs/ROADMAP.md): "execução com cache aquecido não acessa
/// MongoDB" e publicação feita por outra instância chega ao cache da API —
/// provado contra a API real (WebApplicationFactory) e um <c>mongod</c> real.
/// </summary>
[Collection(MongoCollection.Name)]
public class Phase5GateTests(MongoFixture mongo)
{
    private const string Dps = "dps";

    [Fact]
    public async Task Warm_cache_generation_sends_no_command_to_mongodb()
    {
        // Invalidação desligada: o Change Stream (getMore contínuo) e o poll
        // de segurança são tráfego de fundo legítimo, fora do caminho de
        // geração — aqui medimos só o que as requisições de geração causam.
        await using var factory = new ApiFactory(mongo, invalidationEnabled: false);
        await factory.CreateDirectPublisher().PublishAsync(SnapshotFixtures.Organization, Dps, SnapshotFixtures.Operation, SnapshotFixtures.SimpleDps(), SnapshotFixtures.ScenariosFor(SnapshotFixtures.SimpleDps()), "teste");
        var client = factory.CreateClient();

        factory.Recorder.Clear();
        var cold = await (await client.PostGenerateAsync(SnapshotFixtures.Organization, Dps, SnapshotFixtures.Operation, SnapshotFixtures.DpsJson())).ReadResultAsync();
        var coldCommands = factory.Recorder.Commands;

        factory.Recorder.Clear();
        for (var i = 0; i < 50; i++)
        {
            var warm = await (await client.PostGenerateAsync(
                SnapshotFixtures.Organization, Dps, SnapshotFixtures.Operation,
                i % 2 == 0 ? SnapshotFixtures.DpsJson() : SnapshotFixtures.DpsJson(cnpjPrestador: "123"),
                version: i % 3 == 0 ? 1 : null)).ReadResultAsync();
            Assert.Equal(i % 2 == 0, warm.Success);
        }

        Assert.True(cold.Success);
        Assert.Contains(coldCommands, c => c.EndsWith(".find", StringComparison.Ordinal));
        Assert.Empty(factory.Recorder.Commands);
    }

    [Fact]
    public async Task Publication_by_another_instance_reaches_the_warm_api_cache_through_change_streams()
    {
        await using var factory = new ApiFactory(mongo, invalidationEnabled: true);
        var otherInstance = factory.CreateDirectPublisher();
        await otherInstance.PublishAsync(SnapshotFixtures.Organization, Dps, SnapshotFixtures.Operation, SnapshotFixtures.SimpleDps(), SnapshotFixtures.ScenariosFor(SnapshotFixtures.SimpleDps()), "teste");
        var client = factory.CreateClient();

        var before = await (await client.PostGenerateAsync(SnapshotFixtures.Organization, Dps, SnapshotFixtures.Operation, SnapshotFixtures.DpsJson())).ReadResultAsync();
        Assert.Equal("1", before.VersionUsed);

        await otherInstance.PublishAsync(SnapshotFixtures.Organization, Dps, SnapshotFixtures.Operation, SnapshotFixtures.SimpleDps("V2-"), SnapshotFixtures.ScenariosFor(SnapshotFixtures.SimpleDps("V2-")), "teste");

        // Poll de segurança configurado para 1h na fábrica: se a versão nova
        // aparecer, foi o Change Stream.
        var after = await Eventually.PollAsync(
            async () => await (await client.PostGenerateAsync(SnapshotFixtures.Organization, Dps, SnapshotFixtures.Operation, SnapshotFixtures.DpsJson())).ReadResultAsync(),
            r => r.VersionUsed == "2");

        Assert.Equal("2", after.VersionUsed);
        Assert.Contains("<numero>V2-000123</numero>", after.Xml);
    }
}

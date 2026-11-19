using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Tradux.Api.IntegrationTests.Infrastructure;
using Tradux.Tests.Shared;
using Xunit.Abstractions;

namespace Tradux.Api.IntegrationTests;

/// <summary>
/// Gate da Fase 11 — concorrência, invalidação, falha de dependência e
/// limites de entrada (ADR-0014 §3–§4).
/// </summary>
[Collection(MongoCollection.Name)]
public sealed class Phase11ResilienceTests(MongoFixture mongo, ITestOutputHelper output)
{
    private const string Dps = "dps";
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>Defasagem máxima aceita entre publicar e toda instância servir a versão nova (Change Stream).</summary>
    private static readonly TimeSpan ConvergenceBound = TestTiming.Scale(TimeSpan.FromSeconds(3));

    private static string Template(string templateId = Dps) =>
        $"/v1/organizations/{SnapshotFixtures.Organization}/templates/{templateId}/operations/{SnapshotFixtures.Operation}";

    private sealed record Observation(int Instance, TimeSpan StartedAt, bool Success, int? Version, bool V2Content)
    {
        public TimeSpan CompletedAt { get; init; }
    }

    private static async Task<Observation> GenerateAsync(HttpClient client, int instance, Stopwatch clock)
    {
        var started = clock.Elapsed;
        using var response = await client.PostAsJsonAsync($"{Template()}/xml", new { data = SnapshotFixtures.DpsJson() }, Web);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            return new Observation(instance, started, false, null, false) { CompletedAt = clock.Elapsed };
        }

        var result = await response.Content.ReadFromJsonAsync<JsonElement>(Web);
        var version = result.GetProperty("versionUsed").GetString() is { } label ? int.Parse(label, CultureInfo.InvariantCulture) : (int?)null;
        return new Observation(instance, started, result.GetProperty("success").GetBoolean(), version, result.GetProperty("xml").GetString()!.Contains(">V2-", StringComparison.Ordinal))
        {
            CompletedAt = clock.Elapsed,
        };
    }

    [Fact]
    public async Task Publication_and_rollback_under_load_switch_versions_atomically_on_every_instance()
    {
        // Duas instâncias da mesma implantação (mesmo banco), cada uma com seu cache e seu Change Stream.
        await using var first = new ApiFactory(mongo, kestrel: true);
        await using var second = new ApiFactory(mongo, kestrel: true, sharedDatabase: first.MongoOptions);
        var publisher = first.CreateDirectPublisher();
        await publisher.PublishAsync(SnapshotFixtures.Organization, Dps, SnapshotFixtures.Operation, SnapshotFixtures.SimpleDps(), SnapshotFixtures.DpsScenarios(), "teste");
        HttpClient[] clients = [first.CreateClient(ApiFactory.RuntimeA), second.CreateClient(ApiFactory.RuntimeA)];
        var clock = Stopwatch.StartNew();
        foreach (var (client, instance) in clients.Select((c, i) => (c, i)))
        {
            for (var i = 0; i < 20; i++)
            {
                Assert.Equal(1, (await GenerateAsync(client, instance, clock)).Version);
            }
        }

        // Carga contínua nas duas instâncias (~4 clientes cada) durante publicação e rollback.
        var observations = new ConcurrentBag<Observation>();
        using var stop = new CancellationTokenSource();
        var load = Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(async () =>
        {
            var instance = worker % 2;
            while (!stop.IsCancellationRequested)
            {
                observations.Add(await GenerateAsync(clients[instance], instance, clock));
                await Task.Delay(5);
            }
        })));

        await Task.Delay(TimeSpan.FromSeconds(1));
        // "Antes" = antes de CHAMAR a publicação; "depois" = quando ela retornou.
        var publishing = clock.Elapsed;
        var v2 = await publisher.PublishAsync(SnapshotFixtures.Organization, Dps, SnapshotFixtures.Operation, SnapshotFixtures.SimpleDps("V2-"), SnapshotFixtures.DpsScenarios(), "teste");
        var published = clock.Elapsed;
        Assert.Equal(2, v2.SnapshotVersion);

        await Task.Delay(ConvergenceBound + TimeSpan.FromSeconds(2));
        var rollingBack = clock.Elapsed;
        var rollback = await first.CreateClient().PostAsJsonAsync($"{Template()}/rollback", new { version = 1 }, Web);
        var rolledBack = clock.Elapsed;
        Assert.Equal(HttpStatusCode.OK, rollback.StatusCode);

        await Task.Delay(ConvergenceBound + TimeSpan.FromSeconds(2));
        await stop.CancelAsync();
        await load;

        var all = observations.ToList();
        Assert.True(all.Count > 200, $"só {all.Count} gerações durante o teste");
        Assert.All(all, o => Assert.True(o.Success, $"falha na instância {o.Instance} em {o.StartedAt}"));
        // Atomicidade: o conteúdo e a versão informada são sempre da MESMA versão.
        Assert.All(all, o => Assert.Equal(o.Version == 2, o.V2Content));
        Assert.All(all.Where(o => o.StartedAt < publishing), o => Assert.Equal(1, o.Version));

        for (var instance = 0; instance < clients.Length; instance++)
        {
            var mine = all.Where(o => o.Instance == instance).OrderBy(o => o.StartedAt).ToList();
            Assert.All(mine.Where(o => o.StartedAt >= published + ConvergenceBound && o.StartedAt < rollingBack), o => Assert.Equal(2, o.Version));
            Assert.All(mine.Where(o => o.StartedAt >= rolledBack + ConvergenceBound), o => Assert.Equal(1, o.Version));

            // Sem ida e volta: depois que uma resposta com a v2 TERMINOU, todo pedido
            // iniciado em seguida (e antes do rollback) recebe a v2 — a troca no cache é única.
            var firstV2Done = mine.Where(o => o.Version == 2).Min(o => o.CompletedAt);
            Assert.All(mine.Where(o => o.StartedAt > firstV2Done && o.StartedAt < rollingBack), o => Assert.Equal(2, o.Version));
            var firstV2 = mine.First(o => o.Version == 2).StartedAt;
            var back = mine.FirstOrDefault(o => o.StartedAt > rollingBack && o.Version == 1);
            Assert.True(back is not null, $"instância {instance} não voltou à v1 depois do rollback");
            var firstBack = back.StartedAt;
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"instância {instance}: v2 {(firstV2 - published).TotalMilliseconds:0} ms após o fim da publicação; v1 {(firstBack - rolledBack).TotalMilliseconds:0} ms após o fim do rollback"));
        }

        output.WriteLine($"{all.Count} gerações, todas com conteúdo coerente com a versão informada.");
    }

    [Fact]
    public async Task Warm_keys_keep_generating_with_mongodb_down_and_cold_keys_fail_fast_with_503()
    {
        // mongod próprio: este teste o derruba.
        var own = new MongoFixture();
        await own.InitializeAsync();
        var mongoDown = false;
        try
        {
            await using var factory = new ApiFactory(own, serverSelectionTimeout: TimeSpan.FromSeconds(2),
                settings: new Dictionary<string, string> { ["Tradux:Security:CredentialCacheTtl"] = "00:00:03" });
            var publisher = factory.CreateDirectPublisher();
            await publisher.PublishAsync(SnapshotFixtures.Organization, Dps, SnapshotFixtures.Operation, SnapshotFixtures.SimpleDps(), SnapshotFixtures.DpsScenarios(), "teste");
            await publisher.PublishAsync(SnapshotFixtures.Organization, "dps-fria", SnapshotFixtures.Operation, SnapshotFixtures.SimpleDps(), SnapshotFixtures.DpsScenarios(), "teste");
            var runtime = factory.CreateClient(ApiFactory.RuntimeA);
            var anonymous = factory.CreateClient(secret: null);
            var clock = Stopwatch.StartNew();
            Assert.True((await GenerateAsync(runtime, 0, clock)).Success);
            Assert.Equal("Healthy", await anonymous.GetStringAsync("/health/ready"));
            Assert.Equal("Healthy", await anonymous.GetStringAsync("/health/live"));

            await own.DisposeAsync();
            mongoDown = true;
            await Task.Delay(TimeSpan.FromSeconds(3.5)); // cache de credencial (3 s) vencido: vale a carência

            // Chave aquecida: segue gerando sem MongoDB. Só o 1º pedido após o vencimento
            // da credencial paga a tentativa no armazenamento (~2 s); os seguintes não.
            var first = await GenerateAsync(runtime, 0, clock);
            Assert.True(first.Success && first.Version == 1, "chave aquecida deveria gerar com o MongoDB fora");
            var burst = Stopwatch.StartNew();
            for (var i = 0; i < 20; i++)
            {
                var warm = await GenerateAsync(runtime, 0, clock);
                Assert.True(warm.Success && warm.Version == 1, "chave aquecida deveria gerar com o MongoDB fora");
            }

            Assert.True(burst.Elapsed < TestTiming.Scale(TimeSpan.FromSeconds(2)), $"20 gerações aquecidas levaram {burst.Elapsed} com o MongoDB fora");
            output.WriteLine($"MongoDB fora: 1º pedido após o vencimento da credencial {first.CompletedAt - first.StartedAt}; 20 seguintes em {burst.Elapsed}.");

            // Chave fria: 503 com Retry-After, rápido (seleção de servidor de 2 s), sem detalhe interno.
            var started = Stopwatch.StartNew();
            using var cold = await runtime.PostAsJsonAsync($"{Template("dps-fria")}/xml", new { data = SnapshotFixtures.DpsJson() }, Web);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, cold.StatusCode);
            Assert.True(started.Elapsed < TimeSpan.FromSeconds(10), $"chave fria levou {started.Elapsed}");
            Assert.Equal("5", cold.Headers.GetValues("Retry-After").Single());
            var problem = await cold.Content.ReadAsStringAsync();
            Assert.DoesNotContain("mongodb://", problem, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Timeout", problem, StringComparison.Ordinal);

            // Saúde: vivo; pronto em Degraded (continua no balanceador).
            using var ready = await anonymous.GetAsync("/health/ready");
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
            Assert.Equal("Degraded", await ready.Content.ReadAsStringAsync());
            Assert.Equal("Healthy", await anonymous.GetStringAsync("/health/live"));
        }
        finally
        {
            if (!mongoDown)
            {
                await own.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task Input_limits_are_enforced_over_http_without_echoing_the_data()
    {
        await using var factory = new ApiFactory(mongo);
        await factory.CreateDirectPublisher().PublishAsync(SnapshotFixtures.Organization, Dps, SnapshotFixtures.Operation, SnapshotFixtures.SimpleDps(), SnapshotFixtures.DpsScenarios(), "teste");
        var runtime = factory.CreateClient(ApiFactory.RuntimeA);
        var route = $"{Template()}/xml";

        async Task<(HttpStatusCode Status, string Body)> Post(string json)
        {
            using var response = await runtime.PostAsync(route, new StringContent(json, Encoding.UTF8, "application/json"));
            return (response.StatusCode, await response.Content.ReadAsStringAsync());
        }

        var deep = "{\"data\":" + string.Concat(Enumerable.Repeat("{\"a\":", 40)) + "1" + new string('}', 40) + "}";
        var wide = "{\"data\":{\"itens\":[" + string.Join(",", Enumerable.Range(0, 10_001)) + "]}}";
        var secret = "SEGREDO-FISCAL-" + new string('9', 10_000);
        var longText = "{\"data\":{\"Descricao\":\"" + secret + "\"}}";
        var huge = "{\"data\":{\"Descricao\":\"" + new string('x', 1_100_000) + "\"}}";

        foreach (var (json, fragment) in new[] { (deep, "profundidade"), (wide, "nós"), (longText, "caracteres") })
        {
            var (status, body) = await Post(json);
            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.Contains(fragment, body, StringComparison.Ordinal);
            Assert.DoesNotContain("SEGREDO-FISCAL", body, StringComparison.Ordinal);
        }

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await Post(huge)).Status);

        // O limite vale para o endpoint, não para a grafia do caminho (revisão B1).
        foreach (var variant in new[] { "/XML", "/Xml", "/xml/" })
        {
            using var response = await runtime.PostAsync(Template() + variant, new StringContent(huge, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        }
        Assert.Equal(HttpStatusCode.OK, (await Post(JsonSerializer.Serialize(new { data = SnapshotFixtures.DpsJson() }, Web))).Status);
    }
}

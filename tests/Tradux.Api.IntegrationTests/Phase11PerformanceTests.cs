using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Tradux.Api.IntegrationTests.Infrastructure;
using Tradux.Application.Snapshots;
using Tradux.Domain.SourceData;
using Tradux.Engine;
using Tradux.Tests.Shared;
using Xunit.Abstractions;

namespace Tradux.Api.IntegrationTests;

/// <summary>
/// Gate da Fase 11 — carga (ADR-0014 §1–§2). API real sobre Kestrel com
/// sockets reais, MongoDB real, credencial de runtime, cache aquecido.
/// Janela medida: 15 s na taxa real por padrão; <c>TRADUX_LOAD_SECONDS=60</c>
/// para a execução completa registrada em <c>docs/operations/PERFORMANCE.md</c>.
/// Relatórios JSON em <c>artifacts/perf/</c>.
/// </summary>
[Collection(MongoCollection.Name)]
public sealed class Phase11PerformanceTests(MongoFixture mongo, ITestOutputHelper output)
{
    private const string Dps = "dps";
    private const string SnapshotCollection = "compiledSnapshots";
    private const double MaxWorkingSetGrowthMb = 64;

    private static TimeSpan Window =>
        TimeSpan.FromSeconds(double.TryParse(Environment.GetEnvironmentVariable("TRADUX_LOAD_SECONDS"), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ? seconds : 15);

    private static readonly string Body = JsonSerializer.Serialize(new { data = SnapshotFixtures.DpsJson() }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static string XmlRoute(string templateId = Dps) =>
        $"/v1/organizations/{SnapshotFixtures.Organization}/templates/{templateId}/operations/{SnapshotFixtures.Operation}/xml";

    /// <summary>Envia uma geração e confere HTTP 200 + <c>success</c>.</summary>
    internal static Func<Task<bool>> Sender(HttpClient client, string route)
    {
        return async () =>
        {
            using var content = new StringContent(Body, Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(route, content);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
            return json.RootElement.GetProperty("success").GetBoolean();
        };
    }

    /// <summary>Instância pronta: template publicado, credencial validada e cache aquecido.</summary>
    private async Task<(ApiFactory Factory, Func<Task<bool>> Send)> WarmInstanceAsync()
    {
        // Sem Change Stream: a contagem de comandos fica só com o que a geração fizer.
        var factory = new ApiFactory(mongo, invalidationEnabled: false, kestrel: true);
        await factory.CreateDirectPublisher().PublishAsync(SnapshotFixtures.Organization, Dps, SnapshotFixtures.Operation, SnapshotFixtures.SimpleDps(), SnapshotFixtures.DpsScenarios(), "teste");
        var send = Sender(factory.CreateClient(ApiFactory.RuntimeA), XmlRoute());
        for (var i = 0; i < 100; i++)
        {
            Assert.True(await send(), "aquecimento falhou");
        }

        return (factory, send);
    }

    private void Record(LoadReport report, string name)
    {
        output.WriteLine(report.Summary());
        report.Save(name);
    }

    private static void AssertTargets(LoadReport report, double maxP99)
    {
        Assert.Equal(0, report.Errors);
        // Com menos de 100 amostras o p99 é o próprio máximo (uma pausa do SO reprova):
        // a janela curta da suíte confere o p95; a execução registrada (≥ 200 amostras) confere o p99.
        var (label, observed) = report.Requests >= 100 ? ("p99", report.P99) : ("p95", report.P95);
        Assert.True(observed <= maxP99 * TestTiming.Slack, $"{label} {observed:0.0} ms > {maxP99 * TestTiming.Slack} ms (n={report.Requests})");
        Assert.Equal(0, report.SnapshotCollectionCommands);
        Assert.Equal(0, report.CacheMisses);
        Assert.Equal(report.Requests, report.CacheHits);
        Assert.True(report.WorkingSetEnd - report.WorkingSetStart <= MaxWorkingSetGrowthMb,
            $"working set cresceu {report.WorkingSetEnd - report.WorkingSetStart:0} MB");
        // Taxa atingida dentro de 5% da alvo: o gerador não ficou para trás.
        Assert.True(report.AchievedPerMinute >= report.TargetPerMinute * (TestTiming.Slack > 1 ? 0.85 : 0.95), $"taxa {report.AchievedPerMinute:0}/min abaixo da alvo");
    }

    [Fact]
    public async Task Sustains_200_generations_per_minute_with_warm_cache_and_no_snapshot_reads()
    {
        var (factory, send) = await WarmInstanceAsync();
        await using var _ = factory;

        var report = await LoadRunner.OpenModelAsync("200/min", 200, Window, send, () => factory.Recorder.CountOn(SnapshotCollection));

        Record(report, "load-200-per-minute");
        AssertTargets(report, maxP99: 100);
    }

    [Fact]
    public async Task Sustains_the_2000_per_minute_margin_with_warm_cache_and_no_snapshot_reads()
    {
        var (factory, send) = await WarmInstanceAsync();
        await using var _ = factory;

        var report = await LoadRunner.OpenModelAsync("2000/min", 2000, Window, send, () => factory.Recorder.CountOn(SnapshotCollection));

        Record(report, "load-2000-per-minute");
        AssertTargets(report, maxP99: 250);
    }

    [Fact]
    public async Task One_instance_saturates_above_the_margin()
    {
        var (factory, send) = await WarmInstanceAsync();
        await using var _ = factory;

        var report = await LoadRunner.ClosedModelAsync("saturação", clients: 16, TimeSpan.FromSeconds(Math.Min(Window.TotalSeconds, 10)), send,
            () => factory.Recorder.CountOn(SnapshotCollection));

        Record(report, "load-saturation");
        Assert.Equal(0, report.Errors);
        Assert.True(report.AchievedPerMinute >= 2000, $"teto de {report.AchievedPerMinute:0}/min abaixo da margem de 2.000/min");
        Assert.Equal(0, report.SnapshotCollectionCommands);
    }

    /// <summary>Benchmarks separados (seção 25): compilação de snapshot e execução do motor, sem HTTP.</summary>
    [Fact]
    public void Compilation_and_execution_are_benchmarked_separately()
    {
        var compiler = new SnapshotCompiler();
        var content = SnapshotFixtures.SimpleDps();
        var data = SourceDataTree.FromJson(SnapshotFixtures.DpsJson());

        for (var i = 0; i < 3; i++)
        {
            _ = compiler.CompileContent(content);
        }

        var compile = Measure(30, () => compiler.CompileContent(content));
        var (_, template) = compiler.CompileContent(content);
        for (var i = 0; i < 200; i++)
        {
            Assert.True(XmlGenerationEngine.Generate(template, data).Success);
        }

        var execute = Measure(5000, () => XmlGenerationEngine.Generate(template, data));

        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"compilação: p50 {compile.P50:0.00} ms, p99 {compile.P99:0.00} ms (n={compile.Count}); execução: p50 {execute.P50:0.000} ms, p99 {execute.P99:0.000} ms (n={execute.Count})"));
        var directory = Path.Combine(RepositoryPaths.Root, "artifacts", "perf");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "benchmark-compile-execute.json"),
            JsonSerializer.Serialize(new { compile, execute, Environment.ProcessorCount }, new JsonSerializerOptions { WriteIndented = true }));

        // Execução tem de ser ordens de grandeza abaixo do orçamento por pedido.
        Assert.True(execute.P99 < 20, $"execução p99 {execute.P99:0.000} ms");
        Assert.True(compile.P99 < 2000, $"compilação p99 {compile.P99:0.0} ms");
    }

    internal sealed record Timing(int Count, double P50, double P90, double P99, double Max);

    private static Timing Measure(int iterations, Action action)
    {
        var samples = new double[iterations];
        for (var i = 0; i < iterations; i++)
        {
            var started = Stopwatch.GetTimestamp();
            action();
            samples[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }

        Array.Sort(samples);
        double At(double p) => samples[Math.Clamp((int)Math.Ceiling(p * iterations) - 1, 0, iterations - 1)];
        return new Timing(iterations, At(0.5), At(0.9), At(0.99), samples[^1]);
    }
}

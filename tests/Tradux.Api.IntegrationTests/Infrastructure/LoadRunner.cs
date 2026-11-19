using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text.Json;

namespace Tradux.Api.IntegrationTests.Infrastructure;

/// <summary>
/// Folga dos limiares de tempo em CI compartilhado (revisão da Fase 11, aviso 6):
/// com <c>CI=true</c> os limites de latência/convergência são multiplicados por 4.
/// As metas formais do ADR-0014 valem para a execução registrada em
/// <c>docs/operations/PERFORMANCE.md</c> (sem folga).
/// </summary>
public static class TestTiming
{
    public static double Slack { get; } =
        string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase) ? 4 : 1;

    public static TimeSpan Scale(TimeSpan value) => value * Slack;
}

/// <summary>Resultado de uma rodada de carga (ADR-0014 §1). Tempos em ms, memória em MB.</summary>
public sealed record LoadReport(
    string Scenario,
    string Model,
    double TargetPerMinute,
    double AchievedPerMinute,
    int Requests,
    int Errors,
    double DurationSeconds,
    double P50,
    double P90,
    double P95,
    double P99,
    double Max,
    double CpuPercentOfMachine,
    double WorkingSetStart,
    double WorkingSetPeak,
    double WorkingSetEnd,
    double ManagedHeapEnd,
    double AllocatedKbPerRequest,
    int Gen2Collections,
    long CacheHits,
    long CacheMisses,
    int SnapshotCollectionCommands,
    int ProcessorCount)
{
    public string Summary() => string.Create(CultureInfo.InvariantCulture,
        $"{Scenario} [{Model}]: {Requests} pedidos em {DurationSeconds:0.0}s = {AchievedPerMinute:0}/min (alvo {TargetPerMinute:0}/min), erros {Errors}; " +
        $"p50 {P50:0.0} ms, p90 {P90:0.0}, p95 {P95:0.0}, p99 {P99:0.0}, máx {Max:0.0}; CPU {CpuPercentOfMachine:0.0}% de {ProcessorCount} núcleos; " +
        $"working set {WorkingSetStart:0}→pico {WorkingSetPeak:0}→{WorkingSetEnd:0} MB, heap {ManagedHeapEnd:0} MB, {AllocatedKbPerRequest:0.0} KB alocados/pedido, gen2 {Gen2Collections}; " +
        $"cache {CacheHits} acertos/{CacheMisses} faltas; comandos MongoDB na coleção de snapshots {SnapshotCollectionCommands}");

    /// <summary>Grava em <c>artifacts/perf/{nome}.json</c> na raiz do repositório (fora do controle de versão).</summary>
    public void Save(string name)
    {
        var directory = Path.Combine(Tradux.Tests.Shared.RepositoryPaths.Root, "artifacts", "perf");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, name + ".json"), JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}

/// <summary>
/// Gerador de carga em processo. <see cref="OpenModelAsync"/> dispara na taxa
/// alvo sem esperar respostas (modelo aberto — fila aparece como latência);
/// <see cref="ClosedModelAsync"/> mede o teto com N clientes em laço.
/// CPU e alocação incluem o próprio gerador (mesmo processo): são limite
/// superior do custo do servidor. O MongoDB roda em outro processo.
/// </summary>
public static class LoadRunner
{
    public static async Task<LoadReport> OpenModelAsync(
        string scenario, double perMinute, TimeSpan duration, Func<Task<bool>> send, Func<int> mongoCommands)
    {
        var interval = TimeSpan.FromMinutes(1) / perMinute;
        var total = (int)Math.Round(duration / interval);
        using var probe = new ResourceProbe(mongoCommands);
        var samples = new ConcurrentBag<(double Ms, bool Ok)>();
        var inFlight = new List<Task>(total);
        var clock = Stopwatch.StartNew();
        for (var i = 0; i < total; i++)
        {
            // Agenda por tempo absoluto: atraso de um disparo não reduz a taxa média.
            var due = interval * i;
            while (clock.Elapsed < due)
            {
                var wait = due - clock.Elapsed;
                await Task.Delay(wait > TimeSpan.FromMilliseconds(2) ? wait - TimeSpan.FromMilliseconds(1) : TimeSpan.FromMilliseconds(1));
            }

            inFlight.Add(Task.Run(() => MeasureAsync(send, samples)));
        }

        await Task.WhenAll(inFlight);
        return probe.Finish(scenario, "aberto", perMinute, clock.Elapsed, samples);
    }

    public static async Task<LoadReport> ClosedModelAsync(
        string scenario, int clients, TimeSpan duration, Func<Task<bool>> send, Func<int> mongoCommands)
    {
        using var probe = new ResourceProbe(mongoCommands);
        var samples = new ConcurrentBag<(double Ms, bool Ok)>();
        var clock = Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(0, clients).Select(_ => Task.Run(async () =>
        {
            while (clock.Elapsed < duration)
            {
                await MeasureAsync(send, samples);
            }
        })));
        return probe.Finish(scenario, $"fechado, {clients} clientes", 0, clock.Elapsed, samples);
    }

    private static async Task MeasureAsync(Func<Task<bool>> send, ConcurrentBag<(double Ms, bool Ok)> samples)
    {
        var started = Stopwatch.GetTimestamp();
        bool ok;
        try
        {
            ok = await send();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            ok = false;
        }

        samples.Add((Stopwatch.GetElapsedTime(started).TotalMilliseconds, ok));
    }

    private static double Percentile(IReadOnlyList<double> sorted, double p) =>
        sorted.Count == 0 ? 0 : sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Count) - 1, 0, sorted.Count - 1)];

    /// <summary>CPU, memória, GC, cache (telemetria da Fase 9) e comandos do MongoDB durante a janela.</summary>
    private sealed class ResourceProbe : IDisposable
    {
        private readonly Func<int> _mongoCommands;
        private readonly int _mongoStart;
        private readonly Process _process = Process.GetCurrentProcess();
        private readonly TimeSpan _cpuStart;
        private readonly long _allocatedStart = GC.GetTotalAllocatedBytes();
        private readonly int _gen2Start = GC.CollectionCount(2);
        private readonly double _workingSetStart;
        private readonly Timer _sampler;
        private readonly MeterListener _meters = new();
        private double _workingSetPeak;
        private long _hits;
        private long _misses;

        public ResourceProbe(Func<int> mongoCommands)
        {
            _mongoCommands = mongoCommands;
            _mongoStart = mongoCommands();
            _process.Refresh();
            _cpuStart = _process.TotalProcessorTime;
            _workingSetStart = _workingSetPeak = _process.WorkingSet64 / 1048576.0;
            _sampler = new Timer(_ =>
            {
                lock (_process)
                {
                    _process.Refresh();
                    _workingSetPeak = Math.Max(_workingSetPeak, _process.WorkingSet64 / 1048576.0);
                }
            }, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(200));

            _meters.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == Tradux.Application.Observability.TraduxTelemetry.SourceName && instrument.Name == "tradux.cache.lookups")
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _meters.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                foreach (var tag in tags)
                {
                    if (tag.Key == "tradux.cache.hit")
                    {
                        if (tag.Value is true)
                        {
                            Interlocked.Add(ref _hits, value);
                        }
                        else
                        {
                            Interlocked.Add(ref _misses, value);
                        }
                    }
                }
            });
            _meters.Start();
        }

        public LoadReport Finish(string scenario, string model, double target, TimeSpan elapsed, IEnumerable<(double Ms, bool Ok)> samples)
        {
            _sampler.Dispose();
            _meters.Dispose();
            double workingSetEnd;
            TimeSpan cpu;
            lock (_process)
            {
                _process.Refresh();
                workingSetEnd = _process.WorkingSet64 / 1048576.0;
                _workingSetPeak = Math.Max(_workingSetPeak, workingSetEnd);
                cpu = _process.TotalProcessorTime - _cpuStart;
            }

            var all = samples.ToList();
            var latencies = all.Select(s => s.Ms).Order().ToList();
            return new LoadReport(
                scenario, model, target,
                all.Count / elapsed.TotalMinutes, all.Count, all.Count(s => !s.Ok), elapsed.TotalSeconds,
                Percentile(latencies, 0.50), Percentile(latencies, 0.90), Percentile(latencies, 0.95), Percentile(latencies, 0.99), latencies.LastOrDefault(),
                100.0 * cpu.TotalMilliseconds / (elapsed.TotalMilliseconds * Environment.ProcessorCount),
                _workingSetStart, _workingSetPeak, workingSetEnd, GC.GetTotalMemory(false) / 1048576.0,
                all.Count == 0 ? 0 : (GC.GetTotalAllocatedBytes() - _allocatedStart) / 1024.0 / all.Count,
                GC.CollectionCount(2) - _gen2Start,
                Interlocked.Read(ref _hits), Interlocked.Read(ref _misses),
                _mongoCommands() - _mongoStart,
                Environment.ProcessorCount);
        }

        public void Dispose()
        {
            _sampler.Dispose();
            _meters.Dispose();
        }
    }
}

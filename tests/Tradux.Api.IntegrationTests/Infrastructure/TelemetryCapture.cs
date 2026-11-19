using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace Tradux.Api.IntegrationTests.Infrastructure;

/// <summary>
/// Captura tudo o que a API emite como telemetria — logs (todos os níveis a
/// partir de Debug, com mensagem formatada, valores estruturados e exceção),
/// tags de spans e tags/valores de métricas — para o gate da Fase 9 provar
/// que nenhum valor fiscal aparece ali.
/// </summary>
public sealed class TelemetryCapture : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _entries = new();
    private readonly ActivityListener _activities;
    private readonly MeterListener _meters;

    public TelemetryCapture()
    {
        _activities = new ActivityListener
        {
            ShouldListenTo = source => source.Name.StartsWith("Tradux", StringComparison.Ordinal) || source.Name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                _entries.Enqueue($"span {activity.OperationName} {activity.DisplayName} {activity.StatusDescription}");
                foreach (var tag in activity.TagObjects)
                {
                    _entries.Enqueue($"tag {tag.Key}={tag.Value}");
                }

                foreach (var e in activity.Events)
                {
                    _entries.Enqueue($"event {e.Name} " + string.Join(";", e.Tags.Select(t => $"{t.Key}={t.Value}")));
                }
            },
        };
        ActivitySource.AddActivityListener(_activities);

        _meters = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name.StartsWith("Tradux", StringComparison.Ordinal) || instrument.Meter.Name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        _meters.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Record(instrument, value, tags));
        _meters.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Record(instrument, value, tags));
        _meters.SetMeasurementEventCallback<int>((instrument, value, tags, _) => Record(instrument, value, tags));
        _meters.Start();
    }

    public IReadOnlyList<string> Entries => _entries.ToList();

    public bool Contains(string fragment) => _entries.Any(e => e.Contains(fragment, StringComparison.Ordinal));

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _entries);

    public void Dispose()
    {
        _activities.Dispose();
        _meters.Dispose();
    }

    private void Record<T>(Instrument instrument, T value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var text = $"metric {instrument.Name}={value} ";
        foreach (var tag in tags)
        {
            text += $"{tag.Key}={tag.Value};";
        }

        _entries.Enqueue(text);
    }

    private sealed class Logger(string category, ConcurrentQueue<string> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            entries.Enqueue($"scope {category} {state}");
            return null;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var values = state is IEnumerable<KeyValuePair<string, object?>> pairs ? string.Join(";", pairs.Select(p => $"{p.Key}={p.Value}")) : "";
            entries.Enqueue($"log {logLevel} {category} {formatter(state, exception)} | {values} | {exception}");
        }
    }
}

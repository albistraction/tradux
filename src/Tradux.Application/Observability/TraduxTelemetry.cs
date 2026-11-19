using System.Diagnostics;
using System.Diagnostics.Metrics;
using Tradux.Contracts;

namespace Tradux.Application.Observability;

/// <summary>
/// Telemetria do motor (seção 23, ADR-0012 §7) com os tipos da BCL
/// (<see cref="ActivitySource"/>/<see cref="Meter"/>) — o host liga o
/// OpenTelemetry; aqui não há pacote nem exportador. Só metadados: nunca
/// dados de entrada, XML ou mensagens de diagnóstico (que podem citar
/// valores) — erros vão como códigos estáveis.
/// </summary>
public static class TraduxTelemetry
{
    public const string SourceName = "Tradux";

    public static readonly ActivitySource Source = new(SourceName);

    private static readonly Meter Meter = new(SourceName);

    private static readonly Counter<long> Generations = Meter.CreateCounter<long>("tradux.generation.count", description: "Gerações de XML por resultado.");
    private static readonly Histogram<double> GenerationDuration = Meter.CreateHistogram<double>("tradux.generation.duration", unit: "ms", description: "Duração da geração (incluindo validação XSD).");
    private static readonly Counter<long> CacheLookups = Meter.CreateCounter<long>("tradux.cache.lookups", description: "Consultas ao cache de snapshots compilados por resultado (hit/miss).");
    private static readonly Counter<long> Publications = Meter.CreateCounter<long>("tradux.publication.count", description: "Tentativas de publicação por resultado.");

    public static Activity? StartGeneration(TemplateKey key, bool fallback, int? requestedVersion)
    {
        var activity = Source.StartActivity("tradux.generate", ActivityKind.Internal);
        activity?.SetTag("tradux.organization", key.OrganizationId);
        activity?.SetTag("tradux.template", key.TemplateId);
        activity?.SetTag("tradux.operation", key.Operation);
        activity?.SetTag("tradux.fallback", fallback);
        if (requestedVersion is { } version)
        {
            activity?.SetTag("tradux.version.requested", version);
        }

        return activity;
    }

    /// <summary>Fecha o span e registra métricas de uma geração.</summary>
    public static void CompleteGeneration(Activity? activity, TemplateKey key, bool fallback, GenerationResult result)
    {
        var errorCodes = ErrorCodes(result);
        if (activity is not null)
        {
            activity.SetTag("tradux.success", result.Success);
            activity.SetTag("tradux.version.used", result.VersionUsed);
            activity.SetTag("tradux.versions.attempted", string.Join(",", result.VersionsAttempted.Select(a => a.Version)));
            activity.SetTag("tradux.correlation_id", result.CorrelationId);
            activity.SetTag("tradux.duration_ms", result.Duration.TotalMilliseconds);
            if (errorCodes.Length > 0)
            {
                activity.SetTag("tradux.error_codes", errorCodes);
            }

            activity.SetStatus(result.Success ? ActivityStatusCode.Ok : ActivityStatusCode.Error, result.Success ? null : errorCodes);
        }

        var tags = new TagList
        {
            { "tradux.organization", key.OrganizationId },
            { "tradux.template", key.TemplateId },
            { "tradux.operation", key.Operation },
            { "tradux.fallback", fallback },
            { "tradux.success", result.Success },
        };
        Generations.Add(1, tags);
        GenerationDuration.Record(result.Duration.TotalMilliseconds, tags);
    }

    /// <summary>Resultado de consulta ao cache (marca também o span corrente).</summary>
    public static void CacheLookup(bool hit)
    {
        CacheLookups.Add(1, new KeyValuePair<string, object?>("tradux.cache.hit", hit));
        Activity.Current?.SetTag("tradux.cache.hit", hit);
    }

    public static void Publication(TemplateKey key, string path, bool success, IEnumerable<Diagnostic> failures)
    {
        using var activity = Source.StartActivity("tradux.publish", ActivityKind.Internal);
        activity?.SetTag("tradux.organization", key.OrganizationId);
        activity?.SetTag("tradux.template", key.TemplateId);
        activity?.SetTag("tradux.operation", key.Operation);
        activity?.SetTag("tradux.publication.path", path);
        activity?.SetTag("tradux.success", success);
        var codes = string.Join(",", failures.Select(f => f.Code).Distinct().Take(10));
        if (codes.Length > 0)
        {
            activity?.SetTag("tradux.error_codes", codes);
        }

        Publications.Add(1, new TagList { { "tradux.organization", key.OrganizationId }, { "tradux.publication.path", path }, { "tradux.success", success } });
    }

    /// <summary>Códigos de erro distintos (críticos), nunca mensagens.</summary>
    public static string ErrorCodes(GenerationResult result) =>
        string.Join(",", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Critical).Select(d => d.Code).Distinct().Take(10));
}

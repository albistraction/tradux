using Tradux.Application;
using Tradux.Application.Observability;
using Tradux.Contracts;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Tradux.Api;

/// <summary>
/// OpenTelemetry na API (seção 23, ADR-0012 §7): traces e métricas do motor
/// (<see cref="TraduxTelemetry"/>) e do ASP.NET Core, e logs estruturados. O
/// exportador OTLP só é ligado se <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> estiver
/// configurado. Nada de corpo de requisição, dado de entrada ou XML.
/// </summary>
public static partial class ApiObservability
{
    public static IServiceCollection AddTraduxObservability(this IServiceCollection services, IConfiguration configuration)
    {
        var otel = services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService("Tradux.Api"))
            .WithTracing(tracing => tracing
                .AddSource(TraduxTelemetry.SourceName)
                // Exceção não vira evento do span: mensagem de exceção pode citar dado.
                .AddAspNetCoreInstrumentation(options => options.RecordException = false))
            .WithMetrics(metrics => metrics
                .AddMeter(TraduxTelemetry.SourceName)
                .AddAspNetCoreInstrumentation())
            .WithLogging();

        if (!string.IsNullOrWhiteSpace(configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            otel.UseOtlpExporter();
        }

        return services;
    }

    /// <summary>Categoria dos registros de geração.</summary>
    public const string GenerationCategory = "Tradux.Api.Generation";

    public static ILogger GenerationLogger(this ILoggerFactory loggers) => loggers.CreateLogger(GenerationCategory);

    /// <summary>Um registro por geração: só metadados e códigos (seção 23).</summary>
    public static void LogGeneration(this ILogger logger, TemplateKey key, GenerationResult result, bool fallback) =>
        GenerationCompleted(
            logger,
            key.OrganizationId,
            key.TemplateId,
            key.Operation,
            result.VersionUsed,
            string.Join(",", result.VersionsAttempted.Select(a => a.Version)),
            fallback,
            result.Success,
            result.Duration.TotalMilliseconds,
            TraduxTelemetry.ErrorCodes(result),
            result.CorrelationId);

    [LoggerMessage(EventId = 1001, Level = LogLevel.Information,
        Message = "Geração {Organization}/{Template}/{Operation} versão={VersionUsed} tentadas=[{VersionsAttempted}] fallback={Fallback} sucesso={Success} duracaoMs={DurationMs} erros=[{ErrorCodes}] correlacao={CorrelationId}")]
    private static partial void GenerationCompleted(
        ILogger logger, string organization, string template, string operation, string? versionUsed, string versionsAttempted,
        bool fallback, bool success, double durationMs, string errorCodes, string? correlationId);
}

using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Tradux.Api;

/// <summary>
/// Saúde e falhas de dependência (ADR-0014 §4). <c>/health/live</c> = o
/// processo responde; <c>/health/ready</c> = MongoDB alcançável. MongoDB fora
/// deixa o ready em <c>Degraded</c> (200), não <c>Unhealthy</c>: a instância
/// continua servindo toda chave com cache aquecido.
/// </summary>
public static class HealthAndResilience
{
    public const string ReadyTag = "ready";

    /// <summary>Segundos sugeridos em <c>Retry-After</c> quando o MongoDB está indisponível.</summary>
    public const int RetryAfterSeconds = 5;

    public static IServiceCollection AddTraduxHealth(this IServiceCollection services)
    {
        services.AddHealthChecks()
            .AddCheck<MongoReadinessCheck>("mongodb", failureStatus: HealthStatus.Degraded, tags: [ReadyTag], timeout: TimeSpan.FromSeconds(2));
        return services;
    }

    public static IEndpointRouteBuilder MapTraduxHealth(this IEndpointRouteBuilder app)
    {
        // Só o status em texto — nenhum detalhe de dependência sai daqui.
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
        app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains(ReadyTag) }).AllowAnonymous();
        return app;
    }

    /// <summary>Falha de dependência (MongoDB fora/sem servidor elegível) → 503, não 500.</summary>
    public static bool IsDependencyUnavailable(Exception exception) =>
        exception is Tradux.Application.Security.CredentialStoreUnavailableException
        || Tradux.Infrastructure.MongoDb.MongoAvailability.IsUnavailable(exception);

    /// <summary>Todo 503 leva <c>Retry-After</c> (inclusive o gerado pelo tratador de exceções).</summary>
    public static IApplicationBuilder UseRetryAfterOnUnavailable(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                if (context.Response.StatusCode == StatusCodes.Status503ServiceUnavailable && !context.Response.Headers.ContainsKey("Retry-After"))
                {
                    context.Response.Headers.RetryAfter = RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }

                return Task.CompletedTask;
            });
            await next(context);
        });
}

/// <summary>Ping no MongoDB (comando administrativo, sem ler dado de negócio).</summary>
internal sealed class MongoReadinessCheck(IMongoClient client) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await client.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: cancellationToken).ConfigureAwait(false);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Sem mensagem da exceção (pode conter endereço/usuário da conexão).
            return new HealthCheckResult(context.Registration.FailureStatus, "MongoDB indisponível.");
        }
    }
}

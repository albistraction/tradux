using MongoDB.Driver;

namespace Tradux.Infrastructure.MongoDb;

/// <summary>Falhas do driver que significam "MongoDB indisponível agora" (ADR-0014 §4).</summary>
public static class MongoAvailability
{
    public static bool IsUnavailable(Exception exception) => exception switch
    {
        MongoConnectionException or MongoNotPrimaryException or MongoNodeIsRecoveringException => true,
        // Timeout de seleção de servidor do driver (não o de regex, que também é TimeoutException).
        TimeoutException and not System.Text.RegularExpressions.RegexMatchTimeoutException => true,
        _ => exception.InnerException is { } inner && IsUnavailable(inner),
    };
}

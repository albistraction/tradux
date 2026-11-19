using Tradux.Contracts;

namespace Tradux.Application.Ports;

/// <summary>Evento de auditoria (seção 20) — sem payload fiscal.</summary>
public sealed record AuditEvent(
    DateTimeOffset AtUtc,
    string OrganizationId,
    string Actor,
    string Action,
    string Target,
    string Outcome,
    IReadOnlyDictionary<string, string> Details);

/// <summary>Trilha de auditoria. Implementado por Infrastructure.MongoDb.</summary>
public interface IAuditSink
{
    Task RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AuditEvent>> ListAsync(string organizationId, int limit = 100, CancellationToken cancellationToken = default);
}

/// <summary>Grava eventos de auditoria com o relógio do serviço que audita.</summary>
public sealed class AuditTrail(IAuditSink sink, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public const string Success = "success";
    public const string Rejected = "rejected";

    public Task RecordAsync(
        string organizationId, string actor, string action, string target, string outcome, Dictionary<string, string> details, CancellationToken cancellationToken) =>
        sink.RecordAsync(new AuditEvent(_time.GetUtcNow(), organizationId, actor, action, target, outcome, details), cancellationToken);

    public Task RecordAsync(TemplateKey key, string actor, string action, string outcome, Dictionary<string, string> details, CancellationToken cancellationToken) =>
        RecordAsync(key.OrganizationId, actor, action, key.ToString(), outcome, details, cancellationToken);

    /// <summary>Resumo de falhas para a auditoria: até 10, só código e XPath — nunca valores.</summary>
    public static string Failures(IEnumerable<Diagnostic> failures) =>
        string.Join("; ", failures.Take(10).Select(f => $"{f.Code}@{f.DestinationXPath}"));
}

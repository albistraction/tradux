using System.Globalization;
using Tradux.Application.Ports;
using Tradux.Application.Snapshots;
using Tradux.Application.Testing;
using Tradux.Contracts;

namespace Tradux.Application.Publishing;

/// <summary>
/// Publicação de um conteúdo completo (sem camadas): o caminho do editor e
/// do cadastro de template do microsserviço. Mesmos gates da publicação por
/// camadas, auditada e condicionada à versão ativa lida (ADR-0011 §5).
/// Falha nunca altera a versão ativa.
/// </summary>
public sealed class ContentPublicationService
{
    private readonly ITemplateSnapshotRepository _snapshots;
    private readonly SnapshotPublisher _publisher;
    private readonly AuditTrail _audit;

    /// <param name="limits">Limites de entrada dos gates (os mesmos da geração); nulo = padrão.</param>
    public ContentPublicationService(ITemplateSnapshotRepository snapshots, SnapshotCompiler compiler, IAuditSink audit, Generation.GenerationLimits? limits = null)
    {
        _snapshots = snapshots;
        _publisher = new SnapshotPublisher(snapshots, compiler, (limits ?? Generation.GenerationLimits.Default).Validated());
        _audit = new AuditTrail(audit);
    }

    public Task<PublicationResult> PublishAsync(
        TemplateKey key, SnapshotContent content, IReadOnlyList<FunctionalScenario> scenarios, string actor, CancellationToken cancellationToken = default) =>
        Prepare(content, out var failure) is { } candidate
            ? PublishAsync(key, candidate, scenarios, actor, cancellationToken)
            : RejectAsync(key, actor, [failure!], null, cancellationToken);

    internal PublicationCandidate? Prepare(SnapshotContent content, out Diagnostic? failure) => _publisher.Prepare(content, out failure);

    /// <summary>Publica um conteúdo já preparado (a importação de pacote confere o hash antes).</summary>
    internal async Task<PublicationResult> PublishAsync(
        TemplateKey key, PublicationCandidate candidate, IReadOnlyList<FunctionalScenario> scenarios, string actor, CancellationToken cancellationToken)
    {
        var active = await _snapshots.GetActiveVersionAsync(key, cancellationToken).ConfigureAwait(false);
        var gates = _publisher.Gate(candidate, scenarios);
        if (gates.Failures.Count > 0)
        {
            return await RejectAsync(key, actor, gates.Failures, gates.Report, cancellationToken).ConfigureAwait(false);
        }

        SnapshotSummary published;
        try
        {
            published = await _publisher.WriteAsync(key, candidate, manifest: null, active, scenarios, cancellationToken).ConfigureAwait(false);
        }
        catch (SnapshotConcurrencyException ex)
        {
            return await RejectAsync(key, actor, [Diagnostic.Critical(PublicationResult.ConcurrentChangeCode, ex.Message)], gates.Report, cancellationToken).ConfigureAwait(false);
        }

        Observability.TraduxTelemetry.Publication(key, "content", true, []);
        await _audit.RecordAsync(key, actor, "template.publish", AuditTrail.Success, new Dictionary<string, string>
        {
            ["version"] = published.Version.ToString(CultureInfo.InvariantCulture),
            ["structuralCases"] = gates.Report.Cases.Count.ToString(CultureInfo.InvariantCulture),
            ["scenarios"] = scenarios.Count.ToString(CultureInfo.InvariantCulture),
        }, cancellationToken).ConfigureAwait(false);
        return new PublicationResult(true, published.Version, [], [], gates.Report);
    }

    private async Task<PublicationResult> RejectAsync(
        TemplateKey key, string actor, IReadOnlyList<Diagnostic> failures, Contracts.Quality.QualityReport? report, CancellationToken cancellationToken)
    {
        Observability.TraduxTelemetry.Publication(key, "content", false, failures);
        await _audit.RecordAsync(key, actor, "template.publish", AuditTrail.Rejected,
            new Dictionary<string, string> { ["failures"] = AuditTrail.Failures(failures) }, cancellationToken).ConfigureAwait(false);
        return new PublicationResult(false, null, [], failures, report);
    }
}

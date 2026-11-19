using System.Globalization;
using Tradux.Application.Ports;
using Tradux.Application.Publishing;
using Tradux.Application.Snapshots;
using Tradux.Application.Testing;
using Tradux.Contracts;
using Tradux.Domain.Layers;
using Tradux.Engine.Compilation;
using Tradux.Xsd;

namespace Tradux.Application.Layers;

/// <summary>
/// Nova versão de camada e as atualizações que ela disparou — só as dos
/// templates da própria organização que criou a camada; as de outras
/// organizações são processadas, mas não devolvidas a quem publicou.
/// </summary>
public sealed record LayerVersionResult(TemplateLayer Layer, IReadOnlyList<TemplateUpdate> Updates);

/// <summary>Resultado de um rollback.</summary>
public sealed record RollbackResult(bool Success, int? ActiveVersion, int? PreviousVersion, string? Error);

/// <summary>Explicação de um snapshot publicado: camadas usadas e a história de cada regra.</summary>
public sealed record ResolutionExplanation(int Version, SnapshotManifest? Manifest);

/// <summary>
/// Orquestra camadas, vínculos, publicação, atualização (manual e
/// automática), rollback e auditoria (seção 16, ADR-0006/ADR-0010).
/// Invariantes (gate da Fase 7):
/// <list type="bullet">
/// <item>nenhuma falha — conflito, gate reprovado, cadeia inválida ou sem
/// acesso — altera a versão ativa;</item>
/// <item>toda publicação é condicionada à versão ativa que foi avaliada
/// (concorrência otimista): se outra publicação/rollback chegou antes, nada
/// é gravado e a atualização é reavaliada sobre o novo estado;</item>
/// <item>proposta manual só é aprovada sobre a versão ativa em que foi feita;
/// publicação ou rollback posteriores a substituem.</item>
/// </list>
/// </summary>
public sealed class LayeredTemplateService
{
    public const string ChainInvalidCode = "LAYER_CHAIN_INVALID";
    public const string ChainAccessDeniedCode = "LAYER_ACCESS_DENIED";
    public const string ActiveWithoutLayersCode = "ACTIVE_WITHOUT_LAYERS";
    public const string UnexpectedErrorCode = "UPDATE_UNEXPECTED_ERROR";
    public const string LayerDowngradeCode = "LAYER_DOWNGRADE";

    private const int MaxPropagationAttempts = 3;

    private readonly ITemplateLayerRepository _layers;
    private readonly ITemplateBindingRepository _bindings;
    private readonly ITemplateUpdateRepository _updates;
    private readonly ITemplateSnapshotRepository _snapshots;
    private readonly AuditTrail _audit;
    private readonly SnapshotCompiler _compiler;
    private readonly SnapshotPublisher _publisher;
    private readonly Generation.GenerationLimits _limits;
    private readonly LayerResolver _resolver = new();
    private readonly TimeProvider _time = TimeProvider.System;

    public LayeredTemplateService(
        ITemplateLayerRepository layers,
        ITemplateBindingRepository bindings,
        ITemplateUpdateRepository updates,
        ITemplateSnapshotRepository snapshots,
        IAuditSink audit,
        SnapshotCompiler compiler,
        Generation.GenerationLimits? limits = null)
    {
        _limits = (limits ?? Generation.GenerationLimits.Default).Validated();
        _layers = layers;
        _bindings = bindings;
        _updates = updates;
        _snapshots = snapshots;
        _audit = new AuditTrail(audit);
        _compiler = compiler;
        _publisher = new SnapshotPublisher(snapshots, compiler, _limits);
    }

    /// <summary>Resolve uma cadeia com o mesmo resolvedor da publicação (a importação de pacote confere o hash antes de gravar).</summary>
    internal ResolvedTemplate Resolve(IReadOnlyList<TemplateLayer> chain) => _resolver.Resolve(chain);

    // ------------------------------------------------------------------ camadas

    /// <summary>
    /// Grava uma nova versão imutável de camada e propaga para os templates
    /// que usam a camada (política de cada vínculo).
    /// </summary>
    /// <exception cref="ArgumentException">Tipo/pai/XSD inconsistentes, pai inexistente ou de outra organização sem compartilhamento.</exception>
    public async Task<LayerVersionResult> CreateLayerVersionAsync(NewLayerVersion request, string actor, CancellationToken cancellationToken = default)
    {
        await ValidateNewLayerAsync(request, cancellationToken).ConfigureAwait(false);

        var layer = new TemplateLayer
        {
            Ref = new LayerRef(request.OrganizationId, request.LayerId, 0),
            Kind = request.Kind,
            Parent = request.Parent,
            Shared = request.Shared,
            Schema = request.Schema,
            SourceSchema = request.SourceSchema,
            Overrides = request.Overrides,
            LookupOverrides = request.LookupOverrides,
            ContentHash = LayerHashing.Compute(request.Kind, request.Parent, request.Shared, request.Schema, request.Overrides, request.LookupOverrides, request.SourceSchema),
            CreatedAtUtc = _time.GetUtcNow(),
            CreatedBy = actor,
            Comment = request.Comment,
        };

        var created = await _layers.AddVersionAsync(layer, cancellationToken).ConfigureAwait(false);
        await _audit.RecordAsync(created.Ref.OrganizationId, actor, "layer.version.create", created.Ref.ToString(), "success",
            new() { ["kind"] = created.Kind.ToString(), ["parent"] = created.Parent?.ToString() ?? "", ["overrides"] = created.Overrides.Count.ToString(CultureInfo.InvariantCulture), ["shared"] = created.Shared ? "true" : "false" },
            cancellationToken).ConfigureAwait(false);

        var updates = await PropagateAsync(created, actor, cancellationToken).ConfigureAwait(false);

        // Quem publica a camada só vê as atualizações da própria organização
        // (revisão da Fase 7, A4): as de clientes de outras organizações ficam
        // no histórico de cada template.
        return new LayerVersionResult(created, updates.Where(u => u.Key.OrganizationId == created.Ref.OrganizationId).ToList());
    }

    private async Task ValidateNewLayerAsync(NewLayerVersion request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.OrganizationId) || string.IsNullOrWhiteSpace(request.LayerId))
        {
            throw new ArgumentException("Organização e identificador da camada são obrigatórios.", nameof(request));
        }

        var existing = await _layers.GetLatestAsync(request.OrganizationId, request.LayerId, cancellationToken).ConfigureAwait(false);
        if (existing is not null && existing.Kind != request.Kind)
        {
            throw new ArgumentException($"A camada '{request.LayerId}' é {existing.Kind}; uma nova versão não pode mudar o tipo.", nameof(request));
        }

        if (request.Kind == LayerKind.Base)
        {
            if (request.Parent is not null || request.Schema is null)
            {
                throw new ArgumentException("Camada base traz o XSD de destino e não tem pai.", nameof(request));
            }

            if (request.Overrides.Any(o => o.Operation != OverrideOperation.Set) || request.LookupOverrides.Any(o => o.Operation != OverrideOperation.Set))
            {
                throw new ArgumentException("Camada base só define regras (Set); remover é papel de produto/cliente.", nameof(request));
            }

            return;
        }

        if (request.Schema is not null || request.SourceSchema is not null)
        {
            throw new ArgumentException("Só a base define XSD de destino e de origem; produto e cliente guardam só diferenças.", nameof(request));
        }

        if (request.Parent is null)
        {
            throw new ArgumentException($"Camada {request.Kind} precisa de uma camada pai.", nameof(request));
        }

        var parent = await _layers.GetAsync(request.Parent, cancellationToken).ConfigureAwait(false)
            ?? throw new ArgumentException(Unavailable(request.Parent), nameof(request));

        if (AccessError(request.OrganizationId, parent) is { } denied)
        {
            throw new ArgumentException(denied, nameof(request));
        }

        var allowed = request.Kind == LayerKind.Product
            ? parent.Kind == LayerKind.Base
            : parent.Kind is LayerKind.Product or LayerKind.Base;
        if (!allowed)
        {
            throw new ArgumentException($"Camada {request.Kind} não pode ter pai {parent.Kind} (base → produto → cliente).", nameof(request));
        }
    }

    // Base e produto são protegidos (seção 16): outra organização só os usa
    // como pai (ou topo) se a VERSÃO em questão estiver compartilhada, e nunca
    // os altera — só cria a própria camada por cima.
    private static string? AccessError(string organizationId, TemplateLayer layer) =>
        layer.Ref.OrganizationId != organizationId && !layer.Shared
            ? Unavailable(layer.Ref)
            : null;

    // Mesma resposta para "não existe" e "é de outra organização e não está
    // compartilhada": a mensagem não revela se a camada alheia existe
    // (revisão da Fase 9, W1; ADR-0012 §3).
    private static string Unavailable(LayerRef reference) =>
        $"Camada '{reference}' não existe ou não está acessível para esta organização.";

    /// <summary>
    /// Mesma regra de acesso para qualquer cadeia — montada por publicação,
    /// propagação ou rebase (revisão da Fase 7, C1): o topo é da organização do
    /// template ou compartilhado, e cada pai é do mesmo dono do filho ou
    /// compartilhado.
    /// </summary>
    private static string? ChainAccessError(TemplateKey key, IReadOnlyList<TemplateLayer> chain)
    {
        if (chain.Count == 0)
        {
            return "Cadeia vazia.";
        }

        if (AccessError(key.OrganizationId, chain[^1]) is { } topDenied)
        {
            return topDenied;
        }

        for (var i = chain.Count - 1; i > 0; i--)
        {
            if (AccessError(chain[i].Ref.OrganizationId, chain[i - 1]) is { } denied)
            {
                return denied;
            }
        }

        return null;
    }

    // ------------------------------------------------------------------ publicação

    /// <summary>
    /// Vincula o template a uma cadeia (pelo topo) e publica. O vínculo só é
    /// gravado se a publicação passar.
    /// </summary>
    public async Task<PublicationResult> BindAndPublishAsync(
        TemplateKey key,
        LayerRef top,
        UpdatePolicy policy,
        IReadOnlyList<FunctionalScenario> scenarios,
        string actor,
        CancellationToken cancellationToken = default)
    {
        var chain = await LoadChainAsync(top, key.OrganizationId, cancellationToken).ConfigureAwait(false);
        if (chain.Error is not null)
        {
            return await FailAsync(key, actor, "template.publish", chain.Code, chain.Error, cancellationToken).ConfigureAwait(false);
        }

        var binding = new TemplateBinding { Key = key, Policy = policy, Scenarios = scenarios, Lineage = Lineage(chain.Layers) };
        var active = await _snapshots.GetActiveVersionAsync(key, cancellationToken).ConfigureAwait(false);
        var result = await TryPublishAsync(key, chain.Layers, binding, active, actor, "template.publish", cancellationToken).ConfigureAwait(false);
        if (result.Success)
        {
            await _bindings.UpsertAsync(binding, cancellationToken).ConfigureAwait(false);
            await SupersedePendingAsync(key, actor, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>Publica uma nova cadeia (ex.: nova versão da camada cliente) para um template já vinculado.</summary>
    public async Task<PublicationResult> PublishAsync(TemplateKey key, LayerRef top, string actor, CancellationToken cancellationToken = default)
    {
        var binding = await _bindings.GetAsync(key, cancellationToken).ConfigureAwait(false);
        if (binding is null)
        {
            return await FailAsync(key, actor, "template.publish", ChainInvalidCode, $"Template '{key}' ainda não foi vinculado a camadas.", cancellationToken).ConfigureAwait(false);
        }

        var chain = await LoadChainAsync(top, key.OrganizationId, cancellationToken).ConfigureAwait(false);
        if (chain.Error is not null)
        {
            return await FailAsync(key, actor, "template.publish", chain.Code, chain.Error, cancellationToken).ConfigureAwait(false);
        }

        var active = await _snapshots.GetActiveVersionAsync(key, cancellationToken).ConfigureAwait(false);
        var result = await TryPublishAsync(key, chain.Layers, binding, active, actor, "template.publish", cancellationToken).ConfigureAwait(false);
        if (result.Success)
        {
            await _bindings.UpsertAsync(binding with { Lineage = Lineage(chain.Layers) }, cancellationToken).ConfigureAwait(false);

            // Propostas feitas sobre a versão anterior não valem mais (revisão da Fase 7, C2).
            await SupersedePendingAsync(key, actor, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>Troca política de atualização e/ou cenários funcionais do vínculo.</summary>
    public async Task<TemplateBinding> ConfigureBindingAsync(
        TemplateKey key, UpdatePolicy? policy, IReadOnlyList<FunctionalScenario>? scenarios, string actor, CancellationToken cancellationToken = default)
    {
        var binding = await _bindings.GetAsync(key, cancellationToken).ConfigureAwait(false)
            ?? throw new ArgumentException($"Template '{key}' ainda não foi vinculado a camadas.", nameof(key));
        var updated = binding with { Policy = policy ?? binding.Policy, Scenarios = scenarios ?? binding.Scenarios };
        await _bindings.UpsertAsync(updated, cancellationToken).ConfigureAwait(false);
        await _audit.RecordAsync(key.OrganizationId, actor, "template.binding.configure", key.ToString(), "success",
            new() { ["policy"] = updated.Policy.ToString(), ["scenarios"] = updated.Scenarios.Count.ToString(CultureInfo.InvariantCulture) },
            cancellationToken).ConfigureAwait(false);
        return updated;
    }

    /// <summary>Acesso, resolução, compilação e gates — sem publicar (usado por proposta manual e por publicação).</summary>
    private Evaluation Evaluate(TemplateKey key, IReadOnlyList<TemplateLayer> chain, TemplateBinding binding)
    {
        if (ChainAccessError(key, chain) is { } denied)
        {
            return Evaluation.Failed([Diagnostic.Critical(ChainAccessDeniedCode, denied)]);
        }

        ResolvedTemplate resolved;
        try
        {
            resolved = _resolver.Resolve(chain);
        }
        catch (InvalidLayerChainException ex)
        {
            return Evaluation.Failed([Diagnostic.Critical(ChainInvalidCode, ex.Message)]);
        }

        if (resolved.HasBlockingConflicts)
        {
            return new Evaluation(resolved, resolved.Conflicts.Where(c => c.Severity == DiagnosticSeverity.Critical)
                .Select(c => new Diagnostic(DiagnosticSeverity.Critical, c.Code, c.Message, c.Path, null, $"Introduzido por {c.IntroducedBy}.")).ToList());
        }

        if (_publisher.Prepare(resolved.Content, out var failure) is not { } candidate)
        {
            return new Evaluation(resolved, [failure!]);
        }

        var result = _publisher.Gate(candidate, binding.Scenarios);
        return new Evaluation(resolved with { Content = candidate.Content }, result.Failures, result.Report, candidate);
    }

    /// <summary>
    /// Avalia (ou reaproveita a avaliação desta mesma cadeia) e publica
    /// condicionado à versão ativa <paramref name="expectedActive"/>.
    /// Concorrência (outra publicação chegou antes) vira falha
    /// <see cref="PublicationResult.ConcurrentChangeCode"/>, sem gravar nada.
    /// </summary>
    private async Task<PublicationResult> TryPublishAsync(
        TemplateKey key, IReadOnlyList<TemplateLayer> chain, TemplateBinding binding, int? expectedActive,
        string actor, string action, CancellationToken cancellationToken, Evaluation? evaluated = null)
    {
        var evaluation = evaluated ?? Evaluate(key, chain, binding);
        var conflicts = evaluation.Resolved?.Conflicts ?? [];
        if (evaluation.Failures.Count > 0 || evaluation.Candidate is null)
        {
            Observability.TraduxTelemetry.Publication(key, "layers", false, evaluation.Failures);
            await _audit.RecordAsync(key, actor, action, AuditTrail.Rejected, Summary(chain, evaluation.Failures), cancellationToken).ConfigureAwait(false);
            return new PublicationResult(false, null, conflicts, evaluation.Failures, evaluation.Report);
        }

        SnapshotSummary published;
        try
        {
            published = await _publisher.WriteAsync(
                key, evaluation.Candidate, evaluation.Resolved!.Manifest, expectedActive, binding.Scenarios, cancellationToken).ConfigureAwait(false);
        }
        catch (SnapshotConcurrencyException ex)
        {
            var diagnostic = Diagnostic.Critical(PublicationResult.ConcurrentChangeCode, ex.Message);
            await _audit.RecordAsync(key, actor, action, AuditTrail.Rejected, Summary(chain, [diagnostic]), cancellationToken).ConfigureAwait(false);
            return new PublicationResult(false, null, conflicts, [diagnostic]);
        }

        Observability.TraduxTelemetry.Publication(key, "layers", true, []);
        var details = Summary(chain, []);
        details["version"] = published.Version.ToString(CultureInfo.InvariantCulture);
        await _audit.RecordAsync(key, actor, action, AuditTrail.Success, details, cancellationToken).ConfigureAwait(false);
        return new PublicationResult(true, published.Version, conflicts, [], evaluation.Report);
    }

    // ------------------------------------------------------------------ rollback e explicação

    /// <summary>
    /// Rollback (seção 16): reativa uma versão já publicada — a informada ou,
    /// por padrão, a maior versão abaixo da ativa. Não re-resolve camadas: o
    /// snapshot é imutável e já traz o manifesto. Propostas pendentes feitas
    /// sobre a versão que saiu do ar são substituídas.
    /// </summary>
    public async Task<RollbackResult> RollbackAsync(TemplateKey key, int? targetVersion, string actor, CancellationToken cancellationToken = default)
    {
        var active = await _snapshots.GetActiveVersionAsync(key, cancellationToken).ConfigureAwait(false);
        var versions = await _snapshots.ListVersionsAsync(key, cancellationToken).ConfigureAwait(false);
        var target = targetVersion ?? versions.Select(v => v.Version).Where(v => active is null || v < active).DefaultIfEmpty(0).Max();

        if (target <= 0 || versions.All(v => v.Version != target))
        {
            return await RollbackFailedAsync(key, actor, active, $"Não há versão publicada {(targetVersion is null ? "anterior à ativa" : target.ToString(CultureInfo.InvariantCulture))} para reativar.", cancellationToken).ConfigureAwait(false);
        }

        if (target == active)
        {
            return new RollbackResult(true, active, active, null);
        }

        TemplateSnapshot? activated;
        try
        {
            // Condicionado à ativa lida: se outra publicação chegou no meio, o
            // rollback planejado não derruba a versão nova (revisão da Fase 7, W3).
            activated = await _snapshots.ActivateVersionAsync(key, target, active, cancellationToken).ConfigureAwait(false);
        }
        catch (SnapshotConcurrencyException ex)
        {
            return await RollbackFailedAsync(key, actor, ex.Actual, $"A versão ativa mudou durante o rollback ({PublicationResult.ConcurrentChangeCode}); refaça a partir da versão atual.", cancellationToken).ConfigureAwait(false);
        }

        if (activated is null)
        {
            return await RollbackFailedAsync(key, actor, active, $"Versão {target} não encontrada.", cancellationToken).ConfigureAwait(false);
        }

        if (activated.Manifest is { } manifest && await _bindings.GetAsync(key, cancellationToken).ConfigureAwait(false) is { } binding)
        {
            await _bindings.UpsertAsync(binding with { Lineage = manifest.Layers.Select(l => LayerIdentity.Of(l.Ref)).ToList() }, cancellationToken).ConfigureAwait(false);
        }

        await SupersedePendingAsync(key, actor, cancellationToken).ConfigureAwait(false);
        await _audit.RecordAsync(key.OrganizationId, actor, "template.rollback", key.ToString(), "success",
            new() { ["from"] = active?.ToString(CultureInfo.InvariantCulture) ?? "", ["to"] = target.ToString(CultureInfo.InvariantCulture) },
            cancellationToken).ConfigureAwait(false);
        return new RollbackResult(true, target, active, null);
    }

    private async Task<RollbackResult> RollbackFailedAsync(TemplateKey key, string actor, int? active, string error, CancellationToken cancellationToken)
    {
        await _audit.RecordAsync(key.OrganizationId, actor, "template.rollback", key.ToString(), "rejected", new() { ["error"] = error }, cancellationToken).ConfigureAwait(false);
        return new RollbackResult(false, active, active, error);
    }

    /// <summary>Explicação da resolução de um snapshot (ativo ou versão informada).</summary>
    public async Task<ResolutionExplanation?> ExplainAsync(TemplateKey key, int? version = null, CancellationToken cancellationToken = default)
    {
        var snapshot = version is { } v
            ? await _snapshots.GetVersionAsync(key, v, cancellationToken).ConfigureAwait(false)
            : await _snapshots.GetActiveAsync(key, cancellationToken).ConfigureAwait(false);
        return snapshot is null ? null : new ResolutionExplanation(snapshot.Version, snapshot.Manifest);
    }

    public Task<IReadOnlyList<TemplateUpdate>> ListUpdatesAsync(TemplateKey key, CancellationToken cancellationToken = default) =>
        _updates.ListAsync(key, cancellationToken);

    // ------------------------------------------------------------------ atualização

    /// <summary>
    /// Propaga uma nova versão de camada (seção 16). Cada vínculo afetado é
    /// processado isoladamente: um erro inesperado num template vira uma
    /// atualização <see cref="UpdateStatus.Failed"/> registrada e não impede
    /// os demais (revisão da Fase 7, A1).
    /// </summary>
    private async Task<IReadOnlyList<TemplateUpdate>> PropagateAsync(TemplateLayer released, string actor, CancellationToken cancellationToken)
    {
        var results = new List<TemplateUpdate>();
        var bindings = await _bindings.FindByLineageAsync(LayerIdentity.Of(released.Ref), cancellationToken).ConfigureAwait(false);

        foreach (var binding in bindings)
        {
            // Versão não compartilhada nunca entra na cadeia de outra organização
            // (topo ou pai do topo exigem compartilhamento): nem atualização nem
            // auditoria lá — o registro revelaria que a versão privada existe
            // (3ª revisão da Fase 9, N2).
            if (binding.Key.OrganizationId != released.Ref.OrganizationId && !released.Shared)
            {
                continue;
            }

            try
            {
                if (await PropagateToBindingAsync(binding, released, actor, cancellationToken).ConfigureAwait(false) is { } update)
                {
                    results.Add(update);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var failed = NewUpdate(binding, released.Ref, null, null, [released.Ref], UpdateStatus.Failed) with
                {
                    GateFailures = [Diagnostic.Critical(UnexpectedErrorCode, $"Falha inesperada ao aplicar a atualização: {ex.Message}")],
                    ResolvedAtUtc = _time.GetUtcNow(),
                    ResolvedBy = actor,
                };
                await _updates.AddAsync(failed, cancellationToken).ConfigureAwait(false);
                await _audit.RecordAsync(binding.Key.OrganizationId, actor, "template.update.apply", binding.Key.ToString(), "error", UpdateDetails(failed), cancellationToken).ConfigureAwait(false);
                results.Add(failed);
            }
        }

        return results;
    }

    /// <summary>
    /// Uma atualização de um vínculo. A cadeia proposta parte da cadeia ATIVA
    /// (ou, se a camada nova é o próprio topo, do pai que ela declara). Se a
    /// publicação automática esbarra em concorrência, reavalia sobre o novo
    /// estado — que pode já trazer uma versão igual ou mais nova da camada
    /// (então nada mais há a fazer).
    /// </summary>
    private async Task<TemplateUpdate?> PropagateToBindingAsync(TemplateBinding binding, TemplateLayer released, string actor, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var active = await _snapshots.GetActiveAsync(binding.Key, cancellationToken).ConfigureAwait(false);
            if (active is null)
            {
                return null;
            }

            if (active.Manifest is not { } manifest)
            {
                // Publicado fora das camadas (ex.: publicação de conteúdo): não há
                // cadeia ativa para reaplicar — registra em vez de sumir (A2).
                var skipped = NewUpdate(binding, released.Ref, active.Version, null, [released.Ref], UpdateStatus.Failed) with
                {
                    GateFailures = [Diagnostic.Critical(ActiveWithoutLayersCode, $"A versão ativa {active.Version} foi publicada sem camadas; republique pelo vínculo antes de receber atualizações.")],
                    ResolvedAtUtc = _time.GetUtcNow(),
                    ResolvedBy = actor,
                };
                await _updates.AddAsync(skipped, cancellationToken).ConfigureAwait(false);
                await _audit.RecordAsync(binding.Key.OrganizationId, actor, "template.update.apply", binding.Key.ToString(), "rejected", UpdateDetails(skipped), cancellationToken).ConfigureAwait(false);
                return skipped;
            }

            var plan = await PlanAsync(binding, active, manifest, released, cancellationToken).ConfigureAwait(false);
            if (plan is null)
            {
                return null;
            }

            var (update, evaluation) = plan.Value;
            // Cadeia que volta uma camada ativa para versão anterior (ex.: cliente
            // novo escrito sobre o produto antigo) nunca se aplica sozinha: vira
            // proposta, mesmo em política automática (2ª revisão da Fase 7, W2).
            var downgrade = update.Conflicts.Any(c => c.Code == LayerDowngradeCode);
            if (binding.Policy == UpdatePolicy.Manual || downgrade)
            {
                // Proposta que já nasce reprovada não derruba uma pendente válida (W5).
                if (update.GateFailures.Count == 0)
                {
                    await SupersedePendingAsync(binding.Key, actor, cancellationToken).ConfigureAwait(false);
                }

                await _updates.AddAsync(update, cancellationToken).ConfigureAwait(false);
                await _audit.RecordAsync(binding.Key.OrganizationId, actor, "template.update.propose", binding.Key.ToString(), "success", UpdateDetails(update), cancellationToken).ConfigureAwait(false);
                return update;
            }

            var final = await ApplyAsync(binding, update, evaluation, active.Version, actor, cancellationToken).ConfigureAwait(false);
            if (final.GateFailures.Any(f => f.Code == PublicationResult.ConcurrentChangeCode) && attempt < MaxPropagationAttempts)
            {
                continue; // o ativo mudou durante a publicação: reavaliar sobre o estado novo
            }

            if (final.Status == UpdateStatus.Applied)
            {
                await SupersedePendingAsync(binding.Key, actor, cancellationToken).ConfigureAwait(false); // W6
            }

            await _updates.AddAsync(final, cancellationToken).ConfigureAwait(false);
            return final;
        }
    }

    /// <summary>Monta a cadeia proposta a partir da ativa e avalia (sem publicar). Nulo = esta camada não afeta este vínculo agora.</summary>
    private async Task<(TemplateUpdate Update, Evaluation Evaluation)?> PlanAsync(
        TemplateBinding binding, TemplateSnapshot active, SnapshotManifest manifest, TemplateLayer released, CancellationToken cancellationToken)
    {
        var refs = manifest.Layers.Select(l => l.Ref).ToList();
        var index = refs.FindIndex(r => LayerIdentity.Of(r) == LayerIdentity.Of(released.Ref));
        if (index < 0 || refs[index].Version >= released.Ref.Version || index < refs.Count - 2)
        {
            return null; // já tem esta versão (ou mais nova), ou a camada está mais funda que o pai do topo
        }

        LayerRef? rebasedFrom = null;
        IReadOnlyList<ResolutionConflict> downgrades = [];
        IReadOnlyList<TemplateLayer> chain;
        List<LayerRef> proposed;

        if (index == refs.Count - 1)
        {
            // A camada nova é o próprio topo: vale a cadeia que ELA declara (A3).
            var loaded = await LoadChainAsync(released.Ref, binding.Key.OrganizationId, cancellationToken).ConfigureAwait(false);
            if (loaded.Error is not null)
            {
                var failure = Diagnostic.Critical(loaded.Code, loaded.Error);
                return (NewUpdate(binding, released.Ref, active.Version, null, [released.Ref], UpdateStatus.Proposed) with { GateFailures = [failure] },
                    Evaluation.Failed([failure]));
            }

            chain = loaded.Layers;
            proposed = chain.Select(l => l.Ref).ToList();
            downgrades = chain
                .Select(l => (Declared: l.Ref, Active: refs.FirstOrDefault(r => LayerIdentity.Of(r) == LayerIdentity.Of(l.Ref))))
                .Where(p => p.Active is not null && p.Active.Version > p.Declared.Version)
                .Select(p => new ResolutionConflict("/", released.Ref, LayerDowngradeCode, DiagnosticSeverity.Warning,
                    $"A nova versão declara '{p.Declared}', mais antiga que a ativa '{p.Active}': exige aprovação."))
                .ToList();
        }
        else
        {
            // A camada nova é pai direto do topo: o topo é rebaseado (mesmas
            // diferenças, novo pai) — só se for da organização do vínculo.
            if (refs[^1].OrganizationId != binding.Key.OrganizationId)
            {
                return null;
            }

            rebasedFrom = refs[^1];
            proposed = refs.ToList();
            proposed[index] = released.Ref;
            proposed[^1] = refs[^1] with { Version = 0 };
            var built = await BuildProposedChainAsync(proposed, rebasedFrom, cancellationToken).ConfigureAwait(false);
            if (built is null)
            {
                return null;
            }

            chain = built;
        }

        var evaluation = Evaluate(binding.Key, chain, binding);
        var changes = evaluation.Resolved is { } resolved
            ? ResolutionDiff.Compute(PublishedEntries(active), active.Manifest, resolved.Entries, resolved.Manifest)
            : [];

        var update = NewUpdate(binding, released.Ref, active.Version, rebasedFrom, proposed, UpdateStatus.Proposed) with
        {
            Changes = changes,
            Conflicts = [.. downgrades, .. evaluation.Resolved?.Conflicts ?? []],
            GateFailures = evaluation.Failures,
            Report = evaluation.Report,
            Regressions = ScenarioRegressions(active, evaluation, binding),
        };
        return (update, evaluation);
    }

    /// <summary>
    /// Aplica uma proposta manual — só sobre a mesma versão ativa em que foi
    /// feita (revisão da Fase 7, C2). Se o ativo mudou, a proposta é marcada
    /// como substituída e nada é publicado.
    /// </summary>
    /// <exception cref="InvalidOperationException">A proposta não está pendente ou ficou desatualizada.</exception>
    public async Task<TemplateUpdate> ApproveUpdateAsync(TemplateKey key, string updateId, string actor, CancellationToken cancellationToken = default)
    {
        var update = await _updates.GetAsync(key, updateId, cancellationToken).ConfigureAwait(false)
            ?? throw new ArgumentException($"Atualização '{updateId}' não existe para '{key}'.", nameof(updateId));
        if (update.Status != UpdateStatus.Proposed)
        {
            throw new InvalidOperationException($"Atualização '{updateId}' está {update.Status}; só propostas podem ser aprovadas.");
        }

        var activeVersion = await _snapshots.GetActiveVersionAsync(key, cancellationToken).ConfigureAwait(false);
        if (activeVersion != update.FromSnapshotVersion)
        {
            var superseded = update with { Status = UpdateStatus.Superseded, ResolvedAtUtc = _time.GetUtcNow(), ResolvedBy = actor };
            if (await _updates.TransitionAsync(superseded, UpdateStatus.Proposed, cancellationToken).ConfigureAwait(false))
            {
                await _audit.RecordAsync(key.OrganizationId, actor, "template.update.supersede", key.ToString(), "success", UpdateDetails(superseded), cancellationToken).ConfigureAwait(false);
            }

            throw new InvalidOperationException(
                $"Proposta '{updateId}' foi feita sobre a versão {update.FromSnapshotVersion}, mas a ativa agora é {activeVersion}; ela foi marcada como substituída.");
        }

        // Reserva a proposta antes de publicar: aprovação e rejeição (ou duas
        // aprovações) simultâneas — só uma segue (revisão da Fase 7, A6).
        var claimed = update with { Status = UpdateStatus.Applying };
        if (!await _updates.TransitionAsync(claimed, UpdateStatus.Proposed, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"Atualização '{updateId}' já foi decidida por outra operação.");
        }

        TemplateUpdate final;
        try
        {
            var binding = await _bindings.GetAsync(key, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Template '{key}' não está mais vinculado.");
            var chain = await BuildProposedChainAsync(update.ProposedChain, update.RebasedFrom, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Camadas da proposta não existem mais.");

            var evaluation = Evaluate(key, chain, binding);
            final = await ApplyAsync(binding, update with { Conflicts = evaluation.Resolved?.Conflicts ?? [], GateFailures = evaluation.Failures },
                evaluation, update.FromSnapshotVersion, actor, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Não deixa a proposta presa em "Applying": falha fica registrada.
            final = update with
            {
                Status = UpdateStatus.Failed,
                GateFailures = [Diagnostic.Critical(UnexpectedErrorCode, $"Falha ao aplicar a proposta ({ex.GetType().Name}).")],
                ResolvedAtUtc = _time.GetUtcNow(),
                ResolvedBy = actor,
            };
            await _updates.TransitionAsync(final, UpdateStatus.Applying, cancellationToken).ConfigureAwait(false);
            throw;
        }

        await _updates.TransitionAsync(final, UpdateStatus.Applying, cancellationToken).ConfigureAwait(false);
        return final;
    }

    /// <summary>Descarta uma proposta manual.</summary>
    public async Task<TemplateUpdate> RejectUpdateAsync(TemplateKey key, string updateId, string actor, CancellationToken cancellationToken = default)
    {
        var update = await _updates.GetAsync(key, updateId, cancellationToken).ConfigureAwait(false)
            ?? throw new ArgumentException($"Atualização '{updateId}' não existe para '{key}'.", nameof(updateId));
        if (update.Status != UpdateStatus.Proposed)
        {
            throw new InvalidOperationException($"Atualização '{updateId}' está {update.Status}; só propostas podem ser rejeitadas.");
        }

        var rejected = update with { Status = UpdateStatus.Rejected, ResolvedAtUtc = _time.GetUtcNow(), ResolvedBy = actor };
        if (!await _updates.TransitionAsync(rejected, UpdateStatus.Proposed, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"Atualização '{updateId}' já foi decidida por outra operação.");
        }

        await _audit.RecordAsync(key.OrganizationId, actor, "template.update.reject", key.ToString(), "success", UpdateDetails(rejected), cancellationToken).ConfigureAwait(false);
        return rejected;
    }

    /// <summary>
    /// Publica uma atualização que passou na avaliação. Se houver topo
    /// rebaseado, a nova versão dele é gravada imediatamente antes da
    /// publicação — se a publicação então falhar (concorrência ou
    /// infraestrutura), essa versão fica gravada sem uso (camadas são
    /// imutáveis; é inofensiva e rastreável) e a tentativa é registrada.
    /// </summary>
    private async Task<TemplateUpdate> ApplyAsync(
        TemplateBinding binding, TemplateUpdate update, Evaluation evaluation, int? expectedActive, string actor, CancellationToken cancellationToken)
    {
        if (evaluation.Failures.Count > 0 || evaluation.Resolved is null)
        {
            var failed = update with { Status = UpdateStatus.Failed, ResolvedAtUtc = _time.GetUtcNow(), ResolvedBy = actor };
            await _audit.RecordAsync(binding.Key.OrganizationId, actor, "template.update.apply", binding.Key.ToString(), "rejected", UpdateDetails(failed), cancellationToken).ConfigureAwait(false);
            return failed;
        }

        var chainRefs = update.ProposedChain.ToList();
        var reusable = update.RebasedFrom is null ? evaluation : null; // topo rebaseado ganha ref nova: reavalia com ela
        if (update.RebasedFrom is { } from)
        {
            var original = await _layers.GetAsync(from, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Camada '{from}' não existe mais.");
            var rebased = await _layers.AddVersionAsync(original with
            {
                Ref = original.Ref with { Version = 0 },
                Parent = chainRefs[^2],
                CreatedAtUtc = _time.GetUtcNow(),
                CreatedBy = actor,
                ContentHash = LayerHashing.Compute(original.Kind, chainRefs[^2], original.Shared, original.Schema, original.Overrides, original.LookupOverrides, original.SourceSchema),
                Comment = $"Rebase de {from} sobre {update.Trigger}.",
            }, cancellationToken).ConfigureAwait(false);
            chainRefs[^1] = rebased.Ref;
            await _audit.RecordAsync(rebased.Ref.OrganizationId, actor, "layer.version.create", rebased.Ref.ToString(), "success",
                new() { ["kind"] = rebased.Kind.ToString(), ["rebasedFrom"] = from.ToString(), ["parent"] = rebased.Parent?.ToString() ?? "" },
                cancellationToken).ConfigureAwait(false);
        }

        var chain = new List<TemplateLayer>();
        foreach (var reference in chainRefs)
        {
            chain.Add(await _layers.GetAsync(reference, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Camada '{reference}' não existe."));
        }

        var publication = await TryPublishAsync(binding.Key, chain, binding, expectedActive, actor, "template.update.apply", cancellationToken, reusable).ConfigureAwait(false);
        if (!publication.Success)
        {
            return update with { Status = UpdateStatus.Failed, ProposedChain = chainRefs, GateFailures = publication.Diagnostics, ResolvedAtUtc = _time.GetUtcNow(), ResolvedBy = actor };
        }

        await _bindings.UpsertAsync(binding with { Lineage = Lineage(chain) }, cancellationToken).ConfigureAwait(false);
        return update with
        {
            Status = UpdateStatus.Applied,
            ProposedChain = chainRefs,
            AppliedSnapshotVersion = publication.SnapshotVersion,
            ResolvedAtUtc = _time.GetUtcNow(),
            ResolvedBy = actor,
        };
    }

    private async Task<IReadOnlyList<TemplateLayer>?> BuildProposedChainAsync(IReadOnlyList<LayerRef> refs, LayerRef? rebasedFrom, CancellationToken cancellationToken)
    {
        var chain = new List<TemplateLayer>();
        for (var i = 0; i < refs.Count; i++)
        {
            var isRebasedTop = i == refs.Count - 1 && rebasedFrom is not null;
            var layer = await _layers.GetAsync(isRebasedTop ? rebasedFrom! : refs[i], cancellationToken).ConfigureAwait(false);
            if (layer is null)
            {
                return null;
            }

            // Topo rebaseado é avaliado com a identidade original (quem escreveu as
            // regras): conflitos apontam para a camada real, não para um marcador.
            chain.Add(isRebasedTop ? layer with { Parent = refs[i - 1] } : layer);
        }

        return chain;
    }

    private async Task SupersedePendingAsync(TemplateKey key, string actor, CancellationToken cancellationToken)
    {
        foreach (var pending in (await _updates.ListAsync(key, cancellationToken).ConfigureAwait(false)).Where(u => u.Status == UpdateStatus.Proposed))
        {
            var superseded = pending with { Status = UpdateStatus.Superseded, ResolvedAtUtc = _time.GetUtcNow(), ResolvedBy = actor };
            if (await _updates.TransitionAsync(superseded, UpdateStatus.Proposed, cancellationToken).ConfigureAwait(false))
            {
                await _audit.RecordAsync(key.OrganizationId, actor, "template.update.supersede", key.ToString(), "success", UpdateDetails(superseded), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static Dictionary<string, MappingEntry> PublishedEntries(TemplateSnapshot active)
    {
        var root = active.Content.RootElementName
            ?? XsdSchemaImporter.ImportInMemory(active.Content.SchemaFiles, active.Content.SchemaEntryPoint).Schema.RootElementName;
        return MappingPaths.Flatten(active.Content.Mapping, root);
    }

    // ------------------------------------------------------------------ auxiliares

    private TemplateUpdate NewUpdate(
        TemplateBinding binding, LayerRef trigger, int? fromVersion, LayerRef? rebasedFrom, IReadOnlyList<LayerRef> proposed, UpdateStatus status) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Key = binding.Key,
        Policy = binding.Policy,
        Trigger = trigger,
        FromSnapshotVersion = fromVersion,
        RebasedFrom = rebasedFrom,
        ProposedChain = proposed,
        Status = status,
        CreatedAtUtc = _time.GetUtcNow(),
    };

    /// <summary>
    /// Carrega a cadeia a partir do topo checando acesso a cada passo (topo
    /// contra a organização que pede; pai contra o dono do filho) ANTES de
    /// carregar o próximo: camada inacessível nunca é carregada, e a recusa
    /// cita só o ref pedido — nem a mensagem nem a auditoria revelam se a
    /// camada alheia existe ou quem são seus pais (2ª revisão da Fase 9, N1).
    /// </summary>
    private async Task<(IReadOnlyList<TemplateLayer> Layers, string? Error, string Code)> LoadChainAsync(
        LayerRef top, string requesterOrganizationId, CancellationToken cancellationToken)
    {
        var chain = new List<TemplateLayer>();
        var current = await _layers.GetAsync(top, cancellationToken).ConfigureAwait(false);
        if (current is null || AccessError(requesterOrganizationId, current) is not null)
        {
            return ([], Unavailable(top), ChainAccessDeniedCode);
        }

        while (current is not null)
        {
            chain.Insert(0, current);
            if (chain.Count > 3)
            {
                return ([], "Cadeia com mais de 3 camadas.", ChainInvalidCode);
            }

            if (current.Parent is null)
            {
                break;
            }

            var child = current;
            current = await _layers.GetAsync(current.Parent, cancellationToken).ConfigureAwait(false);
            if (current is null || AccessError(child.Ref.OrganizationId, current) is not null)
            {
                return ([], Unavailable(top), ChainAccessDeniedCode);
            }
        }

        return (chain, null, ChainInvalidCode);
    }

    private async Task<PublicationResult> FailAsync(TemplateKey key, string actor, string action, string code, string error, CancellationToken cancellationToken)
    {
        await _audit.RecordAsync(key, actor, action, AuditTrail.Rejected, new() { ["error"] = error }, cancellationToken).ConfigureAwait(false);
        return new PublicationResult(false, null, [], [Diagnostic.Critical(code, error)]);
    }

    private static Dictionary<string, string> Summary(IReadOnlyList<TemplateLayer> chain, IReadOnlyList<Diagnostic> failures)
    {
        var details = new Dictionary<string, string> { ["chain"] = string.Join(" → ", chain.Select(l => l.Ref.ToString())) };
        if (failures.Count > 0)
        {
            details["failures"] = AuditTrail.Failures(failures);
        }

        return details;
    }

    private static Dictionary<string, string> UpdateDetails(TemplateUpdate update) => new()
    {
        ["update"] = update.Id,
        ["trigger"] = update.Trigger.ToString(),
        ["status"] = update.Status.ToString(),
        ["changes"] = update.Changes.Count.ToString(CultureInfo.InvariantCulture),
        ["conflicts"] = update.Conflicts.Count(c => c.Severity == DiagnosticSeverity.Critical).ToString(CultureInfo.InvariantCulture),
        ["gateFailures"] = string.Join(",", update.GateFailures.Select(f => f.Code).Distinct()),
    };

    private static IReadOnlyList<LayerIdentity> Lineage(IReadOnlyList<TemplateLayer> chain) =>
        chain.Select(l => LayerIdentity.Of(l.Ref)).ToList();

    /// <summary>
    /// Regressão em atualização de camadas (ADR-0011 §6): cada cenário
    /// funcional gerado pela versão ativa e pela proposta; registra se a saída
    /// mudou e onde (XPath da primeira diferença) — sem valores. Cenário que
    /// não pôde ser comparado (ativa que não compila, entrada ilegível em um
    /// dos lados) aparece com <see cref="ScenarioRegression.Problem"/>, nunca some.
    /// </summary>
    private IReadOnlyList<ScenarioRegression> ScenarioRegressions(TemplateSnapshot active, Evaluation evaluation, TemplateBinding binding)
    {
        if (evaluation.Candidate is not { } proposed || binding.Scenarios.Count == 0)
        {
            return [];
        }

        CompiledTemplate current;
        Xsd.SourceSchema? currentSource;
        try
        {
            current = _compiler.CompileContent(SnapshotJson.Rehydrate(active.Content)).Template;
            currentSource = _compiler.CompileSource(active.Content);
        }
        catch (SnapshotCompilationException)
        {
            return binding.Scenarios
                .Select(s => ScenarioRegression.NotCompared(s.Name, "a versão ativa não compila mais (SnapshotCompilationException)."))
                .ToList();
        }

        var regressions = new List<ScenarioRegression>();
        foreach (var scenario in binding.Scenarios)
        {
            // Cada lado lê o cenário com o próprio XSD de origem (pode ter mudado na proposta).
            Domain.SourceData.SourceDataTree dataBefore, dataAfter;
            try
            {
                dataBefore = Generation.SourceInputs.Tree(scenario, currentSource, _limits);
            }
            catch (Exception ex) when (ex is ArgumentException or Xsd.SourceXmlException)
            {
                regressions.Add(ScenarioRegression.NotCompared(scenario.Name, "versão ativa: " + FunctionalScenarioGate.UnreadableInput(ex)));
                continue;
            }

            try
            {
                dataAfter = Generation.SourceInputs.Tree(scenario, proposed.Source, _limits);
            }
            catch (Exception ex) when (ex is ArgumentException or Xsd.SourceXmlException)
            {
                regressions.Add(ScenarioRegression.NotCompared(scenario.Name, "proposta: " + FunctionalScenarioGate.UnreadableInput(ex)));
                continue;
            }

            var before = Engine.XmlGenerationEngine.Generate(current, dataBefore);
            var after = Engine.XmlGenerationEngine.Generate(proposed.Template, dataAfter);
            if (before.Success && after.Success)
            {
                var comparison = Testing.XmlSemanticComparer.Compare(before.Xml!, after.Xml);
                regressions.Add(new ScenarioRegression(scenario.Name, !comparison.Equal, comparison.Equal ? null : comparison.XPath));
            }
            else
            {
                var failing = (after.Success ? before : after).Diagnostics.FirstOrDefault(d => d.Severity == Engine.Diagnostics.EngineDiagnosticSeverity.Critical);
                regressions.Add(new ScenarioRegression(scenario.Name, before.Success != after.Success, failing?.DestinationXPath));
            }
        }

        return regressions;
    }

    private sealed record Evaluation(
        ResolvedTemplate? Resolved,
        IReadOnlyList<Diagnostic> Failures,
        Contracts.Quality.QualityReport? Report = null,
        PublicationCandidate? Candidate = null)
    {
        public static Evaluation Failed(IReadOnlyList<Diagnostic> failures) => new(null, failures);
    }
}

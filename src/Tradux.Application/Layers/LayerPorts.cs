using Tradux.Domain.Layers;

namespace Tradux.Application.Layers;

/// <summary>Camadas versionadas e imutáveis (append-only). Implementado por Infrastructure.MongoDb.</summary>
public interface ITemplateLayerRepository
{
    Task<TemplateLayer?> GetAsync(LayerRef reference, CancellationToken cancellationToken = default);

    Task<TemplateLayer?> GetLatestAsync(string organizationId, string layerId, CancellationToken cancellationToken = default);

    /// <summary>Grava a próxima versão da camada (versão = última + 1, atribuída atomicamente).</summary>
    Task<TemplateLayer> AddVersionAsync(TemplateLayer layerWithoutVersion, CancellationToken cancellationToken = default);

    /// <summary>
    /// Importação de pacote (ADR-0013 §2): grava a camada na versão EXATA de
    /// <see cref="TemplateLayer.Ref"/> se ela ainda não existe; se existe,
    /// devolve a gravada (o chamador compara o <c>ContentHash</c>).
    /// </summary>
    Task<TemplateLayer> ImportVersionAsync(TemplateLayer layer, CancellationToken cancellationToken = default);
}

public interface ITemplateBindingRepository
{
    Task<TemplateBinding?> GetAsync(TemplateKey key, CancellationToken cancellationToken = default);

    Task UpsertAsync(TemplateBinding binding, CancellationToken cancellationToken = default);

    /// <summary>
    /// Vínculos cuja cadeia inclui a camada — de qualquer organização. Uso
    /// exclusivo da propagação de atualizações (processo interno); nunca
    /// exposto a um chamador de outra organização.
    /// </summary>
    Task<IReadOnlyList<TemplateBinding>> FindByLineageAsync(LayerIdentity layer, CancellationToken cancellationToken = default);
}

public interface ITemplateUpdateRepository
{
    Task AddAsync(TemplateUpdate update, CancellationToken cancellationToken = default);

    Task<TemplateUpdate?> GetAsync(TemplateKey key, string updateId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Troca o registro só se o status gravado ainda for <paramref name="expectedStatus"/>
    /// (transição condicional — duas decisões simultâneas não vencem as duas;
    /// revisão da Fase 7, A6). Falso = outra decisão chegou antes.
    /// </summary>
    Task<bool> TransitionAsync(TemplateUpdate update, UpdateStatus expectedStatus, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TemplateUpdate>> ListAsync(TemplateKey key, CancellationToken cancellationToken = default);
}

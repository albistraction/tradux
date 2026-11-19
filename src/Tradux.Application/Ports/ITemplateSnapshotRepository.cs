using Tradux.Application.Snapshots;

namespace Tradux.Application.Ports;

/// <summary>
/// Porta de persistência de snapshots (ADR-0004/ADR-0007): definida aqui,
/// implementada por <c>Tradux.Infrastructure.MongoDb</c>. Não existe
/// operação de alteração de conteúdo — um snapshot publicado é imutável
/// (ADR-0003); só a publicação de uma nova versão muda qual está ativa.
/// </summary>
public interface ITemplateSnapshotRepository
{
    Task<TemplateSnapshot?> GetActiveAsync(TemplateKey key, CancellationToken cancellationToken = default);

    Task<TemplateSnapshot?> GetVersionAsync(TemplateKey key, int version, CancellationToken cancellationToken = default);

    /// <summary>Leitura barata só do ponteiro de versão ativa — usada pelo poll de segurança do cache (ADR-0005), nunca pelo caminho de geração.</summary>
    Task<int?> GetActiveVersionAsync(TemplateKey key, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SnapshotSummary>> ListVersionsAsync(TemplateKey key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Publica atomicamente (ADR-0005): grava o novo snapshot como ativo com a
    /// próxima versão sequencial da chave e rebaixa o ativo anterior para
    /// <see cref="SnapshotStatus.Superseded"/>, tudo ou nada.
    /// </summary>
    Task<TemplateSnapshot> PublishAsync(SnapshotDraft draft, CancellationToken cancellationToken = default);

    /// <summary>
    /// Rollback (seção 16): torna ativa uma versão já publicada e rebaixa a
    /// ativa atual, atomicamente — só se a versão ativa ainda for
    /// <paramref name="expectedActiveVersion"/>; senão lança
    /// <see cref="SnapshotConcurrencyException"/> sem mudar nada. Devolve nulo
    /// se a versão não existe.
    /// </summary>
    Task<TemplateSnapshot?> ActivateVersionAsync(TemplateKey key, int version, int? expectedActiveVersion, CancellationToken cancellationToken = default);
}

/// <summary>A versão ativa mudou entre a avaliação e a publicação; nada foi gravado.</summary>
public sealed class SnapshotConcurrencyException(TemplateKey key, int? expected, int? actual)
    : Exception($"Versão ativa de '{key}' mudou durante a publicação (esperada {expected?.ToString() ?? "nenhuma"}, atual {actual?.ToString() ?? "nenhuma"}).")
{
    public int? Expected { get; } = expected;
    public int? Actual { get; } = actual;
}

namespace Tradux.Application.Ports;

/// <summary>
/// Porta de notificação de mudança de versão ativa (ADR-0005: Change Streams
/// do MongoDB na implementação real). Cada item é uma chave cuja versão
/// ativa pode ter mudado; o consumidor relê o ponteiro — o feed não precisa
/// ser exato nem sem duplicatas, só não pode deixar de avisar.
/// </summary>
public interface ISnapshotChangeFeed
{
    IAsyncEnumerable<TemplateKey> WatchAsync(CancellationToken cancellationToken);
}

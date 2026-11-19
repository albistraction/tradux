using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using Tradux.Application;
using Tradux.Application.Snapshots;

namespace Tradux.Infrastructure.MongoDb;

// Campos de consulta ficam como campos BSON indexados; o restante (árvores de
// regra, cenários) é guardado como JSON canônico (SnapshotJson) — mesmo motivo
// do mapeamento do snapshot: round-trip exato de decimais e do polimorfismo de RuleNode.

internal static class JsonPayload
{
    public static string Write<T>(T value) => JsonSerializer.Serialize(value, SnapshotJson.Options);

    public static T Read<T>(BsonValue value) => JsonSerializer.Deserialize<T>(value.AsString, SnapshotJson.Options)!;
}

/// <summary>Campos da chave lógica do template, iguais em toda coleção que a guarda.</summary>
internal static class TemplateKeyFields
{
    public const string OrganizationId = "organizationId";
    public const string TemplateId = "templateId";
    public const string Operation = "operation";

    public static FilterDefinition<T> Filter<T>(TemplateKey key)
    {
        var filter = Builders<T>.Filter;
        return filter.Eq(OrganizationId, key.OrganizationId) & filter.Eq(TemplateId, key.TemplateId) & filter.Eq(Operation, key.Operation);
    }

    public static TemplateKey Read(BsonDocument document) =>
        new(document[OrganizationId].AsString, document[TemplateId].AsString, document[Operation].AsString);
}

/// <summary>
/// Único mecanismo de índices: cada repositório cria os da sua coleção uma
/// vez por instância, antes da primeira escrita (a API e os testes usam os
/// mesmos repositórios). Criação de índice é idempotente no MongoDB; o guarda
/// só evita a ida repetida ao servidor.
/// </summary>
internal sealed class IndexGuard
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private volatile bool _ensured;

    public async Task EnsureAsync(Func<CancellationToken, Task> create, CancellationToken cancellationToken)
    {
        if (_ensured)
        {
            return;
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_ensured)
            {
                await create(cancellationToken).ConfigureAwait(false);
                _ensured = true;
            }
        }
        finally
        {
            _lock.Release();
        }
    }
}

/// <summary>
/// Violação de índice único (código 11000), venha de uma escrita isolada ou
/// de um comando (transação). Índices únicos são a garantia de versão sem
/// repetição e de um só ativo (ADR-0005).
/// </summary>
internal static class DuplicateKey
{
    private const int MaxAttempts = 5;

    public static bool Is(MongoException ex) => ex switch
    {
        MongoWriteException write => write.WriteError?.Category == ServerErrorCategory.DuplicateKey,
        MongoCommandException command => command.Code == 11000,
        _ => false,
    };

    /// <summary>
    /// Repete <paramref name="attempt"/> quando duas escritas concorrentes
    /// calculam a mesma próxima versão: cada tentativa relê o estado. Na
    /// última tentativa a violação propaga.
    /// </summary>
    public static async Task<T> RetryAsync<T>(Func<CancellationToken, Task<T>> attempt, CancellationToken cancellationToken)
    {
        for (var n = 1; ; n++)
        {
            try
            {
                return await attempt(cancellationToken).ConfigureAwait(false);
            }
            catch (MongoException ex) when (n < MaxAttempts && Is(ex))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10 * n), cancellationToken).ConfigureAwait(false);
            }
        }
    }
}

using System.Runtime.CompilerServices;
using MongoDB.Bson;
using MongoDB.Driver;
using Tradux.Application;
using Tradux.Application.Ports;

namespace Tradux.Infrastructure.MongoDb;

/// <summary>
/// Feed de mudança de versão ativa via MongoDB Change Streams (ADR-0005).
/// Emite a chave lógica de todo snapshot que passou a ser o ativo; o
/// consumidor (<c>SnapshotCacheInvalidator</c>) relê o ponteiro. O
/// <c>$project</c> mantém só os campos de chave — o conteúdo do snapshot
/// (XSDs, mapeamento) nunca trafega pelo stream.
/// </summary>
public sealed class MongoSnapshotChangeFeed : ISnapshotChangeFeed
{
    private readonly IMongoCollection<BsonDocument> _snapshots;

    public MongoSnapshotChangeFeed(IMongoClient client, MongoDbOptions options)
    {
        _snapshots = client.GetDatabase(options.DatabaseName).GetCollection<BsonDocument>(options.SnapshotCollectionName);
    }

    public async IAsyncEnumerable<TemplateKey> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var pipeline = new EmptyPipelineDefinition<ChangeStreamDocument<BsonDocument>>()
            // Só eventos cujo documento resultante está ativo: uma publicação
            // gera dois eventos (antigo → superseded, novo inserido) e só o
            // segundo muda o que deve ser servido; o rollback (versão
            // reativada) também passa por este filtro.
            .Match(new BsonDocument
            {
                { "operationType", new BsonDocument("$in", new BsonArray { "insert", "update", "replace" }) },
                { "fullDocument." + SnapshotDocument.Fields.Status, SnapshotDocument.ActiveStatus },
            })
            .Project<ChangeStreamDocument<BsonDocument>, ChangeStreamDocument<BsonDocument>, ChangeStreamDocument<BsonDocument>>(
                new BsonDocument
                {
                    { "operationType", 1 },
                    { "documentKey", 1 },
                    { "fullDocument." + TemplateKeyFields.OrganizationId, 1 },
                    { "fullDocument." + TemplateKeyFields.TemplateId, 1 },
                    { "fullDocument." + TemplateKeyFields.Operation, 1 },
                });

        var options = new ChangeStreamOptions { FullDocument = ChangeStreamFullDocumentOption.UpdateLookup };

        using var cursor = await _snapshots.WatchAsync(pipeline, options, cancellationToken).ConfigureAwait(false);
        while (await cursor.MoveNextAsync(cancellationToken).ConfigureAwait(false))
        {
            foreach (var change in cursor.Current)
            {
                if (change.FullDocument is { } document
                    && document.Contains(TemplateKeyFields.OrganizationId)
                    && document.Contains(TemplateKeyFields.TemplateId)
                    && document.Contains(TemplateKeyFields.Operation))
                {
                    yield return TemplateKeyFields.Read(document);
                }
            }
        }
    }
}

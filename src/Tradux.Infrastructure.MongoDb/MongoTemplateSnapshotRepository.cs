using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using Tradux.Application;
using Tradux.Application.Ports;
using Tradux.Application.Layers;
using Tradux.Application.Testing;
using Tradux.Application.Snapshots;
using Tradux.Domain.Mapping;

namespace Tradux.Infrastructure.MongoDb;

/// <summary>
/// Implementação MongoDB de <see cref="ITemplateSnapshotRepository"/>
/// (ADR-0004). Toda consulta filtra pela chave completa — organização
/// incluída (isolamento multiempresa, seção 20). Não existe caminho de
/// atualização de conteúdo: a única escrita além do insert de uma versão
/// nova é a transição de <c>status</c> para <c>superseded</c> (ADR-0003).
/// </summary>
public sealed class MongoTemplateSnapshotRepository : ITemplateSnapshotRepository, ITemplateCatalog
{
    private readonly IMongoClient _client;
    private readonly IMongoCollection<SnapshotDocument> _snapshots;

    public MongoTemplateSnapshotRepository(IMongoClient client, MongoDbOptions options)
    {
        _client = client;
        _snapshots = client.GetDatabase(options.DatabaseName).GetCollection<SnapshotDocument>(options.SnapshotCollectionName);
    }

    private readonly IndexGuard _indexes = new();

    /// <summary>Índices da coleção, garantidos antes da primeira escrita desta instância (nunca no caminho de geração).</summary>
    private Task CreateIndexesAsync(CancellationToken cancellationToken)
    {
        var keys = Builders<SnapshotDocument>.IndexKeys;
        var models = new[]
        {
            // Unicidade de versão por chave lógica.
            new CreateIndexModel<SnapshotDocument>(
                keys.Ascending(d => d.OrganizationId).Ascending(d => d.TemplateId).Ascending(d => d.Operation).Descending(d => d.SnapshotVersion),
                new CreateIndexOptions { Name = "ux_key_version", Unique = true }),

            // Nunca dois snapshots ativos para a mesma chave (ADR-0005) —
            // garantido pelo banco, não só pela transação de publicação.
            // Também é o índice da consulta mais frequente (resolução do ativo).
            new CreateIndexModel<SnapshotDocument>(
                keys.Ascending(d => d.OrganizationId).Ascending(d => d.TemplateId).Ascending(d => d.Operation),
                new CreateIndexOptions<SnapshotDocument>
                {
                    Name = "ux_key_active",
                    Unique = true,
                    PartialFilterExpression = Builders<SnapshotDocument>.Filter.Eq(d => d.Status, SnapshotDocument.ActiveStatus),
                }),

            new CreateIndexModel<SnapshotDocument>(
                keys.Ascending(d => d.OrganizationId).Ascending(d => d.Status),
                new CreateIndexOptions { Name = "ix_org_status" }),

            new CreateIndexModel<SnapshotDocument>(
                keys.Ascending(d => d.ContentHash),
                new CreateIndexOptions { Name = "ix_content_hash" }),
        };

        return _snapshots.Indexes.CreateManyAsync(models, cancellationToken);
    }

    public async Task<TemplateSnapshot?> GetActiveAsync(TemplateKey key, CancellationToken cancellationToken = default)
    {
        var document = await _snapshots.Find(ActiveFilter(key)).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return document is null ? null : ToSnapshot(document);
    }

    public async Task<TemplateSnapshot?> GetVersionAsync(TemplateKey key, int version, CancellationToken cancellationToken = default)
    {
        var filter = KeyFilter(key) & Builders<SnapshotDocument>.Filter.Eq(d => d.SnapshotVersion, version);
        var document = await _snapshots.Find(filter).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return document is null ? null : ToSnapshot(document);
    }

    public async Task<int?> GetActiveVersionAsync(TemplateKey key, CancellationToken cancellationToken = default)
    {
        var projection = Builders<SnapshotDocument>.Projection.Include(d => d.SnapshotVersion).Exclude(d => d.Id);
        var document = await _snapshots.Find(ActiveFilter(key)).Project(projection).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return document?[SnapshotDocument.Fields.SnapshotVersion].AsInt32;
    }

    /// <summary>Versões ativas da organização, por template e operação (só o ponteiro, sem conteúdo).</summary>
    public async Task<IReadOnlyList<TemplateCatalogEntry>> ListAsync(string organizationId, CancellationToken cancellationToken = default)
    {
        var projection = Builders<SnapshotDocument>.Projection
            .Include(d => d.TemplateId)
            .Include(d => d.Operation)
            .Include(d => d.SnapshotVersion)
            .Include(d => d.ContentHash)
            .Include(d => d.PublishedAtUtc)
            .Exclude(d => d.Id);
        var filter = Builders<SnapshotDocument>.Filter.Eq(d => d.OrganizationId, organizationId)
            & Builders<SnapshotDocument>.Filter.Eq(d => d.Status, SnapshotDocument.ActiveStatus);

        var documents = await _snapshots.Find(filter).Project(projection).ToListAsync(cancellationToken).ConfigureAwait(false);
        return documents
            .Select(d => new TemplateCatalogEntry(
                d[TemplateKeyFields.TemplateId].AsString,
                d[TemplateKeyFields.Operation].AsString,
                d[SnapshotDocument.Fields.SnapshotVersion].AsInt32,
                d[SnapshotDocument.Fields.ContentHash].AsString,
                new DateTimeOffset(d[SnapshotDocument.Fields.PublishedAtUtc].ToUniversalTime(), TimeSpan.Zero)))
            .OrderBy(e => e.TemplateId, StringComparer.Ordinal)
            .ThenBy(e => e.Operation, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<IReadOnlyList<SnapshotSummary>> ListVersionsAsync(TemplateKey key, CancellationToken cancellationToken = default)
    {
        var projection = Builders<SnapshotDocument>.Projection
            .Include(d => d.SnapshotVersion)
            .Include(d => d.ContentHash)
            .Include(d => d.Status)
            .Include(d => d.PublishedAtUtc)
            .Exclude(d => d.Id);

        var documents = await _snapshots.Find(KeyFilter(key))
            .SortByDescending(d => d.SnapshotVersion)
            .Project(projection)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return documents
            .Select(d => new SnapshotSummary(
                d[SnapshotDocument.Fields.SnapshotVersion].AsInt32,
                d[SnapshotDocument.Fields.ContentHash].AsString,
                ParseStatus(d[SnapshotDocument.Fields.Status].AsString),
                new DateTimeOffset(d[SnapshotDocument.Fields.PublishedAtUtc].ToUniversalTime(), TimeSpan.Zero)))
            .ToList();
    }

    public async Task<TemplateSnapshot> PublishAsync(SnapshotDraft draft, CancellationToken cancellationToken = default)
    {
        // Conflito em índice único (duas publicações concorrentes calculando
        // a mesma próxima versão, ou dois "primeiros" ativos) não é erro
        // transitório para o driver; a transação inteira é repetida aqui,
        // relendo a última versão. Índices únicos são a garantia de "um só
        // ativo" (ADR-0005): nenhuma publicação acontece antes deles existirem.
        await _indexes.EnsureAsync(CreateIndexesAsync, cancellationToken).ConfigureAwait(false);

        return await DuplicateKey.RetryAsync(ct => PublishOnceAsync(draft, ct), cancellationToken).ConfigureAwait(false);
    }

    private async Task<TemplateSnapshot> PublishOnceAsync(SnapshotDraft draft, CancellationToken cancellationToken)
    {
        using var session = await _client.StartSessionAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        // Transação multi-documento (ADR-0005): rebaixar o ativo anterior e
        // inserir o novo ativo acontecem juntos ou não acontecem. Publicações
        // concorrentes da mesma chave colidem no documento ativo (write
        // conflict, repetido automaticamente por WithTransactionAsync) ou nos
        // índices únicos — nunca resultam em dois ativos ou versão repetida.
        var document = await session.WithTransactionAsync(
            async (s, ct) =>
            {
                // Idempotência dentro da transação: publicações concorrentes do
                // mesmo conteúdo convergem para uma única versão (quem perde o
                // conflito relê e encontra o ativo com o mesmo hash).
                var active = await _snapshots.Find(s, ActiveFilter(draft.Key)).FirstOrDefaultAsync(ct).ConfigureAwait(false);
                if (active is not null && active.ContentHash == draft.ContentHash)
                {
                    return active;
                }

                if (draft.EnforceExpectedActive && active?.SnapshotVersion != draft.ExpectedActiveVersion)
                {
                    // Outra publicação/rollback mudou o ativo desde a avaliação:
                    // aborta a transação sem gravar (revisão da Fase 7, C3).
                    throw new SnapshotConcurrencyException(draft.Key, draft.ExpectedActiveVersion, active?.SnapshotVersion);
                }

                var latest = await _snapshots.Find(s, KeyFilter(draft.Key))
                    .SortByDescending(d => d.SnapshotVersion)
                    .Project(Builders<SnapshotDocument>.Projection.Include(d => d.SnapshotVersion).Exclude(d => d.Id))
                    .FirstOrDefaultAsync(ct)
                    .ConfigureAwait(false);
                var nextVersion = (latest?[SnapshotDocument.Fields.SnapshotVersion].AsInt32 ?? 0) + 1;

                await _snapshots.UpdateManyAsync(
                        s,
                        ActiveFilter(draft.Key),
                        Builders<SnapshotDocument>.Update.Set(d => d.Status, SnapshotDocument.SupersededStatus),
                        cancellationToken: ct)
                    .ConfigureAwait(false);

                var newDocument = ToDocument(draft, nextVersion);
                await _snapshots.InsertOneAsync(s, newDocument, cancellationToken: ct).ConfigureAwait(false);
                return newDocument;
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return ToSnapshot(document);
    }

    public async Task<TemplateSnapshot?> ActivateVersionAsync(TemplateKey key, int version, int? expectedActiveVersion, CancellationToken cancellationToken = default)
    {
        await _indexes.EnsureAsync(CreateIndexesAsync, cancellationToken).ConfigureAwait(false);
        using var session = await _client.StartSessionAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        // Mesma garantia da publicação: rebaixar o ativo e reativar o alvo na
        // mesma transação — nunca zero nem dois ativos (ADR-0005). O Change
        // Stream vê o alvo virar "active" e as instâncias trocam o cache.
        var document = await session.WithTransactionAsync(
            async (s, ct) =>
            {
                var target = await _snapshots.Find(s, KeyFilter(key) & Builders<SnapshotDocument>.Filter.Eq(d => d.SnapshotVersion, version))
                    .FirstOrDefaultAsync(ct).ConfigureAwait(false);
                if (target is null)
                {
                    return null;
                }

                var active = await _snapshots.Find(s, ActiveFilter(key)).FirstOrDefaultAsync(ct).ConfigureAwait(false);
                if (active?.SnapshotVersion != expectedActiveVersion)
                {
                    throw new SnapshotConcurrencyException(key, expectedActiveVersion, active?.SnapshotVersion);
                }

                if (target.Status == SnapshotDocument.ActiveStatus)
                {
                    return target;
                }

                await _snapshots.UpdateManyAsync(s, ActiveFilter(key),
                    Builders<SnapshotDocument>.Update.Set(d => d.Status, SnapshotDocument.SupersededStatus), cancellationToken: ct).ConfigureAwait(false);
                await _snapshots.UpdateOneAsync(s, Builders<SnapshotDocument>.Filter.Eq(d => d.Id, target.Id),
                    Builders<SnapshotDocument>.Update.Set(d => d.Status, SnapshotDocument.ActiveStatus), cancellationToken: ct).ConfigureAwait(false);
                target.Status = SnapshotDocument.ActiveStatus;
                return target;
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return document is null ? null : ToSnapshot(document);
    }

    private static FilterDefinition<SnapshotDocument> KeyFilter(TemplateKey key) => TemplateKeyFields.Filter<SnapshotDocument>(key);

    private static FilterDefinition<SnapshotDocument> ActiveFilter(TemplateKey key) =>
        KeyFilter(key) & Builders<SnapshotDocument>.Filter.Eq(d => d.Status, SnapshotDocument.ActiveStatus);

    private static SnapshotDocument ToDocument(SnapshotDraft draft, int version) => new()
    {
        Id = ObjectId.GenerateNewId(),
        OrganizationId = draft.Key.OrganizationId,
        TemplateId = draft.Key.TemplateId,
        Operation = draft.Key.Operation,
        SnapshotVersion = version,
        ContentHash = draft.ContentHash,
        Status = SnapshotDocument.ActiveStatus,
        PublishedAtUtc = draft.PublishedAtUtc.UtcDateTime,
        SchemaEntryPoint = draft.Content.SchemaEntryPoint,
        RootElementName = draft.Content.RootElementName,
        SchemaFiles = draft.Content.SchemaFiles
            .OrderBy(f => f.Key, StringComparer.Ordinal)
            .Select(f => new SchemaFileDocument { Name = f.Key, Content = f.Value })
            .ToList(),
        MappingJson = JsonSerializer.Serialize(draft.Content.Mapping, SnapshotJson.Options),
        LookupTablesJson = JsonSerializer.Serialize(draft.Content.LookupTables, SnapshotJson.Options),
        ManifestJson = draft.Manifest is null ? null : JsonSerializer.Serialize(draft.Manifest, SnapshotJson.Options),
        ScenariosJson = draft.Scenarios.Count == 0 ? null : JsonSerializer.Serialize(draft.Scenarios, SnapshotJson.Options),
        SourceSchema = draft.Content.SourceSchema is not { } source ? null : new SourceSchemaDocument
        {
            EntryPoint = source.EntryPoint,
            RootElementName = source.RootElementName,
            Files = source.Files.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => new SchemaFileDocument { Name = f.Key, Content = f.Value }).ToList(),
            ExtensionsJson = source.Extensions.Count == 0 ? null : JsonSerializer.Serialize(source.Extensions, SnapshotJson.Options),
        },
    };

    private static TemplateSnapshot ToSnapshot(SnapshotDocument document) => new()
    {
        Key = new TemplateKey(document.OrganizationId, document.TemplateId, document.Operation),
        Version = document.SnapshotVersion,
        ContentHash = document.ContentHash,
        Status = ParseStatus(document.Status),
        PublishedAtUtc = new DateTimeOffset(DateTime.SpecifyKind(document.PublishedAtUtc, DateTimeKind.Utc)),
        Content = new SnapshotContent
        {
            SchemaEntryPoint = document.SchemaEntryPoint,
            RootElementName = document.RootElementName,
            SchemaFiles = document.SchemaFiles.ToDictionary(f => f.Name, f => f.Content, StringComparer.Ordinal),
            Mapping = JsonSerializer.Deserialize<MappingTemplate>(document.MappingJson, SnapshotJson.Options)
                ?? throw new InvalidOperationException($"Snapshot {document.Id} tem mapeamento vazio."),
            LookupTables = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(document.LookupTablesJson, SnapshotJson.Options)!
                .ToDictionary(t => t.Key, t => (IReadOnlyDictionary<string, string>)t.Value),
            SourceSchema = document.SourceSchema is not { } source ? null : new SourceSchemaContent(
                source.Files.ToDictionary(f => f.Name, f => f.Content, StringComparer.Ordinal), source.EntryPoint, source.RootElementName)
            {
                Extensions = source.ExtensionsJson is null ? [] : JsonSerializer.Deserialize<List<Tradux.Domain.SourceContracts.SourceExtension>>(source.ExtensionsJson, SnapshotJson.Options)!,
            },
        },
        Manifest = document.ManifestJson is null ? null : JsonSerializer.Deserialize<SnapshotManifest>(document.ManifestJson, SnapshotJson.Options),
        Scenarios = document.ScenariosJson is null ? [] : JsonSerializer.Deserialize<List<FunctionalScenario>>(document.ScenariosJson, SnapshotJson.Options)!,
    };

    private static SnapshotStatus ParseStatus(string status) => status switch
    {
        SnapshotDocument.ActiveStatus => SnapshotStatus.Active,
        SnapshotDocument.SupersededStatus => SnapshotStatus.Superseded,
        _ => throw new InvalidOperationException($"Status de snapshot desconhecido: '{status}'."),
    };
}

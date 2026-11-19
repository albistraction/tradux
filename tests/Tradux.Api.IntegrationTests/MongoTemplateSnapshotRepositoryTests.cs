using MongoDB.Bson;
using MongoDB.Driver;
using Tradux.Api.IntegrationTests.Infrastructure;
using Tradux.Application;
using Tradux.Application.Ports;
using Tradux.Application.Snapshots;
using Tradux.Infrastructure.MongoDb;
using Tradux.Tests.Shared;

namespace Tradux.Api.IntegrationTests;

/// <summary>
/// Adaptador MongoDB contra um <c>mongod</c> real (ADR-0004/0005):
/// publicação transacional, um único ativo garantido pelo banco, isolamento
/// por organização, round-trip exato do conteúdo e Change Streams.
/// </summary>
[Collection(MongoCollection.Name)]
public class MongoTemplateSnapshotRepositoryTests(MongoFixture mongo)
{
    private static readonly TemplateKey Key = new(SnapshotFixtures.Organization, "dps", SnapshotFixtures.Operation);

    private Task<(MongoTemplateSnapshotRepository Repository, IMongoClient Client, MongoDbOptions Options)> NewRepositoryAsync()
    {
        var options = mongo.NewOptions();
        var client = mongo.CreateClient();
        return Task.FromResult((new MongoTemplateSnapshotRepository(client, options), client, options));
    }

    private static SnapshotDraft Draft(TemplateKey key, SnapshotContent content)
    {
        var (xsdHash, _) = new SnapshotCompiler().CompileContent(content);
        return new SnapshotDraft(key, SnapshotCompiler.ComputeContentHash(xsdHash, content), DateTimeOffset.UtcNow, content);
    }

    [Fact]
    public async Task Publishing_supersedes_the_previous_active_version_atomically()
    {
        var (repository, _, _) = await NewRepositoryAsync();

        await repository.PublishAsync(Draft(Key, SnapshotFixtures.SimpleDps()));
        var second = await repository.PublishAsync(Draft(Key, SnapshotFixtures.SimpleDps("V2-")));

        Assert.Equal(2, second.Version);
        Assert.Equal(2, await repository.GetActiveVersionAsync(Key));
        Assert.Equal(2, (await repository.GetActiveAsync(Key))!.Version);
        var versions = await repository.ListVersionsAsync(Key);
        Assert.Equal([2, 1], versions.Select(v => v.Version));
        Assert.Equal([SnapshotStatus.Active, SnapshotStatus.Superseded], versions.Select(v => v.Status));
        Assert.Equal(SnapshotStatus.Superseded, (await repository.GetVersionAsync(Key, 1))!.Status);
    }

    [Fact]
    public async Task Content_round_trips_exactly_so_the_recomputed_hash_matches_the_stored_one()
    {
        var (repository, _, _) = await NewRepositoryAsync();
        var content = SnapshotFixtures.PedidoComItens();

        var published = await repository.PublishAsync(Draft(Key, content));
        var reloaded = (await repository.GetVersionAsync(Key, published.Version))!;

        var (xsdHash, template) = new SnapshotCompiler().CompileContent(reloaded.Content);
        Assert.Equal(published.ContentHash, SnapshotCompiler.ComputeContentHash(xsdHash, reloaded.Content));
        Assert.False(template.HasCriticalCompileTimeDiagnostics);
    }

    [Fact]
    public async Task Queries_never_cross_organizations()
    {
        var (repository, _, _) = await NewRepositoryAsync();
        await repository.PublishAsync(Draft(Key, SnapshotFixtures.SimpleDps()));
        var otherOrg = new TemplateKey(SnapshotFixtures.OtherOrganization, Key.TemplateId, Key.Operation);

        Assert.Null(await repository.GetActiveAsync(otherOrg));
        Assert.Null(await repository.GetVersionAsync(otherOrg, 1));
        Assert.Null(await repository.GetActiveVersionAsync(otherOrg));
        Assert.Empty(await repository.ListVersionsAsync(otherOrg));

        var own = await repository.PublishAsync(Draft(otherOrg, SnapshotFixtures.SimpleDps()));
        Assert.Equal(1, own.Version);
    }

    [Fact]
    public async Task Concurrent_publications_get_distinct_versions_and_leave_exactly_one_active()
    {
        var (repository, _, _) = await NewRepositoryAsync();

        var published = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(i => Task.Run(() => repository.PublishAsync(Draft(Key, SnapshotFixtures.SimpleDps($"P{i}-"))))));

        Assert.Equal(Enumerable.Range(1, 8), published.Select(p => p.Version).Order());
        var versions = await repository.ListVersionsAsync(Key);
        Assert.Single(versions, v => v.Status == SnapshotStatus.Active);
        Assert.Equal(8, versions.Max(v => v.Version));
        Assert.Equal(8, await repository.GetActiveVersionAsync(Key));
    }

    [Fact]
    public async Task First_publications_on_a_fresh_database_without_explicit_index_setup_still_leave_one_active()
    {
        // Os índices únicos nascem antes da primeira publicação: sem eles, publicações concorrentes já gravaram 8 ativos, todos versão 1.
        var options = mongo.NewOptions();
        var repository = new MongoTemplateSnapshotRepository(mongo.CreateClient(), options);

        var published = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(i => Task.Run(() => repository.PublishAsync(Draft(Key, SnapshotFixtures.SimpleDps($"C{i}-"))))));

        Assert.Equal(Enumerable.Range(1, 8), published.Select(p => p.Version).Order());
        Assert.Single(await repository.ListVersionsAsync(Key), v => v.Status == SnapshotStatus.Active);
    }

    [Fact]
    public async Task Conditional_activation_refuses_when_the_active_version_changed()
    {
        // Revisão da Fase 7, W3: rollback só reativa se a ativa ainda é a lida.
        var (repository, _, _) = await NewRepositoryAsync();
        await repository.PublishAsync(Draft(Key, SnapshotFixtures.SimpleDps("V1-")));
        await repository.PublishAsync(Draft(Key, SnapshotFixtures.SimpleDps("V2-")));
        await repository.PublishAsync(Draft(Key, SnapshotFixtures.SimpleDps("V3-")));

        await Assert.ThrowsAsync<SnapshotConcurrencyException>(() => repository.ActivateVersionAsync(Key, 1, expectedActiveVersion: 2));
        Assert.Equal(3, await repository.GetActiveVersionAsync(Key));

        Assert.Equal(1, (await repository.ActivateVersionAsync(Key, 1, expectedActiveVersion: 3))!.Version);
        Assert.Equal(1, await repository.GetActiveVersionAsync(Key));
    }

    [Fact]
    public async Task Guarded_publications_racing_on_the_same_active_version_let_exactly_one_through()
    {
        // Revisão da Fase 7, C3: duas propagações avaliadas sobre a mesma
        // versão ativa não podem ambas publicar — a segunda perderia a mais nova.
        var (repository, _, _) = await NewRepositoryAsync();
        await repository.PublishAsync(Draft(Key, SnapshotFixtures.SimpleDps("V1-")));

        var attempts = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => Task.Run(async () =>
        {
            try
            {
                return (int?)(await repository.PublishAsync(Draft(Key, SnapshotFixtures.SimpleDps($"R{i}-")) with
                {
                    EnforceExpectedActive = true,
                    ExpectedActiveVersion = 1,
                })).Version;
            }
            catch (SnapshotConcurrencyException)
            {
                return null;
            }
        })));

        Assert.Equal([2], attempts.Where(v => v is not null).Select(v => v!.Value));
        Assert.Equal(2, await repository.GetActiveVersionAsync(Key));
        Assert.Equal(2, (await repository.ListVersionsAsync(Key)).Count);

        var stale = await Assert.ThrowsAsync<SnapshotConcurrencyException>(() =>
            repository.PublishAsync(Draft(Key, SnapshotFixtures.SimpleDps("VELHO-")) with { EnforceExpectedActive = true, ExpectedActiveVersion = 1 }));
        Assert.Equal(2, await repository.GetActiveVersionAsync(Key));
        Assert.NotNull(stale);
    }

    [Fact]
    public async Task Concurrent_publications_of_identical_content_converge_on_one_version()
    {
        var (repository, _, _) = await NewRepositoryAsync();
        await repository.PublishAsync(Draft(Key, SnapshotFixtures.SimpleDps("V1-")));

        var published = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => repository.PublishAsync(Draft(Key, SnapshotFixtures.SimpleDps("MESMO-"))))));

        Assert.All(published, p => Assert.Equal(2, p.Version));
        Assert.Equal(2, (await repository.ListVersionsAsync(Key)).Count);
    }

    [Fact]
    public async Task Database_itself_refuses_a_second_active_snapshot_for_the_same_key()
    {
        var (repository, client, options) = await NewRepositoryAsync();
        await repository.PublishAsync(Draft(Key, SnapshotFixtures.SimpleDps()));
        var raw = client.GetDatabase(options.DatabaseName).GetCollection<BsonDocument>(options.SnapshotCollectionName);

        var rogue = new BsonDocument
        {
            { "organizationId", Key.OrganizationId },
            { "templateId", Key.TemplateId },
            { "operation", Key.Operation },
            { "snapshotVersion", 99 },
            { "status", "active" },
        };

        var ex = await Assert.ThrowsAsync<MongoWriteException>(() => raw.InsertOneAsync(rogue));
        Assert.Equal(ServerErrorCategory.DuplicateKey, ex.WriteError.Category);
    }

    [Fact]
    public async Task Change_feed_reports_the_key_of_a_new_publication()
    {
        var (repository, client, options) = await NewRepositoryAsync();
        var feed = new MongoSnapshotChangeFeed(client, options);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await using var enumerator = feed.WatchAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        var next = enumerator.MoveNextAsync().AsTask();

        // O cursor do Change Stream só existe depois do primeiro MoveNext;
        // publica repetidamente até o evento chegar (sem depender de timing).
        while (!next.IsCompleted)
        {
            await repository.PublishAsync(Draft(Key, SnapshotFixtures.SimpleDps("F" + Guid.NewGuid().ToString("N")[..6] + "-")));
            await Task.WhenAny(next, Task.Delay(200, cts.Token));
        }

        Assert.True(await next);
        Assert.Equal(Key, enumerator.Current);
    }
}

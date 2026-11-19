using System.Threading.Channels;
using Tradux.Application.Caching;
using Tradux.Application.Ports;
using Tradux.Application.Snapshots;
using Tradux.Tests.Shared;

namespace Tradux.Application.Tests;

/// <summary>
/// ADR-0005 em nível de unidade: cache aquecido nunca lê o repositório;
/// cache-miss concorrente lê uma vez; invalidação troca a entrada inteira;
/// falha e "não encontrado" não ficam em cache.
/// </summary>
public class CompiledSnapshotCacheTests
{
    private static readonly TemplateKey Key = new(SnapshotFixtures.Organization, "dps", SnapshotFixtures.Operation);

    private static async Task<TemplateSnapshot> PublishDirectAsync(InMemorySnapshotRepository repository, SnapshotContent content)
    {
        var compiler = new SnapshotCompiler();
        var (xsdHash, _) = compiler.CompileContent(content);
        return await repository.PublishAsync(
            new SnapshotDraft(Key, SnapshotCompiler.ComputeContentHash(xsdHash, content), DateTimeOffset.UtcNow, content));
    }

    [Fact]
    public async Task Warm_cache_never_reads_the_repository()
    {
        var repository = new InMemorySnapshotRepository();
        await PublishDirectAsync(repository, SnapshotFixtures.SimpleDps());
        var cache = new CompiledSnapshotCache(repository, new SnapshotCompiler());

        await cache.GetActiveAsync(Key);
        var readsAfterWarmUp = repository.ContentReads;
        for (var i = 0; i < 100; i++)
        {
            Assert.NotNull(await cache.GetActiveAsync(Key));
        }

        Assert.Equal(1, readsAfterWarmUp);
        Assert.Equal(readsAfterWarmUp, repository.ContentReads);
        Assert.Equal(0, repository.PointerReads);
    }

    [Fact]
    public async Task Concurrent_cold_requests_share_a_single_repository_read_and_compilation()
    {
        var repository = new InMemorySnapshotRepository { ReadDelay = TimeSpan.FromMilliseconds(150) };
        await PublishDirectAsync(repository, SnapshotFixtures.SimpleDps());
        var cache = new CompiledSnapshotCache(repository, new SnapshotCompiler());

        var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => cache.GetActiveAsync(Key))));

        Assert.Equal(1, repository.ContentReads);
        Assert.All(results, r => Assert.Same(results[0], r));
    }

    [Fact]
    public async Task Missing_template_is_not_cached_as_negative_result()
    {
        var repository = new InMemorySnapshotRepository();
        var cache = new CompiledSnapshotCache(repository, new SnapshotCompiler());

        Assert.Null(await cache.GetActiveAsync(Key));
        await PublishDirectAsync(repository, SnapshotFixtures.SimpleDps());

        Assert.NotNull(await cache.GetActiveAsync(Key));
        Assert.Equal(2, repository.ContentReads);
    }

    [Fact]
    public async Task Refresh_swaps_the_whole_entry_and_keeps_old_reference_intact()
    {
        var repository = new InMemorySnapshotRepository();
        await PublishDirectAsync(repository, SnapshotFixtures.SimpleDps());
        var cache = new CompiledSnapshotCache(repository, new SnapshotCompiler());
        var v1 = await cache.GetActiveAsync(Key);

        await PublishDirectAsync(repository, SnapshotFixtures.SimpleDps("V2-"));
        Assert.Equal(1, (await cache.GetActiveAsync(Key))!.Version);

        await cache.RefreshActiveAsync(Key);
        var v2 = await cache.GetActiveAsync(Key);

        Assert.Equal(2, v2!.Version);
        Assert.Equal(1, v1!.Version);
        Assert.NotSame(v1.Template, v2.Template);
    }

    [Fact]
    public async Task Refresh_of_a_key_not_in_cache_reads_nothing()
    {
        var repository = new InMemorySnapshotRepository();
        await PublishDirectAsync(repository, SnapshotFixtures.SimpleDps());
        var cache = new CompiledSnapshotCache(repository, new SnapshotCompiler());

        await cache.RefreshActiveAsync(Key);

        Assert.Equal(0, repository.ContentReads);
    }

    [Fact]
    public async Task Active_version_also_answers_pinned_requests_without_another_read()
    {
        var repository = new InMemorySnapshotRepository();
        await PublishDirectAsync(repository, SnapshotFixtures.SimpleDps());
        var cache = new CompiledSnapshotCache(repository, new SnapshotCompiler());

        var active = await cache.GetActiveAsync(Key);
        var pinned = await cache.GetVersionAsync(Key, 1);

        Assert.Same(active, pinned);
        Assert.Equal(1, repository.ContentReads);
    }

    [Fact]
    public async Task Poll_reads_only_the_pointer_when_nothing_changed_and_refreshes_when_it_did()
    {
        var repository = new InMemorySnapshotRepository();
        await PublishDirectAsync(repository, SnapshotFixtures.SimpleDps());
        var cache = new CompiledSnapshotCache(repository, new SnapshotCompiler());
        var invalidator = new SnapshotCacheInvalidator(cache, repository);
        await cache.GetActiveAsync(Key);

        await invalidator.PollOnceAsync();
        Assert.Equal(1, repository.PointerReads);
        Assert.Equal(1, repository.ContentReads);

        await PublishDirectAsync(repository, SnapshotFixtures.SimpleDps("V2-"));
        await invalidator.PollOnceAsync();

        Assert.Equal(2, (await cache.GetActiveAsync(Key))!.Version);
        Assert.Equal(2, repository.ContentReads);
    }

    [Fact]
    public async Task Change_feed_refreshes_the_cache_and_survives_a_feed_failure()
    {
        var repository = new InMemorySnapshotRepository();
        await PublishDirectAsync(repository, SnapshotFixtures.SimpleDps());
        var cache = new CompiledSnapshotCache(repository, new SnapshotCompiler());
        var errors = new List<Exception>();
        var invalidator = new SnapshotCacheInvalidator(cache, repository, errors.Add);
        await cache.GetActiveAsync(Key);

        var feed = new FlakyChannelFeed(failFirstConnection: true);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var running = invalidator.RunChangeFeedAsync(feed, TimeSpan.FromMilliseconds(10), cts.Token);

        await PublishDirectAsync(repository, SnapshotFixtures.SimpleDps("V2-"));
        await feed.Channel.Writer.WriteAsync(Key, cts.Token);

        while ((await cache.GetActiveAsync(Key))!.Version != 2)
        {
            await Task.Delay(10, cts.Token);
        }

        cts.Cancel();
        await running;
        Assert.Single(errors);
        Assert.True(feed.Connections >= 2);
    }

    [Fact]
    public async Task Change_feed_event_alone_refreshes_the_cache_without_polling()
    {
        var repository = new InMemorySnapshotRepository();
        await PublishDirectAsync(repository, SnapshotFixtures.SimpleDps());
        var cache = new CompiledSnapshotCache(repository, new SnapshotCompiler());
        var invalidator = new SnapshotCacheInvalidator(cache, repository);
        await cache.GetActiveAsync(Key);

        var feed = new FlakyChannelFeed(failFirstConnection: false);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var running = invalidator.RunChangeFeedAsync(feed, TimeSpan.FromMinutes(10), cts.Token);

        await PublishDirectAsync(repository, SnapshotFixtures.SimpleDps("V2-"));
        await feed.Channel.Writer.WriteAsync(Key, cts.Token);

        while ((await cache.GetActiveAsync(Key))!.Version != 2)
        {
            await Task.Delay(10, cts.Token);
        }

        cts.Cancel();
        await running;
        Assert.Equal(0, repository.PointerReads);
    }

    /// <summary>Feed que (opcionalmente) falha na primeira conexão e depois entrega o que for escrito no canal.</summary>
    private sealed class FlakyChannelFeed(bool failFirstConnection) : ISnapshotChangeFeed
    {
        public Channel<TemplateKey> Channel { get; } = System.Threading.Channels.Channel.CreateUnbounded<TemplateKey>();

        public int Connections { get; private set; }

        public async IAsyncEnumerable<TemplateKey> WatchAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Connections++;
            if (failFirstConnection && Connections == 1)
            {
                throw new InvalidOperationException("feed indisponível (simulado)");
            }

            await foreach (var key in Channel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return key;
            }
        }
    }
}

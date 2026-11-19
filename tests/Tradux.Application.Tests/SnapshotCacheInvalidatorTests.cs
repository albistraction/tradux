using Tradux.Application.Caching;
using Tradux.Application.Snapshots;

namespace Tradux.Application.Tests;

public class SnapshotCacheInvalidatorTests
{
    [Fact]
    public async Task Polling_interval_must_be_positive()
    {
        var repository = new InMemorySnapshotRepository();
        var invalidator = new SnapshotCacheInvalidator(new CompiledSnapshotCache(repository, new SnapshotCompiler()), repository);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => invalidator.RunPollingAsync(TimeSpan.Zero, CancellationToken.None));
    }
}

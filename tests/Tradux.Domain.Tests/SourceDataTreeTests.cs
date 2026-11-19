using Tradux.Domain.SourceData;
using Xunit;

namespace Tradux.Domain.Tests;

public class SourceDataTreeTests
{
    [Fact]
    public void TryGetValue_distinguishes_explicit_null_from_absent_path()
    {
        var tree = new SourceDataTree(new Dictionary<string, object?> { ["Numero"] = "123", ["Serie"] = null });

        Assert.True(tree.TryGetValue("Serie", out var serie));
        Assert.Null(serie);

        Assert.False(tree.TryGetValue("CampoQueNaoExiste", out var missing));
        Assert.Null(missing);
    }

    [Fact]
    public void Collection_item_that_is_not_an_object_is_its_own_value()
    {
        var root = new SourceDataTree(new Dictionary<string, object?>());
        var nested = new List<object?> { 1m, 2m };

        var tree = SourceDataTree.FromItem(nested, new Dictionary<string, object?> { ["$index"] = 0m }, root);

        Assert.True(tree.TryGetValue(SourceDataTree.ValueKey, out var value));
        Assert.Same(nested, value);
        Assert.False(tree.TryGetValue("Count", out _));
        Assert.Same(root, tree.Root);
    }
}

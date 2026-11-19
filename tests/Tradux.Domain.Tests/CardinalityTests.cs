using Tradux.Domain.Schema;
using Xunit;

namespace Tradux.Domain.Tests;

public class CardinalityTests
{
    [Fact]
    public void Single_is_required_and_not_repeatable()
    {
        var cardinality = Cardinality.Single;

        Assert.True(cardinality.IsRequired);
        Assert.False(cardinality.IsRepeatable);
    }

    [Fact]
    public void Optional_is_not_required()
    {
        Assert.False(Cardinality.Optional.IsRequired);
    }

    [Fact]
    public void Unbounded_max_occurs_is_repeatable()
    {
        var cardinality = new Cardinality(MinOccurs: 0, MaxOccurs: null);

        Assert.True(cardinality.IsRepeatable);
    }
}

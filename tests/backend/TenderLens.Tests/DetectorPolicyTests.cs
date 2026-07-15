using TenderLens.Data;

namespace TenderLens.Tests;

public sealed class DetectorPolicyTests
{
    [Theory]
    [InlineData(5, .70, "Requires review")]
    [InlineData(5, .20, "No signal")]
    [InlineData(2, .90, "Insufficient data")]
    public void Buyer_concentration_has_all_states(int contracts, double share, string expected)
        => Assert.Equal(expected, DetectorPolicy.BuyerConcentration(contracts, (decimal)share));

    [Fact]
    public void Repeated_relationship_has_all_states()
    {
        Assert.Equal("Requires review", DetectorPolicy.RepeatedRelationship(3, .5m, .9m));
        Assert.Equal("No signal", DetectorPolicy.RepeatedRelationship(3, .4m, .9m));
        Assert.Equal("Insufficient data", DetectorPolicy.RepeatedRelationship(3, .5m, null));
    }

    [Fact]
    public void Single_bid_has_all_states()
    {
        Assert.Equal("Requires review", DetectorPolicy.SingleBid(3, .5m, .9m));
        Assert.Equal("No signal", DetectorPolicy.SingleBid(3, .4m, .9m));
        Assert.Equal("Insufficient data", DetectorPolicy.SingleBid(2, .8m, .99m));
    }

    [Fact]
    public void Value_outlier_has_all_states()
    {
        Assert.Equal("Requires review", DetectorPolicy.ValueOutlier(.99m, null));
        Assert.Equal("No signal", DetectorPolicy.ValueOutlier(.5m, 1m));
        Assert.Equal("Insufficient data", DetectorPolicy.ValueOutlier(null, null));
    }

    [Fact]
    public void Amendment_intensity_has_all_states()
    {
        Assert.Equal("Requires review", DetectorPolicy.AmendmentIntensity(3, null));
        Assert.Equal("No signal", DetectorPolicy.AmendmentIntensity(1, .05m));
        Assert.Equal("Insufficient data", DetectorPolicy.AmendmentIntensity(0, null));
    }

    [Theory]
    [InlineData("175074752", true)]
    [InlineData("000000019", true)]
    [InlineData("bad", false)]
    public void Eik_validation_distinguishes_syntax(string value, bool expected)
        => Assert.Equal(expected, Eik.IsValid(value));
}

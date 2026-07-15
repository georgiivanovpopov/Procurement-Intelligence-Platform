namespace TenderLens.Data;

public static class DetectorPolicy
{
    public static string BuyerConcentration(int contracts, decimal share) => contracts < 5 ? "Insufficient data" : share >= .70m ? "Requires review" : "No signal";
    public static string RepeatedRelationship(int awards, decimal valueShare, decimal? percentile) => percentile is null ? "Insufficient data" : awards >= 3 && valueShare >= .50m && percentile >= .90m ? "Requires review" : "No signal";
    public static string SingleBid(int eligibleAwards, decimal share, decimal? percentile) => eligibleAwards < 3 || percentile is null ? "Insufficient data" : share >= .50m && percentile >= .90m ? "Requires review" : "No signal";
    public static string ValueOutlier(decimal? percentile, decimal? robustZ) => percentile is null && robustZ is null ? "Insufficient data" : percentile >= .99m || robustZ >= 3.5m ? "Requires review" : "No signal";
    public static string AmendmentIntensity(int amendments, decimal? growth) => growth is null && amendments == 0 ? "Insufficient data" : amendments >= 3 || growth >= .20m ? "Requires review" : "No signal";
}

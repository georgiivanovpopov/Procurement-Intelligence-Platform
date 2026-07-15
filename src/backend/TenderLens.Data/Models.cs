namespace TenderLens.Data;

public static class SignalKeys
{
    public const string BuyerConcentration = "buyer-concentration";
    public const string RepeatedRelationship = "repeated-relationship";
    public const string SingleBidExposure = "single-bid-exposure";
    public const string ValueOutlier = "contract-value-outlier";
    public const string AmendmentIntensity = "amendment-intensity";
}

public sealed record SnapshotMeta(string SnapshotId, string SnapshotDate, string ObservationStart, string ObservationEnd, string[] SourceFamilies, string SchemaVersion, string DetectorVersion);
public sealed record Money(decimal Amount, string Currency);
public sealed record Metric(string Label, decimal? Value, string? Unit, string State, string Window);
public sealed record SignalSummary(string Key, string Name, string Status, string Explanation, string ObservedValue, string Benchmark, int EvidenceCount);
public sealed record SupplierProfile(string Eik, string Name, string ScopeLabel, SnapshotMeta Coverage, IReadOnlyList<Metric> Metrics, IReadOnlyList<SignalSummary> Signals);
public sealed record EvidenceRow(string RecordId, string Buyer, string Subject, string Cpv, string AwardDate, Money Value, string Contribution);
public sealed record SignalDetail(string Key, string Name, string Status, string ObservedFact, string Trigger, string Formula, string Threshold, string PeerDefinition, int? PeerSize, string ObservationWindow, string Limitations, string Version, IReadOnlyList<EvidenceRow> Evidence);
public sealed record ProvenanceField(string Label, string? SourceValue, string TenderLensUse, string State = "available");
public sealed record Amendment(string Id, string Date, string Description, Money? ValueDelta);
public sealed record SourceRecord(string RecordId, string SupplierEik, string Buyer, string Subject, string Cpv, string AwardDate, Money OriginalValue, string? PublicUrl, IReadOnlyList<ProvenanceField> Fields, IReadOnlyList<Amendment> Amendments);

public sealed class SnapshotException(string message) : Exception(message);

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TenderLens.Data;

var output = args.Length > 0 ? args[0] : Path.Combine("data", "snapshot", "tenderlens.db");
var manifestPath = args.Length > 1 ? args[1] : Path.Combine("data", "manifests", "fixture-manifest.json");
var manifestBytes = File.ReadAllBytes(manifestPath);
using var manifest = JsonDocument.Parse(manifestBytes);
var root = manifest.RootElement;
if (root.GetProperty("schemaVersion").GetString() != "1" || root.GetProperty("sources").GetArrayLength() == 0)
    throw new InvalidDataException("Unsupported or empty acquisition manifest.");
if (root.TryGetProperty("mode", out var mode) && mode.GetString() == "ocdsPortal")
{
    await OcdsImporter.PublishAsync(output, root, manifestBytes);
    return;
}
var coverage = root.GetProperty("coverage");
var sources = root.GetProperty("sources").EnumerateArray().Select(x => x.GetProperty("family").GetString()!).ToArray();
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
var temp = output + ".tmp";
if (File.Exists(temp)) File.Delete(temp);

var meta = new SnapshotMeta(
    Convert.ToHexString(SHA256.HashData([.. manifestBytes, .. Encoding.UTF8.GetBytes("|core-1.0.0")])).ToLowerInvariant()[..16],
    root.GetProperty("retrievedAt").GetString()![..10], coverage.GetProperty("from").GetString()!, coverage.GetProperty("to").GetString()!, sources, "1", "core-1.0.0");

var records = FixtureData.Records();
var profiles = FixtureData.Profiles(meta, records);
var signals = FixtureData.Signals(records);

using (var connection = new SqliteConnection($"Data Source={temp};Pooling=False"))
{
    connection.Open();
    using var command = connection.CreateCommand();
    command.CommandText = """
      create table published_meta(payload text not null);
      create table published_suppliers(eik text primary key, payload text not null);
      create table published_signals(eik text not null, signal_key text not null, payload text not null, primary key(eik, signal_key));
      create table published_records(supplier_eik text not null, record_id text not null, payload text not null, primary key(supplier_eik, record_id));
      create table snapshot_verification(snapshot_id text primary key, verified integer not null check(verified=1));
      """;
    command.ExecuteNonQuery();
    Insert(connection, "insert into published_meta(payload) values($payload)", meta);
    foreach (var profile in profiles) Insert(connection, "insert into published_suppliers(eik,payload) values($id,$payload)", profile, profile.Eik);
    foreach (var (eik, signal) in signals) Insert(connection, "insert into published_signals(eik,signal_key,payload) values($id,$key,$payload)", signal, eik, signal.Key);
    foreach (var record in records) Insert(connection, "insert into published_records(supplier_eik,record_id,payload) values($id,$key,$payload)", record, record.SupplierEik, record.RecordId);
    using var verify = connection.CreateCommand();
    verify.CommandText = "insert into snapshot_verification values($id,1)";
    verify.Parameters.AddWithValue("$id", meta.SnapshotId);
    verify.ExecuteNonQuery();
}
File.Move(temp, output, true);
Console.WriteLine($"Published {output} ({meta.SnapshotId})");

static void Insert<T>(SqliteConnection connection, string sql, T value, string? id = null, string? key = null)
{
    using var command = connection.CreateCommand();
    command.CommandText = sql;
    if (id is not null) command.Parameters.AddWithValue("$id", id);
    if (key is not null) command.Parameters.AddWithValue("$key", key);
    command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(value, JsonOptions.Default));
    command.ExecuteNonQuery();
}

static class FixtureData
{
    private const string Eik = "175074752";
    private const string SparseEik = "000000019";

    public static List<SourceRecord> Records() =>
    [
        Record("CAIS:CT-2025-001", "Столична община", "Поддръжка на градска инфраструктура", "45233141", "2025-02-14", 780000m, 2),
        Record("CAIS:CT-2025-014", "Столична община", "Ремонт на обществени пространства", "45453000", "2025-05-20", 620000m, 1),
        Record("CAIS:CT-2025-031", "Столична община", "Аварийни ремонтни дейности", "45200000", "2025-09-03", 510000m, 3),
        Record("CAIS:CT-2026-004", "Столична община", "Зимна поддръжка", "90620000", "2026-01-18", 440000m, 0),
        Record("CAIS:CT-2026-019", "Община Пловдив", "Текущ ремонт", "45400000", "2026-04-11", 180000m, 0),
    ];

    private static SourceRecord Record(string id, string buyer, string subject, string cpv, string date, decimal amount, int amendmentCount)
        => new(id, Eik, buyer, subject, cpv, date, new Money(amount, "BGN"), $"https://app.eop.bg/today/{Uri.EscapeDataString(id)}",
        [new("Възложена стойност", amount.ToString("0.00"), "Нормализирано десетично число; оригиналната валута е запазена"), new("CPV", cpv, "Класификация за групата за сравнение")],
        Enumerable.Range(1, amendmentCount).Select(i => new Amendment($"{id}-A{i}", date, $"Изменение {i}", i == 1 ? new Money(amount * .08m, "BGN") : null)).ToList());

    public static List<SupplierProfile> Profiles(SnapshotMeta meta, List<SourceRecord> records)
    {
        var signalSummaries = Signals(records).Where(x => x.Eik == Eik).Select(x => new SignalSummary(x.Signal.Key, x.Signal.Name, x.Signal.Status, x.Signal.ObservedFact, x.Signal.ObservedFact, x.Signal.PeerDefinition, x.Signal.Evidence.Count)).ToList();
        return
        [
            new(Eik, "ТЕНДЪР ЛЕНС ДЕМО ООД", "Профил, изведен само от данни за обществени поръчки", meta,
                [new("Обща възложена стойност", records.Sum(r => r.OriginalValue.Amount), "BGN", "available", "24 месеца"), new("Договори", records.Count, "записа", "available", "24 месеца"), new("Различни възложители", records.Select(r => r.Buyer).Distinct().Count(), "възложители", "available", "24 месеца"), new("Изменения", records.Sum(r => r.Amendments.Count), "изменения", "available", "24 месеца")], signalSummaries),
            new(SparseEik, "ДЕМО ДОСТАВЧИК С ОГРАНИЧЕНИ ДАННИ", "Профил, изведен само от данни за обществени поръчки", meta,
                [new("Обща възложена стойност", null, "BGN", "insufficient", "24 месеца"), new("Договори", 1, "запис", "available", "24 месеца")],
                CoreKeys().Select(k => new SignalSummary(k.Key, k.Name, "Insufficient data", "Няма достатъчно допустими записи.", "Не е налично", "Минималните изисквания за допустимост не са изпълнени.", 0)).ToList())
        ];
    }

    public static List<(string Eik, SignalDetail Signal)> Signals(List<SourceRecord> records)
    {
        var total = records.Sum(r => r.OriginalValue.Amount);
        var capital = records.Where(r => r.Buyer == "Столична община").ToList();
        var share = capital.Sum(r => r.OriginalValue.Amount) / total;
        return
        [
            (Eik, Detail(SignalKeys.BuyerConcentration, "Концентрация при възложител", DetectorPolicy.BuyerConcentration(records.Count, share), $"Най-големият възложител представлява {share:P0} от възложената стойност.", "Поне 5 договора и дял ≥ 70%", "стойност при най-големия възложител / обща възложена стойност", "70%", "Договори на доставчика в 24-месечния период", null, "Концентрацията може да е нормална за специализирани пазари.", capital)),
            (Eik, Detail(SignalKeys.RepeatedRelationship, "Повтаряща се връзка възложител–доставчик", DetectorPolicy.RepeatedRelationship(capital.Count, share, .95m), "Четири възлагания са свързани с един и същ възложител.", "≥3 възлагания, ≥50% от стойността и ≥90-и персентил", "стойност на двойката / стойност на доставчика", "90-и персентил", "Сравними договори по CPV и стойностен диапазон", 46, "Сравнението използва детерминистични примерни групи.", capital)),
            (Eik, Detail(SignalKeys.SingleBidExposure, "Участие с една оферта", DetectorPolicy.SingleBid(records.Count, .4m, .80m), "Две от пет допустими възлагания са с една оферта.", "≥50% с една оферта и ≥90-и персентил", "възлагания с една оферта / допустими възлагания", "50% + 90-и персентил", "Възлагания с надеждно определен брой оферти", 51, "Значението на броя оферти варира според процедурата.", records.Take(2).ToList())),
            (Eik, Detail(SignalKeys.ValueOutlier, "Отклонение в стойността на договор", DetectorPolicy.ValueOutlier(.99m, null), "Един договор е над 99-ия персентил на групата.", "≥99-и персентил или устойчив z-резултат ≥3.5", "устойчив персентил в групата", "99-и персентил", "CPV 45 / конфигуриран стойностен диапазон", 64, "Примерните групи демонстрират договора за изчисление.", [records[0]])),
            (Eik, Detail(SignalKeys.AmendmentIntensity, "Интензивност на измененията", DetectorPolicy.AmendmentIntensity(3, null), "Един договор има три изменения.", "≥3 изменения или надежден ръст ≥20%", "брой(изменения)", "3 изменения", "Договори на доставчика с данни за изменения", null, "Липсващите стойности на измененията не участват в изчисляването на ръста.", [records[2]]))
        ];
    }

    private static SignalDetail Detail(string key, string name, string status, string fact, string trigger, string formula, string threshold, string peer, int? peerSize, string limitations, IReadOnlyList<SourceRecord> evidence)
        => new(key, name, status, fact, trigger, formula, threshold, peer, peerSize, "2024-07-16 to 2026-07-15", limitations, "core-1.0.0", evidence.Select(r => new EvidenceRow(r.RecordId, r.Buyer, r.Subject, r.Cpv, r.AwardDate, r.OriginalValue, "Included in the observed calculation")).ToList());

    private static (string Key, string Name)[] CoreKeys() =>
    [
        (SignalKeys.BuyerConcentration, "Концентрация при възложител"), (SignalKeys.RepeatedRelationship, "Повтаряща се връзка възложител–доставчик"),
        (SignalKeys.SingleBidExposure, "Участие с една оферта"), (SignalKeys.ValueOutlier, "Отклонение в стойността на договор"), (SignalKeys.AmendmentIntensity, "Интензивност на измененията")
    ];
}

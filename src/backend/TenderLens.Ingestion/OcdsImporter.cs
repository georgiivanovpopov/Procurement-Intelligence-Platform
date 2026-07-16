using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using TenderLens.Data;

internal static partial class OcdsImporter
{
    public static async Task PublishAsync(string output, JsonElement manifest, byte[] manifestBytes)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("TenderLens/1.0 public-open-data importer");
        var catalogUrl = manifest.GetProperty("catalogUrl").GetString()!;
        var catalog = await http.GetStringAsync(catalogUrl);
        var datasetUrls = DatasetRegex().Matches(catalog).Select(m => Decode(m.Groups[1].Value)).Distinct().ToArray();
        if (datasetUrls.Length == 0) throw new InvalidDataException("No OCDS datasets were discovered.");

        var resourceIds = new HashSet<string>();
        foreach (var datasetUrl in datasetUrls)
        {
            var html = await http.GetStringAsync(datasetUrl);
            foreach (Match match in ResourceRegex().Matches(html)) resourceIds.Add(match.Groups[1].Value);
            var lastPage = PageRegex().Matches(html).Select(m => int.Parse(m.Groups[1].Value)).DefaultIfEmpty(1).Max();
            for (var page = 2; page <= lastPage; page++)
            {
                var pageHtml = await http.GetStringAsync($"{datasetUrl}?rpage={page}");
                foreach (Match match in ResourceRegex().Matches(pageHtml)) resourceIds.Add(match.Groups[1].Value);
            }
        }
        if (resourceIds.Count == 0) throw new InvalidDataException("No OCDS JSON resources were discovered.");

        var records = new Dictionary<string, SourceRecord>(StringComparer.Ordinal);
        var supplierNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var dates = new List<DateTimeOffset>();
        var payloads = new ConcurrentBag<(string Id, byte[] Json)>();
        await Parallel.ForEachAsync(resourceIds, new ParallelOptions { MaxDegreeOfParallelism = 4 }, async (id, cancellationToken) =>
        {
            var json = await http.GetByteArrayAsync($"https://data.egov.bg/resource/download/{id}/json", cancellationToken);
            payloads.Add((id, json));
        });
        foreach (var payload in payloads.OrderBy(x => x.Id))
        {
            using var document = JsonDocument.Parse(payload.Json);
            foreach (var release in document.RootElement.GetProperty("releases").EnumerateArray())
                ReadRelease(release, records, supplierNames, dates);
        }

        var retrieved = DateTimeOffset.UtcNow;
        var start = dates.Count == 0 ? retrieved : dates.Min();
        var end = dates.Count == 0 ? retrieved : dates.Max();
        var fingerprint = Encoding.UTF8.GetBytes($"{Convert.ToHexString(SHA256.HashData(manifestBytes))}|{resourceIds.Count}|{records.Count}|core-1.0.0");
        var meta = new SnapshotMeta(Convert.ToHexString(SHA256.HashData(fingerprint)).ToLowerInvariant()[..16], retrieved.ToString("yyyy-MM-dd"), start.ToString("yyyy-MM-dd"), end.ToString("yyyy-MM-dd"), ["АОП / ЦАИС ЕОП OCDS"], "1", "core-1.0.0");
        Publish(output, meta, records.Values.ToList(), supplierNames);
        Console.WriteLine($"Published {output}: {supplierNames.Count} suppliers, {records.Count} contracts, {resourceIds.Count} OCDS resources ({meta.SnapshotId})");
    }

    private static void ReadRelease(JsonElement release, Dictionary<string, SourceRecord> records, Dictionary<string, string> names, List<DateTimeOffset> dates)
    {
        if (!release.TryGetProperty("awards", out var awards) || !release.TryGetProperty("contracts", out var contracts)) return;
        Dictionary<string, (string Eik, string Name)> parties = release.TryGetProperty("parties", out var partyArray)
            ? partyArray.EnumerateArray().Where(p => p.TryGetProperty("identifier", out var i) && i.TryGetProperty("scheme", out var s) && s.GetString() == "BG-EIK").ToDictionary(p => p.GetProperty("id").GetString()!, p => (Eik: p.GetProperty("identifier").GetProperty("id").GetString()!, Name: p.GetProperty("name").GetString() ?? "Неизвестен доставчик"))
            : [];
        var awardMap = awards.EnumerateArray().ToDictionary(a => a.GetProperty("id").GetString()!, a => a);
        var buyer = release.TryGetProperty("buyer", out var buyerElement) && buyerElement.TryGetProperty("name", out var buyerName) ? buyerName.GetString() ?? "Неизвестен възложител" : "Неизвестен възложител";
        var tender = release.TryGetProperty("tender", out var tenderElement) ? tenderElement : default;
        var subject = tender.ValueKind != JsonValueKind.Undefined && tender.TryGetProperty("title", out var title) ? title.GetString() ?? "Без наименование" : "Без наименование";
        var cpv = GetCpv(tender);
        var date = release.TryGetProperty("date", out var dateElement) && DateTimeOffset.TryParse(dateElement.GetString(), out var parsed) ? parsed : DateTimeOffset.UtcNow;
        dates.Add(date);
        var ocid = release.GetProperty("ocid").GetString()!;
        foreach (var contract in contracts.EnumerateArray())
        {
            if (!contract.TryGetProperty("awardID", out var awardIdElement) || !awardMap.TryGetValue(awardIdElement.GetString()!, out var award) || !contract.TryGetProperty("value", out var value)) continue;
            var amount = value.TryGetProperty("amount", out var amountElement) ? amountElement.GetDecimal() : 0m;
            var currency = value.TryGetProperty("currency", out var currencyElement) ? currencyElement.GetString() ?? "EUR" : "EUR";
            if (!award.TryGetProperty("suppliers", out var suppliers)) continue;
            foreach (var supplier in suppliers.EnumerateArray())
            {
                if (!supplier.TryGetProperty("id", out var supplierId) || !parties.TryGetValue(supplierId.GetString()!, out var party) || !IsEik(party.Eik)) continue;
                names[party.Eik] = party.Name;
                var contractId = contract.TryGetProperty("id", out var cid) ? cid.GetString()! : awardIdElement.GetString()!;
                var recordId = $"{ocid}:{contractId}:{party.Eik}";
                records[recordId] = new SourceRecord(recordId, party.Eik, buyer, subject, cpv, date.ToString("yyyy-MM-dd"), new Money(amount, currency), $"https://app.eop.bg/today/{tender.GetProperty("id").GetString()}", [new("Възложена стойност", amount.ToString("0.00"), "Оригинална стойност от OCDS contract.value"), new("CPV", cpv, "Класификация за сравнение")], []);
            }
        }
    }

    private static string GetCpv(JsonElement tender)
    {
        if (tender.ValueKind != JsonValueKind.Undefined && tender.TryGetProperty("items", out var items))
            foreach (var item in items.EnumerateArray()) if (item.TryGetProperty("classification", out var c) && c.TryGetProperty("id", out var id)) return id.GetString() ?? "Няма данни";
        return "Няма данни";
    }

    private static void Publish(string output, SnapshotMeta meta, List<SourceRecord> records, Dictionary<string, string> names)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        var temp = output + ".tmp"; if (File.Exists(temp)) File.Delete(temp);
        using (var db = new SqliteConnection($"Data Source={temp};Pooling=False"))
        {
            db.Open(); using var schema = db.CreateCommand(); schema.CommandText = "create table published_meta(payload text not null); create table published_suppliers(eik text primary key,payload text not null); create table published_signals(eik text not null,signal_key text not null,payload text not null,primary key(eik,signal_key)); create table published_records(supplier_eik text not null,record_id text not null,payload text not null,primary key(supplier_eik,record_id)); create table snapshot_verification(snapshot_id text primary key,verified integer not null check(verified=1));"; schema.ExecuteNonQuery();
            Insert(db, "insert into published_meta(payload) values($payload)", meta);
            foreach (var group in records.GroupBy(r => r.SupplierEik))
            {
                var supplierRecords = group.ToList(); var signals = BuildSignals(supplierRecords);
                var currencies = supplierRecords.Select(r => r.OriginalValue.Currency).Distinct().ToArray();
                var profile = new SupplierProfile(group.Key, names[group.Key], "Профил от официални OCDS данни на АОП", meta, [new("Обща стойност на договорите", currencies.Length == 1 ? supplierRecords.Sum(r => r.OriginalValue.Amount) : null, currencies.Length == 1 ? currencies[0] : null, currencies.Length == 1 ? "available" : "insufficient", "наличен OCDS период"), new("Договори", supplierRecords.Count, "записа", "available", "наличен OCDS период"), new("Различни възложители", supplierRecords.Select(r => r.Buyer).Distinct().Count(), "възложители", "available", "наличен OCDS период")], signals.Select(s => new SignalSummary(s.Key, s.Name, s.Status, s.ObservedFact, s.ObservedFact, s.PeerDefinition, s.Evidence.Count)).ToList());
                Insert(db, "insert into published_suppliers values($id,$payload)", profile, group.Key);
                foreach (var signal in signals) Insert(db, "insert into published_signals values($id,$key,$payload)", signal, group.Key, signal.Key);
                foreach (var record in supplierRecords) Insert(db, "insert into published_records values($id,$key,$payload)", record, group.Key, record.RecordId);
            }
            using var verify = db.CreateCommand(); verify.CommandText = "insert into snapshot_verification values($id,1)"; verify.Parameters.AddWithValue("$id", meta.SnapshotId); verify.ExecuteNonQuery();
        }
        File.Move(temp, output, true);
    }

    private static List<SignalDetail> BuildSignals(List<SourceRecord> records)
    {
        var largest = records.GroupBy(r => r.Buyer).OrderByDescending(g => g.Sum(r => r.OriginalValue.Amount)).First();
        var currencies = records.Select(r => r.OriginalValue.Currency).Distinct().ToArray();
        var total = records.Sum(r => r.OriginalValue.Amount); var share = total == 0 ? 0 : largest.Sum(r => r.OriginalValue.Amount) / total;
        SignalDetail D(string key,string name,string status,string fact,IReadOnlyList<SourceRecord> evidence) => new(key,name,status,fact,"Детерминистично правило върху наличния OCDS период","виж описанието","конфигуриран праг","Договори на доставчика в наличния OCDS период",null,"наличен OCDS период","OCDS не съдържа всички полета за пълна секторна оценка.","core-1.0.0",evidence.Select(r=>new EvidenceRow(r.RecordId,r.Buyer,r.Subject,r.Cpv,r.AwardDate,r.OriginalValue,"Участва в изчислението")).ToList());
        return [D(SignalKeys.BuyerConcentration,"Концентрация при възложител",currencies.Length == 1 ? DetectorPolicy.BuyerConcentration(records.Count,share) : "Insufficient data",currencies.Length == 1 ? $"Най-големият възложител представлява {share:P0} от стойността." : "Договорите са в различни валути и не се сумират.",currencies.Length == 1 ? largest.ToList() : []), D(SignalKeys.RepeatedRelationship,"Повтаряща се връзка възложител–доставчик",records.Count>=3?"Requires review":"Insufficient data",$"Най-честият възложител има {largest.Count()} договора.",largest.ToList()), D(SignalKeys.SingleBidExposure,"Участие с една оферта","Insufficient data","Няма надеждни данни за броя оферти.",[]), D(SignalKeys.ValueOutlier,"Отклонение в стойността на договор","Insufficient data","Секторната peer група още не е достатъчна.",[]), D(SignalKeys.AmendmentIntensity,"Интензивност на измененията","Insufficient data","Измененията не са нормализирани надеждно.",[])];
    }

    private static void Insert<T>(SqliteConnection db,string sql,T value,string? id=null,string? key=null){using var c=db.CreateCommand();c.CommandText=sql;if(id is not null)c.Parameters.AddWithValue("$id",id);if(key is not null)c.Parameters.AddWithValue("$key",key);c.Parameters.AddWithValue("$payload",JsonSerializer.Serialize(value,JsonOptions.Default));c.ExecuteNonQuery();}
    private static string Decode(string value) => System.Net.WebUtility.HtmlDecode(value);
    private static bool IsEik(string value) => value.Length is 9 or 13 && value.All(char.IsDigit);
    [GeneratedRegex("(https://data\\.egov\\.bg/data/view/[0-9a-f-]+)", RegexOptions.IgnoreCase)] private static partial Regex DatasetRegex();
    [GeneratedRegex("/data/resourceView/([0-9a-f-]+)", RegexOptions.IgnoreCase)] private static partial Regex ResourceRegex();
    [GeneratedRegex("[?&]rpage=(\\d+)", RegexOptions.IgnoreCase)] private static partial Regex PageRegex();
}

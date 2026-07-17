using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TenderLens.Data;

public static class HistoricalImporter
{
    private const string SchemaVersion = "2";
    private const string DetectorVersion = "core-2.0.0";

    public static async Task PublishAsync(string output, string manifestPath, JsonElement manifest, byte[] manifestBytes)
    {
        var resources = ReadResources(manifest, manifestPath);
        if (resources.Count == 0) throw new InvalidDataException("Historical manifest has no resources.");
        ValidateCoverage(resources);
        var missingYears = Enumerable.Range(2020, DateTime.UtcNow.Year - 2019)
            .Where(year => year < DateTime.UtcNow.Year && !resources.Any(r => r.Year == year && r.Kind == "contracts" && r.Required))
            .ToArray();
        if (missingYears.Length > 0) throw new InvalidDataException($"Required contract coverage is missing for: {string.Join(", ", missingYears)}.");

        foreach (var resource in resources) VerifyResource(resource);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        var staging = output + ".building";
        if (File.Exists(staging)) File.Delete(staging);
        try
        {
            using var db = new SqliteConnection($"Data Source={staging};Pooling=False");
            await db.OpenAsync();
            CreateSchema(db);
            foreach (var resource in resources.OrderBy(r => r.Year).ThenBy(r => r.Id, StringComparer.Ordinal))
                if (resource.Kind == "snapshot") await ImportSnapshotAsync(db, resource);
                else await ImportCsvAsync(db, resource);
            Materialize(db, manifestBytes, resources);
            VerifySnapshot(db);
            db.Close();
            File.Move(staging, output, true);
        }
        catch
        {
            if (File.Exists(staging)) File.Delete(staging);
            throw;
        }
    }

    private static List<Resource> ReadResources(JsonElement manifest, string manifestPath)
    {
        var root = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        return manifest.GetProperty("resources").EnumerateArray().Select(x => new Resource(
            x.GetProperty("id").GetString()!, NormalizeFamily(x.GetProperty("id").GetString()!, x.GetProperty("family").GetString()!),
            x.GetProperty("kind").GetString()!, x.GetProperty("year").GetInt32(),
            x.GetProperty("url").GetString()!, Path.GetFullPath(Path.Combine(root, x.GetProperty("localPath").GetString()!)),
            x.GetProperty("sha256").GetString()!.ToLowerInvariant(), x.GetProperty("bytes").GetInt64(),
            !x.TryGetProperty("required", out var required) || required.GetBoolean(),
            x.TryGetProperty("retrievedAt", out var retrieved) ? retrieved.GetString()! : "unknown")).ToList();
    }

    private static void VerifyResource(Resource resource)
    {
        if (!Uri.TryCreate(resource.Url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.Host.EndsWith("egov.bg", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Resource {resource.Id} is not an official HTTPS egov.bg source.");
        if (!File.Exists(resource.Path)) throw new FileNotFoundException($"Required resource {resource.Id} is unavailable.", resource.Path);
        using var stream = File.OpenRead(resource.Path);
        if (stream.Length != resource.Bytes) throw new InvalidDataException($"Size mismatch for {resource.Id}.");
        var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(actual), Encoding.ASCII.GetBytes(resource.Sha256)))
            throw new InvalidDataException($"SHA-256 mismatch for {resource.Id}.");
    }

    private static void ValidateCoverage(List<Resource> resources)
    {
        string[] officialHistorical = [
            "fdcc0b4d-f0f6-44f4-b917-638376d1fcf1", "3cf24c43-84af-4cf4-818f-3a45b614e939", "dc1c36c6-8332-4590-8a31-855cae94d5e0", "8c02b1bf-bf31-413a-8492-6ad9da7deee2",
            "ec252e14-71e9-4448-8227-7cae01caada9", "1c1a3e5f-9b75-447b-83e3-a639a4854e20", "0fadfb4a-7361-47ac-a455-7b490ea4e73a", "6a350fc7-7fd8-4696-9a16-ffb2b5d9d5bb",
            "03e63b8c-c0e4-4e44-a777-3a751c3f735d", "2daf2219-b98f-49c3-863b-e89f84a2fddd", "181a183f-aec9-46de-a724-48cf5f7a5362", "958f2f3e-d429-4314-a9e4-6ab4202dbc16",
            "0809e6c3-90b2-4fcd-889c-9130f8dabfbe", "d67457ff-86f0-4501-90db-65497336d3cd", "34a9066f-d467-4921-a427-d41447bb2d8c", "8f04fb86-e307-4f4e-9a1d-fb967e4dcbaa",
            "dcd45fa9-eaec-4ee9-a908-054fada944b1", "75eecda0-2313-4301-9669-1c8421cc48e0", "94c99c57-a07b-4838-aba2-ab085e247aca", "2e1c7c3f-d4c7-49be-92f3-eff90ede1da1"];
        if (!resources.Any(r => officialHistorical.Contains(r.Id, StringComparer.OrdinalIgnoreCase))) return;
        var missing = officialHistorical.Where(id => !resources.Any(r => r.Required && r.Id.Equals(id, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (missing.Length > 0) throw new InvalidDataException($"Required official resources are missing: {string.Join(", ", missing)}.");
        if (!resources.Any(r => r.Required && r.Kind == "snapshot" && r.Year == DateTime.UtcNow.Year))
            throw new InvalidDataException($"A required current OCDS snapshot for {DateTime.UtcNow.Year} is missing.");
    }

    private static void CreateSchema(SqliteConnection db)
    {
        Execute(db, """
          pragma journal_mode=delete; pragma synchronous=full; pragma temp_store=file;
          create table stage_contract(
            stable_key text not null, revision_date text not null, source_order integer not null, row_no integer not null,
            supplier_eik text not null, supplier_name text not null, buyer text not null, subject text not null,
            cpv text, award_date text not null, amount text, currency text, public_url text, source_id text not null,
            primary key(source_id,row_no,supplier_eik));
          create table stage_amendment(contract_key text not null, amendment_id text not null, amendment_date text,
            description text, amount_delta text, currency text, source_id text not null, row_no integer not null,
            primary key(source_id,row_no));
          create table source_inventory(source_id text primary key,family text not null,kind text not null,year integer not null,
            url text not null,retrieved_at text not null,bytes integer not null,sha256 text not null,rows_read integer not null,rows_excluded integer not null);
          """);
    }

    private static async Task ImportCsvAsync(SqliteConnection db, Resource resource)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encoding = DetectEncoding(resource.Path);
        using var reader = new StreamReader(resource.Path, encoding, true, 64 * 1024);
        var rows = Csv.Read(reader).GetEnumerator();
        if (!rows.MoveNext()) throw new InvalidDataException($"Resource {resource.Id} is empty.");
        var headers = rows.Current.Select(NormalizeHeader).ToArray();
        var aliases = new Columns(headers);
        long read = 0, excluded = 0, missingKey = 0, missingDate = 0, missingEik = 0;
        using var transaction = db.BeginTransaction();
        while (rows.MoveNext())
        {
            read++;
            try
            {
                var row = rows.Current;
                if (resource.Kind == "contracts")
                {
                    var key = StableKey(resource, aliases.Value(row, "procedure_id"), aliases.Value(row, "contract_id"));
                    var date = Date(aliases.Value(row, "award_date"));
                    var eiks = SplitEiks(aliases.Value(row, "supplier_eik"));
                    if (string.IsNullOrWhiteSpace(key) || date is null || string.CompareOrdinal(date, "2020-01-01") < 0 || eiks.Count == 0)
                    {
                        excluded++;
                        if (string.IsNullOrWhiteSpace(key)) missingKey++;
                        if (date is null) missingDate++;
                        if (eiks.Count == 0) missingEik++;
                        continue;
                    }
                    foreach (var eik in eiks)
                        InsertContract(db, transaction, resource, read, key, date, eik, aliases, row);
                }
                else if (resource.Kind == "amendments")
                {
                    var key = StableKey(resource, aliases.Value(row, "procedure_id"), aliases.Value(row, "contract_id"));
                    if (string.IsNullOrWhiteSpace(key)) { excluded++; continue; }
                    InsertAmendment(db, transaction, resource, read, key, aliases, row);
                }
                else throw new InvalidDataException($"Unsupported resource kind {resource.Kind}.");
            }
            catch (Exception ex) when (ex is FormatException or OverflowException) { excluded++; }
        }
        using var inventory = db.CreateCommand(); inventory.Transaction = transaction;
        inventory.CommandText = "insert into source_inventory values($id,$family,$kind,$year,$url,$retrieved,$bytes,$sha,$read,$excluded)";
        Add(inventory, "$id", resource.Id); Add(inventory, "$family", resource.Family); Add(inventory, "$kind", resource.Kind);
        Add(inventory, "$year", resource.Year); Add(inventory, "$url", resource.Url); Add(inventory, "$retrieved", resource.RetrievedAt);
        Add(inventory, "$bytes", resource.Bytes); Add(inventory, "$sha", resource.Sha256); Add(inventory, "$read", read); Add(inventory, "$excluded", excluded);
        inventory.ExecuteNonQuery(); transaction.Commit();
        Console.WriteLine($"Imported {resource.Year} {resource.Kind} {resource.Id}: {read - excluded} accepted, {excluded} excluded (key={missingKey}, date={missingDate}, eik={missingEik}).");
        await Task.CompletedTask;
    }

    private static async Task ImportSnapshotAsync(SqliteConnection db, Resource resource)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        using var source = new SqliteConnection($"Data Source={resource.Path};Mode=ReadOnly;Pooling=False");
        await source.OpenAsync();
        using (var profiles = source.CreateCommand())
        {
            profiles.CommandText = "select eik,payload from published_suppliers";
            using var reader = await profiles.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var profile = JsonSerializer.Deserialize<SupplierProfile>(reader.GetString(1), JsonOptions.Default);
                if (profile is not null) names[reader.GetString(0)] = profile.Name;
            }
        }
        long rows = 0, excluded = 0, amendmentRows = 0;
        using var transaction = db.BeginTransaction();
        using (var records = source.CreateCommand())
        {
            records.CommandText = "select supplier_eik,payload from published_records order by supplier_eik,record_id";
            using var reader = await records.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                rows++;
                var record = JsonSerializer.Deserialize<SourceRecord>(reader.GetString(1), JsonOptions.Default);
                var awardDate = Date(record?.AwardDate);
                if (record is null || awardDate is null || string.CompareOrdinal(awardDate, "2020-01-01") < 0) { excluded++; continue; }
                var eik = reader.GetString(0);
                using var contract = db.CreateCommand(); contract.Transaction = transaction;
                contract.CommandText = "insert into stage_contract values($key,$revision,$order,$row,$eik,$name,$buyer,$subject,$cpv,$date,$amount,$currency,$url,$source)";
                Add(contract,"$key",record.RecordId); Add(contract,"$revision",awardDate); Add(contract,"$order",resource.Year); Add(contract,"$row",rows);
                Add(contract,"$eik",eik); Add(contract,"$name",names.GetValueOrDefault(eik,"Неизвестен доставчик")); Add(contract,"$buyer",record.Buyer);
                Add(contract,"$subject",record.Subject); Add(contract,"$cpv",record.Cpv); Add(contract,"$date",awardDate);
                Add(contract,"$amount",record.OriginalValue.Currency == "UNKNOWN" ? null : record.OriginalValue.Amount.ToString(CultureInfo.InvariantCulture));
                Add(contract,"$currency",record.OriginalValue.Currency); Add(contract,"$url",record.PublicUrl ?? resource.Url); Add(contract,"$source",resource.Id); contract.ExecuteNonQuery();
                foreach (var amendment in record.Amendments)
                {
                    amendmentRows++;
                    using var command = db.CreateCommand(); command.Transaction = transaction;
                    command.CommandText = "insert into stage_amendment values($key,$id,$date,$description,$delta,$currency,$source,$row)";
                    Add(command,"$key",record.RecordId); Add(command,"$id",amendment.Id); Add(command,"$date",amendment.Date); Add(command,"$description",amendment.Description);
                    Add(command,"$delta",amendment.ValueDelta?.Amount.ToString(CultureInfo.InvariantCulture)); Add(command,"$currency",amendment.ValueDelta?.Currency);
                    Add(command,"$source",resource.Id); Add(command,"$row",amendmentRows); command.ExecuteNonQuery();
                }
            }
        }
        using var inventory = db.CreateCommand(); inventory.Transaction = transaction;
        inventory.CommandText = "insert into source_inventory values($id,$family,$kind,$year,$url,$retrieved,$bytes,$sha,$read,$excluded)";
        Add(inventory,"$id",resource.Id); Add(inventory,"$family",resource.Family); Add(inventory,"$kind",resource.Kind); Add(inventory,"$year",resource.Year);
        Add(inventory,"$url",resource.Url); Add(inventory,"$retrieved",resource.RetrievedAt); Add(inventory,"$bytes",resource.Bytes); Add(inventory,"$sha",resource.Sha256);
        Add(inventory,"$read",rows); Add(inventory,"$excluded",excluded); inventory.ExecuteNonQuery(); transaction.Commit();
        Console.WriteLine($"Imported {resource.Year} current snapshot {resource.Id}: {rows - excluded} accepted, {excluded} excluded.");
    }

    private static void InsertContract(SqliteConnection db, SqliteTransaction tx, Resource r, long rowNo, string key, string date, string eik, Columns c, string[] row)
    {
        using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = """
          insert into stage_contract values($key,$revision,$order,$row,$eik,$name,$buyer,$subject,$cpv,$date,$amount,$currency,$url,$source)
          """;
        Add(cmd,"$key",key.Trim()); Add(cmd,"$revision",Date(c.Value(row,"revision_date")) ?? date); Add(cmd,"$order",r.Year);
        Add(cmd,"$row",rowNo); Add(cmd,"$eik",eik); Add(cmd,"$name",c.Value(row,"supplier_name") ?? "Неизвестен доставчик");
        Add(cmd,"$buyer",c.Value(row,"buyer") ?? "Неизвестен възложител"); Add(cmd,"$subject",c.Value(row,"subject") ?? "Без наименование");
        Add(cmd,"$cpv",c.Value(row,"cpv")); Add(cmd,"$date",date); Add(cmd,"$amount",Decimal(c.Value(row,"amount"))?.ToString(CultureInfo.InvariantCulture));
        Add(cmd,"$currency",Currency(c.Value(row,"currency"))); Add(cmd,"$url",c.Value(row,"public_url") ?? r.Url); Add(cmd,"$source",r.Id); cmd.ExecuteNonQuery();
    }

    private static void InsertAmendment(SqliteConnection db, SqliteTransaction tx, Resource r, long rowNo, string key, Columns c, string[] row)
    {
        using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "insert into stage_amendment values($key,$id,$date,$description,$delta,$currency,$source,$row)";
        Add(cmd,"$key",key.Trim()); Add(cmd,"$id",c.Value(row,"amendment_id") ?? $"{r.Id}:{rowNo}"); Add(cmd,"$date",Date(c.Value(row,"amendment_date")));
        Add(cmd,"$description",c.Value(row,"description") ?? "Изменение на договор"); Add(cmd,"$delta",Decimal(c.Value(row,"amount_delta"))?.ToString(CultureInfo.InvariantCulture));
        Add(cmd,"$currency",Currency(c.Value(row,"currency"))); Add(cmd,"$source",r.Id); Add(cmd,"$row",rowNo); cmd.ExecuteNonQuery();
    }

    private static void Materialize(SqliteConnection db, byte[] manifestBytes, List<Resource> resources)
    {
        Execute(db, """
          create table published_meta(payload text not null);
          create table published_suppliers(eik text primary key,payload text not null);
          create table published_signals(eik text not null,signal_key text not null,payload text not null,primary key(eik,signal_key));
          create table published_records(supplier_eik text not null,record_id text not null,payload text not null,primary key(supplier_eik,record_id));
          create table snapshot_verification(snapshot_id text primary key,verified integer not null check(verified=1));
          create index stage_amendment_contract_key on stage_amendment(contract_key);
          create temp table winning_revision as
          select stable_key,revision_date,source_order,source_id,row_no from (
            select stable_key,revision_date,source_order,source_id,row_no,
              row_number() over(partition by stable_key order by revision_date desc,source_order desc,source_id desc,row_no desc) rank
            from stage_contract group by stable_key,revision_date,source_order,source_id,row_no) where rank=1;
          create temp table winning_contract as
          select c.* from stage_contract c join winning_revision w on c.stable_key=w.stable_key and c.revision_date=w.revision_date
            and c.source_order=w.source_order and c.source_id=w.source_id and c.row_no=w.row_no;
          create index winning_contract_supplier on winning_contract(supplier_eik);
          create index winning_contract_stable_key on winning_contract(stable_key);
          """);
        Execute(db, "begin immediate");
        var manifestHash = Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant();
        var identityBytes = Encoding.UTF8.GetBytes($"{manifestHash}|{SchemaVersion}|{DetectorVersion}");
        var snapshotId = Convert.ToHexString(SHA256.HashData(identityBytes)).ToLowerInvariant()[..16];
        var dates = QueryStrings(db,"select award_date from winning_contract order by award_date");
        if (dates.Count == 0) throw new InvalidDataException("No eligible historical contracts were imported.");
        var families = resources.Select(r=>r.Family).Distinct(StringComparer.Ordinal).OrderBy(x=>x,StringComparer.Ordinal).ToArray();
        var latestRetrieval = resources.Select(r => r.RetrievedAt).OrderBy(x => x, StringComparer.Ordinal).Last();
        var meta = new SnapshotMeta(snapshotId, latestRetrieval[..Math.Min(10, latestRetrieval.Length)], dates[0], dates[^1], families, SchemaVersion, DetectorVersion);
        InsertJson(db,"insert into published_meta values($payload)",meta);
        foreach (var eik in QueryStrings(db,"select distinct supplier_eik from winning_contract order by supplier_eik"))
        {
            var records = ReadRecords(db,eik); var name = Scalar(db,"select supplier_name from winning_contract where supplier_eik=$eik order by revision_date desc limit 1",eik)!;
            var signals = BuildSignals(records);
            var currencies=records.Where(x=>x.OriginalValue.Currency != "UNKNOWN").Select(x=>x.OriginalValue.Currency).Distinct().ToArray();
            var metrics = new List<Metric>{new("Договори",records.Count,"записа","available","2020–текущ snapshot"),new("Различни възложители",records.Select(x=>x.Buyer).Distinct().Count(),"възложители","available","2020–текущ snapshot"),new("Изменения",records.Sum(x=>x.Amendments.Count),"изменения","available","2020–текущ snapshot")};
            metrics.Insert(0,new("Обща стойност на договорите",currencies.Length==1?records.Sum(x=>x.OriginalValue.Amount):null,currencies.Length==1?currencies[0]:null,currencies.Length==1?"available":"insufficient","2020–текущ snapshot"));
            var profile=new SupplierProfile(eik,name,"Профил от официални исторически данни на АОП",meta,metrics,signals.Select(s=>new SignalSummary(s.Key,s.Name,s.Status,s.ObservedFact,s.ObservedFact,s.PeerDefinition,s.Evidence.Count)).ToList());
            InsertJson(db,"insert into published_suppliers values($id,$payload)",profile,eik);
            foreach(var signal in signals) InsertJson(db,"insert into published_signals values($id,$key,$payload)",signal,eik,signal.Key);
            foreach(var record in records) InsertJson(db,"insert into published_records values($id,$key,$payload)",record,eik,record.RecordId);
        }
        using var verified=db.CreateCommand(); verified.CommandText="insert into snapshot_verification values($id,1)"; Add(verified,"$id",snapshotId); verified.ExecuteNonQuery();
        Execute(db, "commit");
    }

    private static List<SourceRecord> ReadRecords(SqliteConnection db,string eik)
    {
        using var cmd=db.CreateCommand(); cmd.CommandText="select stable_key,buyer,subject,coalesce(cpv,'Няма данни'),award_date,amount,currency,public_url,source_id from winning_contract where supplier_eik=$eik order by award_date,stable_key"; Add(cmd,"$eik",eik);
        using var reader=cmd.ExecuteReader(); var result=new List<SourceRecord>();
        while(reader.Read())
        {
            var key=reader.GetString(0); var amountKnown=!reader.IsDBNull(5); var amount=amountKnown?decimal.Parse(reader.GetString(5),CultureInfo.InvariantCulture):0m; var currency=amountKnown&&!reader.IsDBNull(6)?reader.GetString(6):"UNKNOWN";
            result.Add(new SourceRecord(PublicRecordId(key),eik,reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.GetString(4),new Money(amount,currency),reader.IsDBNull(7)?null:reader.GetString(7),
                [new("Източник",reader.GetString(8),"Идентификатор на manifest-bound ресурс"),new("Възложена стойност",reader.IsDBNull(5)?null:reader.GetString(5),"Оригинална стойност; липсващото не е нула",reader.IsDBNull(5)?"unavailable":"available")],ReadAmendments(db,key)));
        }
        return result;
    }

    private static List<Amendment> ReadAmendments(SqliteConnection db,string key)
    {
        using var cmd=db.CreateCommand(); cmd.CommandText="select amendment_id,coalesce(amendment_date,''),coalesce(description,'Изменение на договор'),amount_delta,currency from (select *,row_number() over(partition by amendment_id order by amendment_date desc,source_id desc,row_no desc) rank from stage_amendment where contract_key=$key) where rank=1 order by amendment_date,amendment_id"; Add(cmd,"$key",key);
        using var reader=cmd.ExecuteReader(); var result=new List<Amendment>(); while(reader.Read()) result.Add(new(reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.IsDBNull(3)?null:new Money(decimal.Parse(reader.GetString(3),CultureInfo.InvariantCulture),reader.IsDBNull(4)?"UNKNOWN":reader.GetString(4)))); return result;
    }

    private static List<SignalDetail> BuildSignals(List<SourceRecord> records)
    {
        var known=records.Where(r=>r.OriginalValue.Currency!="UNKNOWN").ToList(); var currencies=known.Select(r=>r.OriginalValue.Currency).Distinct().ToArray();
        var largest=records.GroupBy(r=>r.Buyer).OrderByDescending(g=>g.Count()).ThenBy(g=>g.Key,StringComparer.Ordinal).First();
        var monetary=currencies.Length==1; var total=monetary?known.Sum(r=>r.OriginalValue.Amount):0; var top=monetary?known.GroupBy(r=>r.Buyer).OrderByDescending(g=>g.Sum(x=>x.OriginalValue.Amount)).First():largest; var share=total==0?0:top.Sum(x=>x.OriginalValue.Amount)/total;
        SignalDetail D(string key,string name,string status,string fact,IReadOnlyList<SourceRecord> evidence,string limitations)=>new(key,name,status,fact,"Детерминистично правило върху manifest-bound исторически записи","виж описанието","конфигуриран праг","Договори на доставчика от 2020 г. насам",null,$"{records.Min(r=>r.AwardDate)} to {records.Max(r=>r.AwardDate)}",limitations,DetectorVersion,evidence.Select(r=>new EvidenceRow(r.RecordId,r.Buyer,r.Subject,r.Cpv,r.AwardDate,r.OriginalValue,"Участва в изчислението")).ToList());
        var amended=records.Where(r=>r.Amendments.Count>=3).ToList();
        return [D(SignalKeys.BuyerConcentration,"Концентрация при възложител",monetary?DetectorPolicy.BuyerConcentration(records.Count,share):"Insufficient data",monetary?$"Най-големият възложител представлява {share:P0} от стойността.":"Липсват стойности или има несъвместими валути.",monetary?top.ToList():[],"Не се сумират различни или неизвестни валути."),D(SignalKeys.RepeatedRelationship,"Повтаряща се връзка възложител–доставчик",largest.Count()>=3?"Requires review":"No signal",$"Най-честият възложител има {largest.Count()} договора.",largest.ToList(),"Честотата сама по себе си не доказва нарушение."),D(SignalKeys.SingleBidExposure,"Участие с една оферта","Insufficient data","Историческите договорни CSV не съдържат надежден брой оферти.",[],"Не се извеждат липсващи оферти."),D(SignalKeys.ValueOutlier,"Отклонение в стойността на договор","Insufficient data","Секторните peer групи не са материализирани надеждно.",[],"Непълен CPV ограничава сравнението."),D(SignalKeys.AmendmentIntensity,"Интензивност на измененията",amended.Count>0?"Requires review":"No signal",$"{amended.Count} договора имат поне три изменения.",amended,"Наличността на анекси варира по източник и година.")];
    }

    private static void VerifySnapshot(SqliteConnection db)
    {
        Execute(db,"pragma optimize");
        if(Scalar(db,"pragma integrity_check")!="ok") throw new InvalidDataException("SQLite integrity verification failed.");
        if(long.Parse(Scalar(db,"select count(*) from published_suppliers")!)==0) throw new InvalidDataException("Snapshot contains no suppliers.");
        if(long.Parse(Scalar(db,"select count(*) from snapshot_verification where verified=1")!)!=1) throw new InvalidDataException("Snapshot verification marker is missing.");
    }

    private static Encoding DetectEncoding(string path){using var s=File.OpenRead(path);var b=new byte[Math.Min(65536,(int)Math.Min(int.MaxValue,s.Length))];var n=s.Read(b);if(n>=3&&b[0]==0xef&&b[1]==0xbb&&b[2]==0xbf)return new UTF8Encoding(true);try{_ = new UTF8Encoding(false,true).GetString(b,0,n);return new UTF8Encoding(false,true);}catch(DecoderFallbackException){return Encoding.GetEncoding(1251);}}
    private static string NormalizeFamily(string id, string family)
    {
        string[] cais = ["fdcc0b4d-f0f6-44f4-b917-638376d1fcf1", "3cf24c43-84af-4cf4-818f-3a45b614e939", "ec252e14-71e9-4448-8227-7cae01caada9", "1c1a3e5f-9b75-447b-83e3-a639a4854e20", "03e63b8c-c0e4-4e44-a777-3a751c3f735d", "2daf2219-b98f-49c3-863b-e89f84a2fddd", "0809e6c3-90b2-4fcd-889c-9130f8dabfbe", "d67457ff-86f0-4501-90db-65497336d3cd"];
        string[] rop = ["dc1c36c6-8332-4590-8a31-855cae94d5e0", "8c02b1bf-bf31-413a-8492-6ad9da7deee2", "0fadfb4a-7361-47ac-a455-7b490ea4e73a", "6a350fc7-7fd8-4696-9a16-ffb2b5d9d5bb", "181a183f-aec9-46de-a724-48cf5f7a5362", "958f2f3e-d429-4314-a9e4-6ab4202dbc16", "34a9066f-d467-4921-a427-d41447bb2d8c", "8f04fb86-e307-4f4e-9a1d-fb967e4dcbaa", "dcd45fa9-eaec-4ee9-a908-054fada944b1", "75eecda0-2313-4301-9669-1c8421cc48e0", "94c99c57-a07b-4838-aba2-ab085e247aca", "2e1c7c3f-d4c7-49be-92f3-eff90ede1da1"];
        return cais.Contains(id, StringComparer.OrdinalIgnoreCase) ? "AOP / CAIS EOP CSV"
            : rop.Contains(id, StringComparer.OrdinalIgnoreCase) ? "AOP / ROP CSV" : family;
    }
    private static string NormalizeHeader(string value)=>new(value.Trim().Trim('\uFEFF').ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
    private static string? StableKey(Resource resource,string? procedure,string? contract)=>string.IsNullOrWhiteSpace(contract)?null:string.IsNullOrWhiteSpace(procedure)?$"{resource.Family}\u001f{contract.Trim()}":$"{procedure.Trim()}:{contract.Trim()}";
    private static string PublicRecordId(string stableKey){var separator=stableKey.IndexOf('\u001f');return separator<0?stableKey:stableKey[(separator+1)..];}
    private static string? Date(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var formats = new[] { "dd/MM/yyyy", "d/M/yyyy", "dd.MM.yyyy", "d.M.yyyy", "yyyy-MM-dd", "yyyy-MM-ddTHH:mm:ss", "O" };
        return DateTime.TryParseExact(value.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var exact)
            || DateTime.TryParse(value, CultureInfo.GetCultureInfo("bg-BG"), DateTimeStyles.AllowWhiteSpaces, out exact)
            ? exact.ToString("yyyy-MM-dd") : null;
    }
    private static decimal? Decimal(string? value){if(string.IsNullOrWhiteSpace(value))return null;value=value.Trim().Replace(" ","").Replace("\u00a0","");if(decimal.TryParse(value,NumberStyles.Number,CultureInfo.GetCultureInfo("bg-BG"),out var bg))return bg;if(decimal.TryParse(value,NumberStyles.Number,CultureInfo.GetCultureInfo("en-US"),out var en))return en;return null;}
    private static string? Currency(string? value)=>string.IsNullOrWhiteSpace(value)?null:value.Trim().ToUpperInvariant() switch{"ЛВ" or "ЛЕВА" or "BGN"=>"BGN","€" or "EUR"=>"EUR",var x=>x};
    private static List<string> SplitEiks(string? value)=>string.IsNullOrWhiteSpace(value)?[]:System.Text.RegularExpressions.Regex.Matches(value,@"(?<!\d)(\d{9}|\d{13})(?!\d)").Select(m=>m.Value).Distinct().ToList();
    private static void Execute(SqliteConnection db,string sql){using var c=db.CreateCommand();c.CommandText=sql;c.ExecuteNonQuery();}
    private static void Add(SqliteCommand c,string name,object? value)=>c.Parameters.AddWithValue(name,value??DBNull.Value);
    private static void InsertJson<T>(SqliteConnection db,string sql,T value,string? id=null,string? key=null){using var c=db.CreateCommand();c.CommandText=sql;if(id!=null)Add(c,"$id",id);if(key!=null)Add(c,"$key",key);Add(c,"$payload",JsonSerializer.Serialize(value,JsonOptions.Default));c.ExecuteNonQuery();}
    private static List<string> QueryStrings(SqliteConnection db,string sql){using var c=db.CreateCommand();c.CommandText=sql;using var r=c.ExecuteReader();var x=new List<string>();while(r.Read())x.Add(r.GetString(0));return x;}
    private static string? Scalar(SqliteConnection db,string sql,string? eik=null){using var c=db.CreateCommand();c.CommandText=sql;if(eik!=null)Add(c,"$eik",eik);return c.ExecuteScalar()?.ToString();}

    private sealed record Resource(string Id,string Family,string Kind,int Year,string Url,string Path,string Sha256,long Bytes,bool Required,string RetrievedAt);
    private sealed class Columns
    {
        private static readonly Dictionary<string,string[]> Aliases=new(StringComparer.Ordinal){
            ["contract_id"]=["contractid","contractnumber","договорид","номернадоговор","договорномер","уникаленномернадоговор","idдоговор"], ["procedure_id"]=["procedureid","ocid","idнапоръчката","унп","уникаленномернапоръчката","номернапоръчката"], ["revision_date"]=["revisiondate","updated","датанапромяна","датанапубликуване","публикуванна"],
            ["supplier_eik"]=["suppliereik","supplieridentifier","еикнаизпълнител","еикнаизпълнителя","еик","идентификаторнаизпълнител"], ["supplier_name"]=["suppliername","изпълнител","именаизпълнител","наименованиенаизпълнител"],
            ["buyer"]=["buyername","възложител","именавъзложителя","наименованиенавъзложител"], ["subject"]=["title","subject","предмет","предметнадоговор","предметнадоговора"], ["cpv"]=["cpv","cpvcode","кодcpv"],
            ["award_date"]=["awarddate","contractdate","договордата","датанадоговор","датанадоговора","датасключване"], ["amount"]=["amount","contractvalue","стойностнадоговор","стойност","стойностприсключване"], ["currency"]=["currency","валута"], ["public_url"]=["url","publicurl","линк"],
            ["amendment_id"]=["amendmentid","annexid","idнадокумент","номернаанекс","анексид","номернадокумент"], ["amendment_date"]=["amendmentdate","annexdate","датанаизпращаненаанекса","датанаанекс","публикуванна"], ["description"]=["description","основание","описание","описаниенаизмененията","причинизаизменение"], ["amount_delta"]=["amountdelta","changedvalue","промянавстойността","изменениенастойността"]};
        private readonly Dictionary<string,int> _indexes;
        public Columns(string[] headers){_indexes=headers.Select((h,i)=>(h,i)).GroupBy(x=>x.h).ToDictionary(g=>g.Key,g=>g.First().i);if(!Has("contract_id"))throw new InvalidDataException("CSV has no recognized contract identifier column.");}
        private bool Has(string key)=>Aliases[key].Any(_indexes.ContainsKey);
        public string? Value(string[] row,string key){foreach(var a in Aliases[key])if(_indexes.TryGetValue(a,out var i)&&i<row.Length&&!string.IsNullOrWhiteSpace(row[i]))return row[i].Trim();return null;}
    }
    private static class Csv
    {
        public static IEnumerable<string[]> Read(TextReader reader){var header=reader.ReadLine();if(header is null)yield break;var delimiter=header.Count(c=>c==';')>header.Count(c=>c==',')?';':',';yield return Parse(header,delimiter);var row=new List<string>();var field=new StringBuilder();var quoted=false;while(true){var n=reader.Read();if(n<0){if(quoted)throw new FormatException("Unterminated quoted CSV field.");if(field.Length>0||row.Count>0){row.Add(field.ToString());yield return row.ToArray();}yield break;}var ch=(char)n;if(ch=='"'){if(quoted&&reader.Peek()=='"'){reader.Read();field.Append('"');}else quoted=!quoted;}else if(ch==delimiter&&!quoted){row.Add(field.ToString());field.Clear();}else if((ch=='\r'||ch=='\n')&&!quoted){if(ch=='\r'&&reader.Peek()=='\n')reader.Read();row.Add(field.ToString());field.Clear();yield return row.ToArray();row.Clear();}else field.Append(ch);}}
        private static string[] Parse(string line,char delimiter){var result=new List<string>();var field=new StringBuilder();var quoted=false;for(var i=0;i<line.Length;i++){var ch=line[i];if(ch=='"'){if(quoted&&i+1<line.Length&&line[i+1]=='"'){field.Append('"');i++;}else quoted=!quoted;}else if(ch==delimiter&&!quoted){result.Add(field.ToString());field.Clear();}else field.Append(ch);}result.Add(field.ToString());return result.ToArray();}
    }
}

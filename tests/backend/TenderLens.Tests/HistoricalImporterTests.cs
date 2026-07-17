using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TenderLens.Data;

namespace TenderLens.Tests;

public sealed class HistoricalImporterTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tenderlens-history-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Backfill_is_deterministic_and_preserves_history_provenance_and_amendments()
    {
        var manifest = CreateManifest();
        var first = Path.Combine(_directory, "first.db"); var second = Path.Combine(_directory, "second.db");
        await Import(first, manifest); await Import(second, manifest);
        var one = new SnapshotRepository(first); var two = new SnapshotRepository(second);
        Assert.Equal(one.GetMeta().SnapshotId, two.GetMeta().SnapshotId);
        Assert.Equal("2020-02-15", one.GetMeta().ObservationStart);
        Assert.Equal("2025-07-22", one.GetMeta().ObservationEnd);
        Assert.True(one.IsReady());
        var profile = one.GetSupplier("131106522");
        Assert.NotNull(profile); Assert.Equal(5, profile.Metrics.Single(x => x.Label == "Договори").Value);
        var corrected = one.GetRecord("131106522", "C-4");
        Assert.NotNull(corrected); Assert.Contains("корекция", corrected.Subject); Assert.Equal(3, corrected.Amendments.Count);
        Assert.Contains(corrected.Fields, x => x.Label == "Източник");
        Assert.Equal(JsonSerializer.Serialize(one.GetSupplier("131106522")), JsonSerializer.Serialize(two.GetSupplier("131106522")));
    }

    [Fact]
    public async Task Invalid_required_resource_does_not_replace_published_snapshot()
    {
        var manifest = CreateManifest(); var output = Path.Combine(_directory, "published.db"); await Import(output, manifest);
        var before = File.ReadAllBytes(output); var json = await File.ReadAllTextAsync(manifest);
        var checksumPattern = new Regex("\\\"sha256\\\":\\\"[0-9a-f]{64}\\\"", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        await File.WriteAllTextAsync(manifest, checksumPattern.Replace(json, $"\"sha256\":\"{new string('0', 64)}\"", 1));
        await Assert.ThrowsAsync<InvalidDataException>(() => Import(output, manifest));
        Assert.Equal(before, File.ReadAllBytes(output));
    }

    private string CreateManifest()
    {
        Directory.CreateDirectory(_directory);
        var rows = new Dictionary<int,string>{
            [2020]="C-1;131106522;ПРОСВЕТА;МОН;Учебници;22110000;15.02.2020;1000,00;BGN;16.02.2020",
            [2021]="C-2;131106522;ПРОСВЕТА;МОН;Материали;22110000;10.03.2021;2000;BGN;11.03.2021",
            [2022]="C-3;131106522;ПРОСВЕТА;МОН;Книги;22110000;12.04.2022;3000;BGN;13.04.2022",
            [2023]="C-4;131106522|000670680;ПРОСВЕТА;МОН;Дигитални учебници;22110000;14.05.2023;4000;BGN;15.05.2023",
            [2024]="C-4;131106522;ПРОСВЕТА;МОН;Дигитални учебници — корекция;22110000;14.05.2023;4500;BGN;01.02.2024\nC-5;bad-eik;ЛОШ;МОН;Невалиден ред;22110000;20.06.2024;5000;BGN;21.06.2024",
            [2025]="C-6;131106522;ПРОСВЕТА;Община Пловдив;Литература;22110000;22.07.2025;;BGN;23.07.2025"};
        var resources = new List<object>();
        foreach(var (year,row) in rows){var path=Path.Combine(_directory,$"contracts-{year}.csv");File.WriteAllText(path,"contract_id;supplier_eik;supplier_name;buyer;subject;cpv;award_date;amount;currency;revision_date\n"+row+"\n",new UTF8Encoding(false));resources.Add(Resource($"contracts-{year}","contracts",year,path));}
        var annex=Path.Combine(_directory,"annexes.csv");File.WriteAllText(annex,"contract_id;amendment_id;amendment_date;description;amount_delta;currency\nC-4;A-1;01.03.2024;Срок;;BGN\nC-4;A-2;01.04.2024;Количество;250;BGN\nC-4;A-3;01.05.2024;Дата;;BGN\n",new UTF8Encoding(false));resources.Add(Resource("annexes-2024","amendments",2024,annex));
        var manifest=Path.Combine(_directory,"manifest.json");File.WriteAllText(manifest,JsonSerializer.Serialize(new {schemaVersion="1",mode="historicalBackfill",resources}));return manifest;
    }
    private static object Resource(string id,string kind,int year,string path){var bytes=File.ReadAllBytes(path);return new{id,family="АОП fixture",kind,year,url=$"https://data.egov.bg/resource/download/{id}/csv",localPath=Path.GetFileName(path),retrievedAt="2026-07-17T00:00:00Z",bytes=bytes.LongLength,sha256=Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),required=true};}
    private static async Task Import(string output,string manifest){var bytes=await File.ReadAllBytesAsync(manifest);using var json=JsonDocument.Parse(bytes);await HistoricalImporter.PublishAsync(output,manifest,json.RootElement,bytes);}
    public void Dispose(){try{Directory.Delete(_directory,true);}catch{}}
}

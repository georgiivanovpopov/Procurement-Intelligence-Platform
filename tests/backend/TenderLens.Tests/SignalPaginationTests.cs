using System.Text.Json;
using Microsoft.Data.Sqlite;
using TenderLens.Data;

namespace TenderLens.Tests;

public sealed class SignalPaginationTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"tenderlens-{Guid.NewGuid():N}.db");

    [Fact]
    public void Paginates_after_deterministic_allowlisted_sort()
    {
        Seed();
        var page = new SnapshotRepository(_path).GetSignalPage("123456789", "example", 2, 2, "value", "desc");
        Assert.NotNull(page);
        Assert.Equal(5, page.EvidenceTotal);
        Assert.Equal(2, page.Evidence.Count);
        Assert.Equal(3, page.TotalPages);
        Assert.Equal(new[] { "r3", "r2" }, page.Evidence.Select(x => x.RecordId));
    }

    private void Seed()
    {
        using var connection = new SqliteConnection($"Data Source={_path};Pooling=False"); connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "create table published_signals(eik text, signal_key text, payload text); insert into published_signals values($eik,$key,$payload)";
        command.Parameters.AddWithValue("$eik", "123456789"); command.Parameters.AddWithValue("$key", "example");
        var evidence = Enumerable.Range(1, 5).Select(i => new EvidenceRow($"r{i}", "buyer", "subject", "cpv", $"2026-01-0{i}", new Money(i, "BGN"), "test")).ToArray();
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(new SignalDetail("example", "Example", "No signal", "fact", "trigger", "formula", "threshold", "peers", null, "window", "limits", "v1", evidence), JsonOptions.Default));
        command.ExecuteNonQuery();
    }

    public void Dispose() { SqliteConnection.ClearAllPools(); if (File.Exists(_path)) File.Delete(_path); }
}

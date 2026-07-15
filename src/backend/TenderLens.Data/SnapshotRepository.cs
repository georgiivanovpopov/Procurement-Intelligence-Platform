using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace TenderLens.Data;

public sealed class SnapshotRepository(string databasePath)
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = Path.GetFullPath(databasePath),
        Mode = SqliteOpenMode.ReadOnly,
        Cache = SqliteCacheMode.Shared
    }.ToString();

    public SnapshotMeta GetMeta() => ReadJson<SnapshotMeta>("select payload from published_meta limit 1")!;

    public SupplierProfile? GetSupplier(string eik) => ReadJson<SupplierProfile>("select payload from published_suppliers where eik=$id", eik);

    public SignalDetail? GetSignal(string eik, string key)
        => ReadJson<SignalDetail>("select payload from published_signals where eik=$id and signal_key=$key", eik, key);

    public SourceRecord? GetRecord(string eik, string recordId)
        => ReadJson<SourceRecord>("select payload from published_records where supplier_eik=$id and record_id=$key", eik, recordId);

    public bool IsReady()
    {
        try { return GetMeta().SchemaVersion == "1"; }
        catch { return false; }
    }

    private T? ReadJson<T>(string sql, string? id = null, string? key = null)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var queryOnly = connection.CreateCommand();
        queryOnly.CommandText = "pragma query_only=ON";
        queryOnly.ExecuteNonQuery();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (id is not null) command.Parameters.AddWithValue("$id", id);
        if (key is not null) command.Parameters.AddWithValue("$key", key);
        var payload = command.ExecuteScalar() as string;
        return payload is null ? default : JsonSerializer.Deserialize<T>(payload, JsonOptions.Default);
    }
}

public static class JsonOptions
{
    public static readonly JsonSerializerOptions Default = new(JsonSerializerDefaults.Web) { WriteIndented = false };
}

using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace TenderLens.Data;

public sealed class SnapshotRepository(string databasePath)
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = Path.GetFullPath(databasePath),
        Mode = SqliteOpenMode.ReadOnly,
        Cache = SqliteCacheMode.Shared,
        Pooling = true
    }.ToString();

    public SnapshotMeta GetMeta() => ReadJson<SnapshotMeta>("select payload from published_meta limit 1")!;

    public SupplierProfile? GetSupplier(string eik) => ReadJson<SupplierProfile>("select payload from published_suppliers where eik=$id", eik);

    public SignalDetail? GetSignal(string eik, string key)
        => ReadJson<SignalDetail>("select payload from published_signals where eik=$id and signal_key=$key", eik, key);

    public SignalDetail? GetSignalPage(string eik, string key, int page, int pageSize, string sort, string direction)
    {
        var order = (sort, direction) switch
        {
            ("date", "asc") => "json_extract(value,'$.awardDate') asc, json_extract(value,'$.recordId') asc",
            ("date", "desc") => "json_extract(value,'$.awardDate') desc, json_extract(value,'$.recordId') asc",
            ("value", "asc") => "cast(json_extract(value,'$.value.amount') as real) asc, json_extract(value,'$.recordId') asc",
            _ => "cast(json_extract(value,'$.value.amount') as real) desc, json_extract(value,'$.recordId') asc"
        };
        using var connection = Open();
        using var metadata = connection.CreateCommand();
        metadata.CommandText = "select json_set(json_remove(payload,'$.evidence'),'$.evidence',json('[]')), json_array_length(payload,'$.evidence') from published_signals where eik=$id and signal_key=$key";
        metadata.Parameters.AddWithValue("$id", eik); metadata.Parameters.AddWithValue("$key", key);
        using var metadataReader = metadata.ExecuteReader();
        if (!metadataReader.Read()) return null;
        var signal = JsonSerializer.Deserialize<SignalDetail>(metadataReader.GetString(0), JsonOptions.Default)!;
        var total = metadataReader.GetInt32(1);
        metadataReader.Close();
        using var evidence = connection.CreateCommand();
        evidence.CommandText = $"select value from json_each((select payload from published_signals where eik=$id and signal_key=$key),'$.evidence') order by {order} limit $take offset $skip";
        evidence.Parameters.AddWithValue("$id", eik); evidence.Parameters.AddWithValue("$key", key);
        evidence.Parameters.AddWithValue("$take", pageSize); evidence.Parameters.AddWithValue("$skip", (page - 1) * pageSize);
        using var evidenceReader = evidence.ExecuteReader();
        var rows = new List<EvidenceRow>(pageSize);
        while (evidenceReader.Read()) rows.Add(JsonSerializer.Deserialize<EvidenceRow>(evidenceReader.GetString(0), JsonOptions.Default)!);
        return signal with
        {
            Evidence = rows,
            EvidenceTotal = total,
            Page = page,
            PageSize = pageSize,
            TotalPages = total == 0 ? 0 : (int)Math.Ceiling(total / (double)pageSize)
        };
    }

    public SourceRecord? GetRecord(string eik, string recordId)
        => ReadJson<SourceRecord>("select payload from published_records where supplier_eik=$id and record_id=$key", eik, recordId);

    public bool IsReady()
    {
        try { return GetMeta().SchemaVersion is "1" or "2"; }
        catch { return false; }
    }

    private T? ReadJson<T>(string sql, string? id = null, string? key = null)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (id is not null) command.Parameters.AddWithValue("$id", id);
        if (key is not null) command.Parameters.AddWithValue("$key", key);
        var payload = command.ExecuteScalar() as string;
        return payload is null ? default : JsonSerializer.Deserialize<T>(payload, JsonOptions.Default);
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var queryOnly = connection.CreateCommand();
        queryOnly.CommandText = "pragma query_only=ON";
        queryOnly.ExecuteNonQuery();
        return connection;
    }
}

public static class JsonOptions
{
    public static readonly JsonSerializerOptions Default = new(JsonSerializerDefaults.Web) { WriteIndented = false };
}

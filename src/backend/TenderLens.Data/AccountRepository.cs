using Microsoft.Data.Sqlite;

namespace TenderLens.Data;

public sealed record Account(Guid Id, string Username, string NormalizedUsername, string PasswordHash, DateTimeOffset CreatedAt);

public sealed class AccountRepository
{
    private readonly string _connectionString;
    private readonly Exception? _initializationError;

    public AccountRepository(string databasePath)
    {
        var fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();
        try { Initialize(); }
        catch (Exception exception) { _initializationError = exception; }
    }

    public bool IsReady()
    {
        try
        {
            using var connection = Open();
            if (_initializationError is not null) return false;
            using var command = connection.CreateCommand();
            command.CommandText = "begin immediate; insert into readiness_probe(value) values(1); delete from readiness_probe; rollback";
            command.ExecuteNonQuery();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public Account? FindByUsername(string username)
    {
        EnsureInitialized();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "select id, username, normalized_username, password_hash, created_at from accounts where normalized_username=$username";
        command.Parameters.AddWithValue("$username", NormalizeUsername(username));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadAccount(reader) : null;
    }

    public Account? FindById(Guid id)
    {
        EnsureInitialized();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "select id, username, normalized_username, password_hash, created_at from accounts where id=$id";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadAccount(reader) : null;
    }

    public bool TryCreate(string username, string passwordHash, out Account account)
    {
        EnsureInitialized();
        account = new Account(Guid.NewGuid(), username, NormalizeUsername(username), passwordHash, DateTimeOffset.UtcNow);
        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "insert into accounts(id, username, normalized_username, password_hash, created_at) values($id,$username,$normalized,$hash,$created)";
            command.Parameters.AddWithValue("$id", account.Id.ToString("D"));
            command.Parameters.AddWithValue("$username", account.Username);
            command.Parameters.AddWithValue("$normalized", account.NormalizedUsername);
            command.Parameters.AddWithValue("$hash", account.PasswordHash);
            command.Parameters.AddWithValue("$created", account.CreatedAt.ToString("O"));
            command.ExecuteNonQuery();
            return true;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            account = default!;
            return false;
        }
    }

    public static string NormalizeUsername(string username) => username.Normalize().ToUpperInvariant();

    private void Initialize()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            pragma journal_mode=WAL;
            pragma foreign_keys=ON;
            create table if not exists accounts(
                id text primary key,
                username text not null,
                normalized_username text not null unique,
                password_hash text not null,
                created_at text not null
            );
            create table if not exists readiness_probe(value integer not null);
            """;
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var timeout = connection.CreateCommand();
        timeout.CommandText = "pragma busy_timeout=5000";
        timeout.ExecuteNonQuery();
        return connection;
    }

    private void EnsureInitialized()
    {
        if (_initializationError is not null)
            throw new InvalidOperationException("The account store could not be initialized.", _initializationError);
    }

    private static Account ReadAccount(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        DateTimeOffset.Parse(reader.GetString(4), System.Globalization.CultureInfo.InvariantCulture));
}

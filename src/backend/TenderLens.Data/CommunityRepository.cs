using Microsoft.Data.Sqlite;

namespace TenderLens.Data;

public sealed record CommunityPost(Guid Id, string Author, string SupplierEik, string SignalKey, string SignalName,
    string Body, DateTimeOffset CreatedAt, int CommentCount);
public sealed record CommunityComment(Guid Id, Guid PostId, string Author, string Body, DateTimeOffset CreatedAt);
public sealed record CommunityProfile(string Username, int Followers, int Following, bool IsFollowing,
    IReadOnlyList<CommunityPost> Posts);
public sealed record CommunityPostDetail(CommunityPost Post, IReadOnlyList<CommunityComment> Comments,
    int CommentPage, int CommentPageSize, int CommentTotal, int CommentTotalPages);

public sealed class CommunityRepository
{
    private readonly string _connectionString;
    private readonly Exception? _initializationError;

    public CommunityRepository(string databasePath)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath), Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared, Pooling = true
        }.ToString();
        try { Initialize(); }
        catch (Exception exception) { _initializationError = exception; }
    }

    public bool IsReady()
    {
        try
        {
            if (_initializationError is not null) return false;
            using var connection = Open(); using var command = connection.CreateCommand();
            command.CommandText = "begin immediate; select count(*) from community_posts; insert into community_readiness(value) values(1); delete from community_readiness; rollback";
            command.ExecuteNonQuery(); return true;
        }
        catch { return false; }
    }

    public CommunityPost? CreatePost(Guid authorId, string eik, string signalKey, string signalName, string body)
    {
        EnsureInitialized();
        var id = Guid.NewGuid();
        var created = DateTimeOffset.UtcNow;
        try
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                insert into community_posts(id,author_id,supplier_eik,signal_key,signal_name,body,created_at)
                values($id,$author,$eik,$key,$name,$body,$created)
                """;
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            command.Parameters.AddWithValue("$author", authorId.ToString("D"));
            command.Parameters.AddWithValue("$eik", eik);
            command.Parameters.AddWithValue("$key", signalKey);
            command.Parameters.AddWithValue("$name", signalName);
            command.Parameters.AddWithValue("$body", body);
            command.Parameters.AddWithValue("$created", created.ToString("O"));
            command.ExecuteNonQuery();
            var result = ReadSinglePost(connection, transaction, id);
            transaction.Commit();
            return result;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19) { return null; }
    }

    public bool Follow(Guid followerId, Guid followedId)
    {
        EnsureInitialized();
        if (followerId == followedId) return false;
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "insert or ignore into community_follows(follower_id,followed_id,created_at) values($from,$to,$created)";
        command.Parameters.AddWithValue("$from", followerId.ToString("D"));
        command.Parameters.AddWithValue("$to", followedId.ToString("D"));
        command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
        return command.ExecuteNonQuery() > 0;
    }

    public void Unfollow(Guid followerId, Guid followedId)
    {
        EnsureInitialized();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "delete from community_follows where follower_id=$from and followed_id=$to";
        command.Parameters.AddWithValue("$from", followerId.ToString("D"));
        command.Parameters.AddWithValue("$to", followedId.ToString("D"));
        command.ExecuteNonQuery();
    }

    public CommunityComment? AddComment(Guid postId, Guid authorId, string body)
    {
        EnsureInitialized();
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var id = Guid.NewGuid(); var created = DateTimeOffset.UtcNow;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            insert into community_comments(id,post_id,author_id,body,created_at)
            select $id,id,$author,$body,$created from community_posts where id=$post
            """;
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$post", postId.ToString("D"));
        command.Parameters.AddWithValue("$author", authorId.ToString("D"));
        command.Parameters.AddWithValue("$body", body);
        command.Parameters.AddWithValue("$created", created.ToString("O"));
        if (command.ExecuteNonQuery() == 0) return null;
        using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = "select c.id,c.post_id,a.username,c.body,c.created_at from community_comments c join accounts a on a.id=c.author_id where c.id=$id";
        read.Parameters.AddWithValue("$id", id.ToString("D"));
        using var reader = read.ExecuteReader();
        var result = reader.Read() ? ReadComment(reader) : null;
        reader.Close(); transaction.Commit(); return result;
    }

    public IReadOnlyList<CommunityPost> GetFeed(Guid? viewerId, int page, int pageSize)
    {
        EnsureInitialized();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = viewerId is null
            ? PostSelect + " order by p.created_at desc,p.id desc limit $take offset $skip"
            : PostSelect + " where p.author_id=$viewer or exists(select 1 from community_follows f where f.follower_id=$viewer and f.followed_id=p.author_id) order by p.created_at desc,p.id desc limit $take offset $skip";
        if (viewerId is not null) command.Parameters.AddWithValue("$viewer", viewerId.Value.ToString("D"));
        command.Parameters.AddWithValue("$take", pageSize); command.Parameters.AddWithValue("$skip", (page - 1) * pageSize);
        return ReadPosts(command);
    }

    public CommunityProfile? GetProfile(string username, Guid? viewerId, int page, int pageSize)
    {
        EnsureInitialized();
        using var connection = Open();
        using var account = connection.CreateCommand();
        account.CommandText = """
            select a.id,a.username,
              (select count(*) from community_follows where followed_id=a.id),
              (select count(*) from community_follows where follower_id=a.id),
              exists(select 1 from community_follows where follower_id=$viewer and followed_id=a.id)
            from accounts a where a.normalized_username=$username
            """;
        account.Parameters.AddWithValue("$viewer", viewerId?.ToString("D") ?? "");
        account.Parameters.AddWithValue("$username", AccountRepository.NormalizeUsername(username));
        using var reader = account.ExecuteReader();
        if (!reader.Read()) return null;
        var id = reader.GetString(0); var name = reader.GetString(1); var followers = reader.GetInt32(2);
        var following = reader.GetInt32(3); var isFollowing = reader.GetBoolean(4); reader.Close();
        using var posts = connection.CreateCommand();
        posts.CommandText = PostSelect + " where p.author_id=$author order by p.created_at desc,p.id desc limit $take offset $skip";
        posts.Parameters.AddWithValue("$author", id); posts.Parameters.AddWithValue("$take", pageSize); posts.Parameters.AddWithValue("$skip", (page - 1) * pageSize);
        return new CommunityProfile(name, followers, following, isFollowing, ReadPosts(posts));
    }

    public CommunityPostDetail? GetPost(Guid id, int commentPage = 1, int commentPageSize = 50)
    {
        EnsureInitialized();
        using var connection = Open();
        using var post = connection.CreateCommand(); post.CommandText = PostSelect + " where p.id=$id";
        post.Parameters.AddWithValue("$id", id.ToString("D"));
        var posts = ReadPosts(post); if (posts.Count == 0) return null;
        using var comments = connection.CreateCommand();
        comments.CommandText = "select c.id,c.post_id,a.username,c.body,c.created_at from community_comments c join accounts a on a.id=c.author_id where c.post_id=$id order by c.created_at asc,c.id asc limit $take offset $skip";
        comments.Parameters.AddWithValue("$id", id.ToString("D"));
        comments.Parameters.AddWithValue("$take", commentPageSize);
        comments.Parameters.AddWithValue("$skip", (commentPage - 1) * commentPageSize);
        using var reader = comments.ExecuteReader(); var rows = new List<CommunityComment>();
        while (reader.Read()) rows.Add(ReadComment(reader));
        reader.Close(); using var count = connection.CreateCommand();
        count.CommandText = "select count(*) from community_comments where post_id=$id";
        count.Parameters.AddWithValue("$id", id.ToString("D")); var total = Convert.ToInt32(count.ExecuteScalar());
        return new CommunityPostDetail(posts[0], rows, commentPage, commentPageSize, total,
            total == 0 ? 0 : (int)Math.Ceiling(total / (double)commentPageSize));
    }

    private const string PostSelect = """
        select p.id,a.username,p.supplier_eik,p.signal_key,p.signal_name,p.body,p.created_at,
          (select count(*) from community_comments c where c.post_id=p.id)
        from community_posts p join accounts a on a.id=p.author_id
        """;

    private static List<CommunityPost> ReadPosts(SqliteCommand command)
    {
        using var reader = command.ExecuteReader(); var rows = new List<CommunityPost>();
        while (reader.Read()) rows.Add(new CommunityPost(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2),
            reader.GetString(3), reader.GetString(4), reader.GetString(5), DateTimeOffset.Parse(reader.GetString(6)), reader.GetInt32(7)));
        return rows;
    }

    private static CommunityPost ReadSinglePost(SqliteConnection connection, SqliteTransaction transaction, Guid id)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = PostSelect + " where p.id=$id"; command.Parameters.AddWithValue("$id", id.ToString("D"));
        var rows = ReadPosts(command); return rows.Single();
    }

    private static CommunityComment ReadComment(SqliteDataReader reader) => new(Guid.Parse(reader.GetString(0)),
        Guid.Parse(reader.GetString(1)), reader.GetString(2), reader.GetString(3), DateTimeOffset.Parse(reader.GetString(4)));

    private void Initialize()
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = """
            pragma foreign_keys=ON;
            create table if not exists community_posts(
              id text primary key, author_id text not null references accounts(id), supplier_eik text not null,
              signal_key text not null, signal_name text not null, body text not null, created_at text not null,
              unique(author_id,supplier_eik,signal_key,body));
            create index if not exists ix_community_posts_created on community_posts(created_at desc,id desc);
            create table if not exists community_follows(
              follower_id text not null references accounts(id), followed_id text not null references accounts(id), created_at text not null,
              primary key(follower_id,followed_id), check(follower_id<>followed_id));
            create table if not exists community_comments(
              id text primary key, post_id text not null references community_posts(id) on delete cascade,
              author_id text not null references accounts(id), body text not null, created_at text not null);
            create index if not exists ix_community_comments_post on community_comments(post_id,created_at,id);
            create index if not exists ix_community_follows_followed on community_follows(followed_id,follower_id);
            create table if not exists community_readiness(value integer not null);
            """;
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString); connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = "pragma foreign_keys=ON; pragma busy_timeout=5000"; command.ExecuteNonQuery();
        return connection;
    }
    private void EnsureInitialized()
    {
        if (_initializationError is not null) throw new InvalidOperationException("The community store could not be initialized.", _initializationError);
    }
}

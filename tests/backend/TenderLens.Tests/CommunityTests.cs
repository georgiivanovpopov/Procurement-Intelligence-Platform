using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using TenderLens.Data;

namespace TenderLens.Tests;

public sealed class CommunityTests
{
    [Fact]
    public void Repository_FeedContainsOnlySelfAndFollowedAuthors_AndPersistsComments()
    {
        using var store = new TemporaryStore();
        var accounts = new AccountRepository(store.Path);
        Assert.True(accounts.TryCreate("alice", "hash", out var alice));
        Assert.True(accounts.TryCreate("bob", "hash", out var bob));
        Assert.True(accounts.TryCreate("carol", "hash", out var carol));
        var community = new CommunityRepository(store.Path);
        var alicePost = community.CreatePost(alice.Id, "175074752", "buyer-concentration", "Buyer concentration", "Review Alice")!;
        var bobPost = community.CreatePost(bob.Id, "175074752", "buyer-concentration", "Buyer concentration", "Review Bob")!;
        community.CreatePost(carol.Id, "175074752", "buyer-concentration", "Buyer concentration", "Review Carol");
        community.Follow(alice.Id, bob.Id);

        var feed = community.GetFeed(alice.Id, 1, 20);
        Assert.Equal(2, feed.Count);
        Assert.Contains(feed, post => post.Id == alicePost.Id);
        Assert.Contains(feed, post => post.Id == bobPost.Id);
        Assert.DoesNotContain(feed, post => post.Author == "carol");
        Assert.NotNull(community.AddComment(bobPost.Id, alice.Id, "Please compare the records."));
        Assert.Single(community.GetPost(bobPost.Id)!.Comments);
    }

    [Fact]
    public async Task Api_RequiresAuthenticationAndCsrf_ThenPublishesCanonicalSignal()
    {
        await using var factory = new CommunityFactory();
        using var anonymous = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = true });
        var (eik, signalKey) = await FindSignal(anonymous);
        var csrf = await GetCsrf(anonymous);
        using var unauthorized = await Post(anonymous, "/api/v1/community/posts", new { supplierEik = eik, signalKey, body = "Needs review" }, csrf);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = true });
        csrf = await GetCsrf(client);
        using var registered = await Post(client, "/api/v1/auth/register", new { username = "feed_author", password = "correct horse battery staple" }, csrf);
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        using var forged = await client.PostAsJsonAsync("/api/v1/community/posts", new { supplierEik = eik, signalKey, body = "Needs review" });
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);

        csrf = await GetCsrf(client);
        using var created = await Post(client, "/api/v1/community/posts", new { supplierEik = eik, signalKey, body = "<script>alert(1)</script> review" }, csrf);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var post = await created.Content.ReadFromJsonAsync<CommunityPost>();
        Assert.NotNull(post);
        Assert.Equal(signalKey, post.SignalKey);

        using var publicRead = await anonymous.GetAsync($"/api/v1/community/posts/{post.Id}");
        Assert.Equal(HttpStatusCode.OK, publicRead.StatusCode);
        var payload = await publicRead.Content.ReadFromJsonAsync<CommunityPostDetail>();
        Assert.Equal("<script>alert(1)</script> review", payload!.Post.Body);

        csrf = await GetCsrf(client);
        var readers = Enumerable.Range(0, 20).Select(_ => anonymous.GetAsync("/api/v1/community/feed?page=1&pageSize=20")).ToArray();
        using var comment = await Post(client, $"/api/v1/community/posts/{post.Id}/comments", new { body = "Concurrent context" }, csrf);
        var readResponses = await Task.WhenAll(readers);
        Assert.Equal(HttpStatusCode.Created, comment.StatusCode);
        Assert.All(readResponses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        foreach (var response in readResponses) response.Dispose();
    }

    [Fact]
    public void Repository_RejectsDuplicatePosts_AndSelfFollow()
    {
        using var store = new TemporaryStore();
        var accounts = new AccountRepository(store.Path);
        accounts.TryCreate("auditor", "hash", out var auditor);
        var community = new CommunityRepository(store.Path);
        Assert.NotNull(community.CreatePost(auditor.Id, "175074752", "signal", "Signal", "Same opinion"));
        Assert.Null(community.CreatePost(auditor.Id, "175074752", "signal", "Signal", "Same opinion"));
        Assert.False(community.Follow(auditor.Id, auditor.Id));
    }

    [Fact]
    public async Task Repository_SupportsTwentyConcurrentFeedReaders()
    {
        using var store = new TemporaryStore();
        var accounts = new AccountRepository(store.Path);
        accounts.TryCreate("reader", "hash", out var reader);
        var community = new CommunityRepository(store.Path);
        community.CreatePost(reader.Id, "175074752", "signal", "Signal", "Concurrent review");
        var reads = Enumerable.Range(0, 20).Select(_ => Task.Run(() => community.GetFeed(reader.Id, 1, 20))).ToArray();
        var results = await Task.WhenAll(reads);
        Assert.All(results, feed => Assert.Single(feed));
    }

    private static async Task<(string Eik, string Signal)> FindSignal(HttpClient client)
    {
        using var response = await client.GetAsync("/api/v1/suppliers/175074752");
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var signal = json.RootElement.GetProperty("signals")[0].GetProperty("key").GetString()!;
        return ("175074752", signal);
    }
    private static async Task<string> GetCsrf(HttpClient client) =>
        (await client.GetFromJsonAsync<CsrfPayload>("/api/v1/auth/csrf"))!.Token;
    private static Task<HttpResponseMessage> Post(HttpClient client, string path, object body, string csrf)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-CSRF-TOKEN", csrf); return client.SendAsync(request);
    }
    private sealed record CsrfPayload(string Token);

    private sealed class CommunityFactory : WebApplicationFactory<Program>
    {
        private readonly TemporaryStore _store = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing"); builder.UseSetting("AccountDbPath", _store.Path);
            builder.UseSetting("AccountStorageDurable", "true"); builder.UseSetting("SnapshotPath", FindSnapshot());
        }
        public override async ValueTask DisposeAsync() { await base.DisposeAsync(); _store.Dispose(); }
    }
    private sealed class TemporaryStore : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tenderlens-community-{Guid.NewGuid():N}.db");
        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" }) { var path = Path + suffix; if (File.Exists(path)) File.Delete(path); }
            if (Directory.Exists(Path + ".keys")) Directory.Delete(Path + ".keys", true);
        }
    }
    private static string FindSnapshot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = System.IO.Path.Combine(directory.FullName, "data", "snapshot", "tenderlens.db");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("The test procurement snapshot was not found.");
    }
}

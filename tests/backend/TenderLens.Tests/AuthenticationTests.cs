using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using TenderLens.Data;

namespace TenderLens.Tests;

public sealed class AuthenticationTests
{
    [Fact]
    public void AccountRepository_PersistsAccounts_AndNormalizesUsernames()
    {
        using var store = new TemporaryAccountStore();
        var repository = new AccountRepository(store.Path);
        Assert.True(repository.TryCreate("Audit_User", "not-a-real-hash", out var created));

        var reopened = new AccountRepository(store.Path);
        var found = reopened.FindByUsername("audit_user");

        Assert.NotNull(found);
        Assert.Equal(created.Id, found.Id);
        Assert.Equal("Audit_User", found.Username);
        Assert.False(reopened.TryCreate("AUDIT_USER", "another-hash", out _));
    }

    [Fact]
    public async Task Registration_IssuesSession_AndRejectsCaseInsensitiveDuplicate()
    {
        await using var factory = new AuthenticationFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
            HandleCookies = true
        });
        var csrf = await GetCsrf(client);

        using var first = await Post(client, "/api/v1/auth/register", new { username = "Audit_User", password = "correct horse battery staple" }, csrf);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var session = await client.GetFromJsonAsync<SessionResponse>("/api/v1/auth/session");
        Assert.True(session!.Authenticated);
        Assert.Equal("Audit_User", session.Username);

        var authenticatedCsrf = await GetCsrf(client);
        using var duplicate = await Post(client, "/api/v1/auth/register", new { username = "audit_user", password = "another secure passphrase" }, authenticatedCsrf);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
    }

    [Fact]
    public async Task Mutations_RequireAntiforgery_AndLoginFailureIsGeneric()
    {
        await using var factory = new AuthenticationFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = true });

        using var missingCsrf = await client.PostAsJsonAsync("/api/v1/auth/register", new { username = "auditor", password = "correct horse battery staple" });
        Assert.Equal(HttpStatusCode.BadRequest, missingCsrf.StatusCode);

        var csrf = await GetCsrf(client);
        using var unknown = await Post(client, "/api/v1/auth/login", new { username = "missing", password = "wrong password value" }, csrf);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        var problem = await unknown.Content.ReadFromJsonAsync<ProblemPayload>();
        Assert.Equal("invalid_credentials", problem!.Code);
        Assert.DoesNotContain("missing", problem.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AuthenticationRateLimit_DoesNotShareThePublicReadBudget()
    {
        await using var factory = new AuthenticationFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = true });
        var csrf = await GetCsrf(client);
        HttpResponseMessage? last = null;
        for (var attempt = 0; attempt < 11; attempt++)
        {
            last?.Dispose();
            last = await Post(client, "/api/v1/auth/login", new { username = "missing", password = "wrong password value" }, csrf);
        }

        using (last)
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, last!.StatusCode);
            Assert.True(last.Headers.Contains("Retry-After"));
        }

        using var publicRead = await client.GetAsync("/api/v1/suppliers/175074752");
        Assert.Equal(HttpStatusCode.OK, publicRead.StatusCode);
    }

    [Fact]
    public async Task ProductionReadiness_RejectsEphemeralAccountStorage()
    {
        await using var factory = new NonDurableAuthenticationFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });

        using var response = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>();
        Assert.Equal("account_storage_not_durable", problem!.Code);
    }

    [Fact]
    public async Task AccountAndSessionKeys_SurviveApplicationRestart()
    {
        using var store = new TemporaryAccountStore();
        await using (var firstFactory = new SharedAuthenticationFactory(store.Path))
        using (var firstClient = firstFactory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = true }))
        {
            var csrf = await GetCsrf(firstClient);
            using var registered = await Post(firstClient, "/api/v1/auth/register",
                new { username = "restart_user", password = "correct horse battery staple" }, csrf);
            Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        }

        await using (var secondFactory = new SharedAuthenticationFactory(store.Path))
        using (var secondClient = secondFactory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = true }))
        {
            var csrf = await GetCsrf(secondClient);
            using var loggedIn = await Post(secondClient, "/api/v1/auth/login",
                new { username = "restart_user", password = "correct horse battery staple" }, csrf);
            Assert.Equal(HttpStatusCode.OK, loggedIn.StatusCode);
        }
    }

    private static async Task<string> GetCsrf(HttpClient client) =>
        (await client.GetFromJsonAsync<CsrfPayload>("/api/v1/auth/csrf"))!.Token;

    private static Task<HttpResponseMessage> Post(HttpClient client, string path, object body, string csrf)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        return client.SendAsync(request);
    }

    private sealed record CsrfPayload(string Token);
    private sealed record ProblemPayload(string Code, string Detail);

    private sealed class AuthenticationFactory : WebApplicationFactory<Program>
    {
        private readonly TemporaryAccountStore _store = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("AccountDbPath", _store.Path);
            builder.UseSetting("AccountStorageDurable", "true");
            builder.UseSetting("SnapshotPath", FindSnapshot());
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            _store.Dispose();
        }
    }

    private sealed class SharedAuthenticationFactory(string accountPath) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("AccountDbPath", accountPath);
            builder.UseSetting("AccountStorageDurable", "true");
            builder.UseSetting("SnapshotPath", FindSnapshot());
        }
    }

    private sealed class NonDurableAuthenticationFactory : WebApplicationFactory<Program>
    {
        private readonly TemporaryAccountStore _store = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("AccountDbPath", _store.Path);
            builder.UseSetting("AccountStorageDurable", "false");
            builder.UseSetting("SnapshotPath", System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.db"));
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            _store.Dispose();
        }
    }

    private sealed class TemporaryAccountStore : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tenderlens-accounts-{Guid.NewGuid():N}.db");
        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                var path = Path + suffix;
                if (File.Exists(path)) File.Delete(path);
            }
            var keys = Path + ".keys";
            if (Directory.Exists(keys)) Directory.Delete(keys, true);
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

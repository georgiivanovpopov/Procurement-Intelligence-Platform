using System.Security.Claims;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.ResponseCompression;
using TenderLens.Data;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 16_384);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();
builder.Services.AddProblemDetails();
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ =>
            new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));
    options.AddPolicy("auth", context =>
        RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ =>
            new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(15), QueueLimit = 0, AutoReplenishment = true }));
    options.OnRejected = async (context, token) =>
    {
        var retry = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var delay)
            ? Math.Max(1, (int)Math.Ceiling(delay.TotalSeconds)) : 60;
        context.HttpContext.Response.Headers.RetryAfter = retry.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await Results.Problem(statusCode: 429, title: "Твърде много заявки", detail: "Изчакайте и опитайте отново.",
            extensions: new Dictionary<string, object?> { ["code"] = "rate_limit_exceeded", ["traceId"] = context.HttpContext.TraceIdentifier })
            .ExecuteAsync(context.HttpContext);
    };
});
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
{
    options.Cookie.Name = "__Host-TenderLens";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.Path = "/";
    options.SlidingExpiration = true;
    options.ExpireTimeSpan = TimeSpan.FromDays(7);
    options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = 401; return Task.CompletedTask; };
    options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; };
});
builder.Services.AddAuthorization();
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.Name = "__Host-TenderLens-CSRF";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.Path = "/";
});

var snapshotPath = ResolveSnapshotPath(builder.Configuration["SnapshotPath"], builder.Environment.ContentRootPath);
var accountPath = ResolveAccountPath(builder.Configuration["AccountDbPath"], builder.Environment.ContentRootPath);
var durableStorageRoot = Path.GetFullPath(builder.Configuration["AccountStorageRoot"] ?? "/var/data");
var durableAccounts = builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing") ||
    builder.Configuration.GetValue<bool>("AccountStorageDurable") && IsPathWithin(accountPath, durableStorageRoot);
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo($"{accountPath}.keys"))
    .SetApplicationName("TenderLens");
builder.Services.AddSingleton(new SnapshotRepository(snapshotPath));
builder.Services.AddSingleton(new AccountRepository(accountPath));
builder.Services.AddSingleton<IPasswordHasher<Account>, PasswordHasher<Account>>();

var app = builder.Build();
var passwordHasher = app.Services.GetRequiredService<IPasswordHasher<Account>>();
var dummyAccount = new Account(Guid.Empty, "dummy", "DUMMY", "", DateTimeOffset.UnixEpoch);
var dummyPasswordHash = passwordHasher.HashPassword(dummyAccount, "This password is never accepted.");

app.UseExceptionHandler();
app.UseForwardedHeaders();
app.UseResponseCompression();
if (!app.Environment.IsDevelopment()) app.UseHsts();
app.Use(async (context, next) =>
{
    context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; object-src 'none'; base-uri 'self'; frame-ancestors 'none'; form-action 'self'";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    await next();
});
app.Use(async (context, next) =>
{
    var allowedPost = context.Request.Method == "POST" &&
        context.Request.Path.Value is "/api/v1/auth/register" or "/api/v1/auth/login" or "/api/v1/auth/logout";
    if (context.Request.Method is not ("GET" or "HEAD") && !allowedPost)
    {
        context.Response.Headers.Allow = context.Request.Path.StartsWithSegments("/api/v1/auth") ? "POST" : "GET, HEAD";
        await Results.Problem(statusCode: 405, title: "Неподдържан метод",
            extensions: new Dictionary<string, object?> { ["code"] = "method_not_allowed", ["traceId"] = context.TraceIdentifier }).ExecuteAsync(context);
        return;
    }
    if (context.Request.ContentLength is > 16384)
    {
        await Results.Problem(statusCode: 413, title: "Заявката е твърде голяма",
            extensions: new Dictionary<string, object?> { ["code"] = "request_too_large", ["traceId"] = context.TraceIdentifier }).ExecuteAsync(context);
        return;
    }
    await next();
});
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", (SnapshotRepository snapshots, AccountRepository accounts) =>
    snapshots.IsReady() && accounts.IsReady() && durableAccounts
        ? Results.Ok(new { status = "ready" })
        : Results.Problem(statusCode: 503, title: "Услугата не е готова",
            extensions: new Dictionary<string, object?> { ["code"] = durableAccounts ? "storage_unavailable" : "account_storage_not_durable" }));

var api = app.MapGroup("/api/v1");
api.MapGet("/meta", (HttpContext http, SnapshotRepository repo) => WithCache(http, repo.GetMeta()));
api.MapGet("/suppliers/{eik}", (string eik, HttpContext http, SnapshotRepository repo) =>
{
    eik = eik.Trim();
    if (!Eik.IsValid(eik)) return Invalid(http, "invalid_eik", "Въведете валиден ЕИК.");
    var value = repo.GetSupplier(eik);
    return value is null ? Missing(http, "supplier_not_found", "В текущата снимка на данните няма доставчик с този ЕИК.") : WithCache(http, value);
});
api.MapGet("/suppliers/{eik}/signals/{signalKey}", (string eik, string signalKey, HttpContext http, SnapshotRepository repo) =>
{
    if (!TryPositiveInt(http.Request.Query["page"], 1, 1, 100000, out var page) ||
        !TryPositiveInt(http.Request.Query["pageSize"], 50, 1, 100, out var pageSize))
        return Invalid(http, "invalid_pagination", "Страницата трябва да е между 1 и 100000, а размерът ѝ — между 1 и 100.");
    var sort = http.Request.Query["sort"].FirstOrDefault() ?? "value";
    var dir = http.Request.Query["dir"].FirstOrDefault() ?? "desc";
    if (sort is not ("date" or "value") || dir is not ("asc" or "desc"))
        return Invalid(http, "invalid_sort", "Позволени са date/value и asc/desc.");
    var value = repo.GetSignalPage(eik.Trim(), signalKey, page, pageSize, sort, dir);
    return value is null ? Missing(http, "signal_not_found", "Сигналът не е намерен в тази снимка.") : WithCache(http, value);
});
api.MapGet("/suppliers/{eik}/records/{recordId}", (string eik, string recordId, HttpContext http, SnapshotRepository repo) =>
{
    var value = repo.GetRecord(eik.Trim(), recordId);
    return value is null ? Missing(http, "record_not_found", "Изходният запис не е намерен в тази снимка.") : WithCache(http, value);
});

var auth = api.MapGroup("/auth");
auth.MapGet("/csrf", (HttpContext http, IAntiforgery antiforgery) =>
{
    NoStore(http);
    return Results.Ok(new { token = antiforgery.GetAndStoreTokens(http).RequestToken });
});
auth.MapGet("/session", (HttpContext http, AccountRepository accounts) =>
{
    NoStore(http);
    var idValue = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
    if (!Guid.TryParse(idValue, out var id)) return Results.Ok(new SessionResponse(false, null));
    var account = accounts.FindById(id);
    return Results.Ok(account is null ? new SessionResponse(false, null) : new SessionResponse(true, account.Username));
});
auth.MapPost("/register", async (AuthRequest request, HttpContext http, IAntiforgery antiforgery,
    AccountRepository accounts, IPasswordHasher<Account> hasher) =>
{
    if (!await ValidateAntiforgery(http, antiforgery)) return Csrf(http);
    NoStore(http);
    var username = request.Username?.Trim() ?? "";
    if (!Regex.IsMatch(username, "^[A-Za-z0-9_]{3,32}$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
        return Invalid(http, "invalid_username", "Потребителското име трябва да съдържа 3–32 латински букви, цифри или долна черта.");
    if (request.Password is null || request.Password.Length is < 12 or > 128)
        return Invalid(http, "invalid_password", "Паролата трябва да бъде между 12 и 128 знака.");
    var pending = new Account(Guid.NewGuid(), username, AccountRepository.NormalizeUsername(username), "", DateTimeOffset.UtcNow);
    var hash = hasher.HashPassword(pending, request.Password);
    if (!accounts.TryCreate(username, hash, out var account))
        return Results.Problem(statusCode: 409, title: "Потребителското име е заето",
            extensions: new Dictionary<string, object?> { ["code"] = "username_taken", ["traceId"] = http.TraceIdentifier });
    await SignIn(http, account);
    return Results.Json(new SessionResponse(true, account.Username), statusCode: StatusCodes.Status201Created);
}).RequireRateLimiting("auth");
auth.MapPost("/login", async (AuthRequest request, HttpContext http, IAntiforgery antiforgery,
    AccountRepository accounts, IPasswordHasher<Account> hasher) =>
{
    if (!await ValidateAntiforgery(http, antiforgery)) return Csrf(http);
    NoStore(http);
    var account = accounts.FindByUsername(request.Username?.Trim() ?? "");
    var result = account is null
        ? hasher.VerifyHashedPassword(dummyAccount, dummyPasswordHash, request.Password ?? "")
        : hasher.VerifyHashedPassword(account, account.PasswordHash, request.Password ?? "");
    if (account is null || result == PasswordVerificationResult.Failed)
        return Results.Problem(statusCode: 401, title: "Невалидни данни за вход",
            detail: "Потребителското име или паролата са неправилни.",
            extensions: new Dictionary<string, object?> { ["code"] = "invalid_credentials", ["traceId"] = http.TraceIdentifier });
    await http.SignOutAsync();
    await SignIn(http, account);
    return Results.Ok(new SessionResponse(true, account.Username));
}).RequireRateLimiting("auth");
auth.MapPost("/logout", async (HttpContext http, IAntiforgery antiforgery) =>
{
    if (!await ValidateAntiforgery(http, antiforgery)) return Csrf(http);
    NoStore(http);
    await http.SignOutAsync();
    return Results.Ok(new SessionResponse(false, null));
});

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");
app.Run();

static async Task SignIn(HttpContext http, Account account)
{
    var identity = new ClaimsIdentity([
        new Claim(ClaimTypes.NameIdentifier, account.Id.ToString("D")),
        new Claim(ClaimTypes.Name, account.Username)
    ], CookieAuthenticationDefaults.AuthenticationScheme);
    await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity),
        new AuthenticationProperties { IsPersistent = true, ExpiresUtc = DateTimeOffset.UtcNow.AddDays(7), AllowRefresh = true });
}

static async Task<bool> ValidateAntiforgery(HttpContext http, IAntiforgery antiforgery)
{
    try { await antiforgery.ValidateRequestAsync(http); return true; }
    catch (AntiforgeryValidationException) { return false; }
}

static void NoStore(HttpContext http) => http.Response.Headers.CacheControl = "no-store";
static IResult Csrf(HttpContext http) => Results.Problem(statusCode: 400, title: "Невалидна защитна заявка",
    extensions: new Dictionary<string, object?> { ["code"] = "invalid_csrf", ["traceId"] = http.TraceIdentifier });

static string ResolveSnapshotPath(string? configuredPath, string contentRootPath)
{
    if (!string.IsNullOrWhiteSpace(configuredPath))
        return Path.IsPathRooted(configuredPath) ? configuredPath : Path.GetFullPath(configuredPath, contentRootPath);
    var candidates = new[]
    {
        Path.Combine(contentRootPath, "tenderlens.db"),
        Path.GetFullPath(Path.Combine(contentRootPath, "..", "..", "..", "data", "snapshot", "tenderlens.db")),
        Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "data", "snapshot", "tenderlens.db"))
    };
    return candidates.FirstOrDefault(File.Exists) ?? candidates[1];
}

static string ResolveAccountPath(string? configuredPath, string contentRootPath) =>
    string.IsNullOrWhiteSpace(configuredPath)
        ? Path.Combine(contentRootPath, "data", "accounts.db")
        : Path.IsPathRooted(configuredPath) ? configuredPath : Path.GetFullPath(configuredPath, contentRootPath);

static bool IsPathWithin(string candidate, string root)
{
    var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(candidate));
    return relative != ".." && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
}

static IResult WithCache<T>(HttpContext http, T value)
{
    var etag = $"\"{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions.Default))).ToLowerInvariant()[..16]}\"";
    var matches = http.Request.Headers.IfNoneMatch.SelectMany(value => value?.Split(',') ?? []).Select(value => value.Trim())
        .Any(value => value == "*" || value == etag || value.StartsWith("W/", StringComparison.OrdinalIgnoreCase) && value[2..] == etag);
    if (matches) return Results.StatusCode(StatusCodes.Status304NotModified);
    http.Response.Headers.ETag = etag;
    http.Response.Headers.CacheControl = "public,max-age=300";
    return Results.Json(value, JsonOptions.Default);
}

static bool TryPositiveInt(string? raw, int defaultValue, int min, int max, out int value)
{
    if (string.IsNullOrEmpty(raw)) { value = defaultValue; return true; }
    return int.TryParse(raw, out value) && value >= min && value <= max;
}

static IResult Invalid(HttpContext http, string code, string detail) => Results.Problem(statusCode: 400, title: "Невалидна заявка", detail: detail,
    extensions: new Dictionary<string, object?> { ["code"] = code, ["traceId"] = http.TraceIdentifier });
static IResult Missing(HttpContext http, string code, string detail) => Results.Problem(statusCode: 404, title: "Не е намерено", detail: detail,
    extensions: new Dictionary<string, object?> { ["code"] = code, ["traceId"] = http.TraceIdentifier });

public sealed record AuthRequest(string? Username, string? Password);
public sealed record SessionResponse(bool Authenticated, string? Username);
public static class Eik { public static bool IsValid(string value) => value.Length is 9 or 13 && value.All(char.IsDigit); }
public partial class Program { }

using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.ResponseCompression;
using TenderLens.Data;

var builder = WebApplication.CreateBuilder(args);
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
        RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));
    options.OnRejected = async (context, token) =>
    {
        var retry = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var delay)
            ? Math.Max(1, (int)Math.Ceiling(delay.TotalSeconds)) : 60;
        context.HttpContext.Response.Headers.RetryAfter = retry.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await Results.Problem(statusCode: 429, title: "Твърде много заявки", detail: "Изчакайте и опитайте отново.", extensions: new Dictionary<string, object?> { ["code"] = "rate_limit_exceeded", ["traceId"] = context.HttpContext.TraceIdentifier }).ExecuteAsync(context.HttpContext);
    };
});
var snapshotPath = ResolveSnapshotPath(builder.Configuration["SnapshotPath"], builder.Environment.ContentRootPath);
builder.Services.AddSingleton(new SnapshotRepository(snapshotPath));

var app = builder.Build();
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
    if (context.Request.Method is not ("GET" or "HEAD"))
    {
        context.Response.Headers.Allow = "GET, HEAD";
        await Results.Problem(statusCode: 405, title: "Неподдържан метод", extensions: new Dictionary<string, object?> { ["code"] = "method_not_allowed", ["traceId"] = context.TraceIdentifier }).ExecuteAsync(context);
        return;
    }
    if (context.Request.ContentLength is > 16384)
    {
        await Results.Problem(statusCode: 413, title: "Заявката е твърде голяма", extensions: new Dictionary<string, object?> { ["code"] = "request_too_large", ["traceId"] = context.TraceIdentifier }).ExecuteAsync(context);
        return;
    }
    await next();
});
app.UseRateLimiter();

app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", (SnapshotRepository repo) => repo.IsReady() ? Results.Ok(new { status = "ready" }) : Results.Problem(statusCode: 503, title: "Снимката на данните не е налична", extensions: new Dictionary<string, object?> { ["code"] = "snapshot_unavailable" }));

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
        return Invalid(http, "invalid_pagination", "Страницата трябва да е между 1 и 100000, а размерът ѝ да е между 1 и 100.");
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

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");
app.Run();

static string ResolveSnapshotPath(string? configuredPath, string contentRootPath)
{
    if (!string.IsNullOrWhiteSpace(configuredPath))
        return Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.GetFullPath(configuredPath, contentRootPath);

    var candidates = new[]
    {
        Path.Combine(contentRootPath, "tenderlens.db"),
        Path.GetFullPath(Path.Combine(contentRootPath, "..", "..", "..", "data", "snapshot", "tenderlens.db")),
        Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "data", "snapshot", "tenderlens.db"))
    };

    return candidates.FirstOrDefault(File.Exists) ?? candidates[1];
}

static IResult WithCache<T>(HttpContext http, T value)
{
    var etag = $"\"{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions.Default))).ToLowerInvariant()[..16]}\"";
    var matches = http.Request.Headers.IfNoneMatch
        .SelectMany(value => value?.Split(',') ?? [])
        .Select(value => value.Trim())
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

static IResult Invalid(HttpContext http, string code, string detail) => Results.Problem(statusCode: 400, title: "Невалидна заявка", detail: detail, extensions: new Dictionary<string, object?> { ["code"] = code, ["traceId"] = http.TraceIdentifier });
static IResult Missing(HttpContext http, string code, string detail) => Results.Problem(statusCode: 404, title: "Не е намерено", detail: detail, extensions: new Dictionary<string, object?> { ["code"] = code, ["traceId"] = http.TraceIdentifier });

public static class Eik
{
    public static bool IsValid(string value) => value.Length is 9 or 13 && value.All(char.IsDigit);
}

public partial class Program { }

using System.Threading.RateLimiting;
using TenderLens.Data;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();
builder.Services.AddProblemDetails();
builder.Services.AddRateLimiter(options => options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })));
var snapshotPath = ResolveSnapshotPath(builder.Configuration["SnapshotPath"], builder.Environment.ContentRootPath);
builder.Services.AddSingleton(new SnapshotRepository(snapshotPath));

var app = builder.Build();
app.UseExceptionHandler();
app.UseRateLimiter();
app.Use(async (context, next) =>
{
    context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; object-src 'none'; base-uri 'self'";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    await next();
});

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
    var value = repo.GetSignal(eik.Trim(), signalKey);
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
    if (http.Request.Headers.IfNoneMatch == etag) return Results.StatusCode(StatusCodes.Status304NotModified);
    http.Response.Headers.ETag = etag;
    http.Response.Headers.CacheControl = "public,max-age=300";
    return Results.Json(value, JsonOptions.Default);
}

static IResult Invalid(HttpContext http, string code, string detail) => Results.Problem(statusCode: 400, title: "Невалидна заявка", detail: detail, extensions: new Dictionary<string, object?> { ["code"] = code, ["traceId"] = http.TraceIdentifier });
static IResult Missing(HttpContext http, string code, string detail) => Results.Problem(statusCode: 404, title: "Не е намерено", detail: detail, extensions: new Dictionary<string, object?> { ["code"] = code, ["traceId"] = http.TraceIdentifier });

public static class Eik
{
    public static bool IsValid(string value) => value.Length is 9 or 13 && value.All(char.IsDigit);
}

public partial class Program { }

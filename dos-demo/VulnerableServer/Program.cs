using System.Diagnostics;
using Microsoft.AspNetCore.Http.Features;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenLocalhost(5099);
    options.Limits.MaxRequestBodySize = 30 * 1024 * 1024;
});

builder.Services.AddRequestDecompression();

var app = builder.Build();

// ── VULNERABILITY ROOT CAUSE ───────────────────────────────────────────────
// RequestDecompressionMiddleware resolves sizeLimit as:
//   endpoint.IRequestSizeLimitMetadata?.MaxRequestBodySize
//     ?? IHttpMaxRequestBodySizeFeature?.MaxRequestBodySize
//
// SizeLimitedStream with _sizeLimit=null: check is (_totalBytesRead > null)
// which is ALWAYS FALSE in C# — no limit enforced.
//
// TRIGGER: nullify IHttpMaxRequestBodySizeFeature BEFORE the decompression
// middleware wraps the stream. A middleware placed BEFORE UseRequestDecompression
// does exactly this — a realistic misconfiguration (e.g. a blanket "allow large
// uploads" middleware, or any non-Kestrel host that never sets the feature).
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path.Value ?? "";
    if (path.StartsWith("/upload", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/json", StringComparison.OrdinalIgnoreCase))
    {
        // Simulate: host does not provide IHttpMaxRequestBodySizeFeature,
        // OR developer adds a "remove all size limits" middleware before
        // UseRequestDecompression — nullifies the fallback.
        var feature = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature != null) feature.MaxRequestBodySize = null;
    }
    await next(ctx);
});

// Now the decompression middleware sees: sizeLimit = null ?? null = null
// → SizeLimitedStream._sizeLimit = null → _totalBytesRead > null → always false → NO LIMIT
app.UseRequestDecompression();

// SAFE: limit is still 30 MB (feature not nullified before middleware)
app.MapPost("/safe", async (HttpContext ctx) =>
{
    long bytesRead = 0;
    var buf = new byte[65536];
    int n;
    while ((n = await ctx.Request.Body.ReadAsync(buf)) > 0)
        bytesRead += n;
    return Results.Ok(new { decompressed_bytes = bytesRead, endpoint = "safe" });
});

// VULNERABLE: limit nullified before decompression middleware — no cap on decompressed size
app.MapPost("/upload", async (HttpContext ctx) =>
{
    var sw = Stopwatch.StartNew();
    long bytesRead = 0;
    var buf = new byte[65536];
    int n;
    while ((n = await ctx.Request.Body.ReadAsync(buf)) > 0)
        bytesRead += n;

    return Results.Ok(new {
        decompressed_bytes = bytesRead,
        decompressed_mb    = bytesRead / 1024 / 1024,
        elapsed_ms         = sw.ElapsedMilliseconds,
        endpoint           = "upload (VULNERABLE)"
    });
});

// OOM endpoint: accumulates ALL decompressed bytes in memory
// This is what JSON model binding, ReadToEndAsync(), form parsing all do.
app.MapPost("/json", async (HttpContext ctx) =>
{
    var sw = Stopwatch.StartNew();
    // ReadToEndAsync buffers the entire decompressed body — real apps do this
    var body = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
    var proc = Process.GetCurrentProcess();
    return Results.Ok(new {
        body_length_mb = body.Length / 1024 / 1024,
        elapsed_ms     = sw.ElapsedMilliseconds,
        working_set_mb = proc.WorkingSet64 / 1024 / 1024,
        gc_total_mb    = GC.GetTotalMemory(false) / 1024 / 1024,
        endpoint       = "json (ACCUMULATES IN MEMORY)"
    });
});

app.MapGet("/health", () =>
{
    var proc = Process.GetCurrentProcess();
    return Results.Ok(new {
        pid            = proc.Id,
        working_set_mb = proc.WorkingSet64 / 1024 / 1024,
        gc_total_mb    = GC.GetTotalMemory(false) / 1024 / 1024,
        status         = "alive"
    });
});

Console.WriteLine("[SERVER] http://localhost:5099");
Console.WriteLine("[SERVER] POST /safe   — protected (30 MB decompressed cap)");
Console.WriteLine("[SERVER] POST /upload — VULNERABLE (null limit → unlimited decompression)");
Console.WriteLine("[SERVER] GET  /health — live memory");
app.Run();

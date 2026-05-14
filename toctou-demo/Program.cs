using Microsoft.Extensions.Caching.Distributed;
using System.Collections.Concurrent;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<IDistributedCache>(_ => new SlowDistributedCache(delayMs: 300));
builder.Services.AddSession(o =>
{
    o.IdleTimeout = TimeSpan.FromMinutes(10);
    o.Cookie.IsEssential = true;
});
builder.Logging.SetMinimumLevel(LogLevel.Warning);

var app = builder.Build();
app.UseSession();

// Step 1: seed 500 keys so Deserialize has real work to do during the race
app.MapGet("/seed", async (HttpContext ctx) =>
{
    await ctx.Session.LoadAsync();
    for (int i = 0; i < 500; i++)
        ctx.Session.SetString($"key{i:D4}", new string((char)('A' + i % 26), 100));
    await ctx.Session.CommitAsync();
    return $"OK — 500 keys seeded. session={ctx.Session.Id[..8]}";
});

// Step 2: fire N concurrent LoadAsync on the same ISession object.
// All N calls pass `if (!_loaded)` before any sets _loaded=true,
// then all resume simultaneously after the 300ms cache delay → concurrent
// writes to the plain Dictionary<EncodedKey,byte[]> inside the session.
app.MapGet("/attack/{concurrency:int?}", async (HttpContext ctx, int concurrency = 20) =>
{
    var runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
    var caught = new ConcurrentBag<(int id, string type, string msg)>();

    // Kick off ALL tasks before awaiting any — this is the race
    var tasks = Enumerable.Range(0, concurrency).Select(async i =>
    {
        try { await ctx.Session.LoadAsync(); }
        catch (Exception ex) { caught.Add((i, ex.GetType().Name, ex.Message)); }
    }).ToList();

    await Task.WhenAll(tasks);

    // Probe the dictionary — may itself throw if internal structure is corrupted
    int keyCount = -1;
    string probeError = "";
    try { keyCount = ctx.Session.Keys.Count(); }
    catch (Exception ex) { probeError = $"{ex.GetType().Name}: {ex.Message}"; }

    var sb = new System.Text.StringBuilder();
    sb.AppendLine($"Runtime     : {runtime}");
    sb.AppendLine($"Concurrency : {concurrency} simultaneous LoadAsync calls");
    sb.AppendLine($"Expected    : 500 keys");
    sb.AppendLine($"Actual keys : {(keyCount == -1 ? "PROBE THREW — " + probeError : keyCount.ToString())}");

    bool corrupted = !caught.IsEmpty || keyCount != 500;
    if (!corrupted)
    {
        sb.AppendLine("Result      : No observable corruption this run (race is non-deterministic).");
        sb.AppendLine("              Try /attack/50 or repeat to increase probability.");
    }
    else
    {
        if (!caught.IsEmpty)
        {
            sb.AppendLine($"Exceptions  : {caught.Count}");
            foreach (var (id, type, msg) in caught.Take(5))
                sb.AppendLine($"  task#{id:D2}  {type}: {msg}");
        }
        if (keyCount != 500 && keyCount != -1)
            sb.AppendLine($"CORRUPTION  : {500 - keyCount} keys lost (concurrent partial writes)");
        if (probeError != "")
            sb.AppendLine($"CORRUPTION  : probe threw — internal Dictionary structure corrupted");
        sb.AppendLine(">>> BUG CONFIRMED — TOCTOU race in DistributedSession.LoadAsync <<<");
    }

    return Results.Text(sb.ToString());
});

app.Run("http://localhost:5199");

// ─────────────────────────────────────────────────────────────────────────────
// SlowDistributedCache: in-memory store with artificial GetAsync delay.
// The delay ensures all concurrent LoadAsync calls are suspended at the await
// point before any of them completes, maximising the TOCTOU window.
// ─────────────────────────────────────────────────────────────────────────────
public sealed class SlowDistributedCache : IDistributedCache
{
    private readonly Dictionary<string, (byte[] data, DateTimeOffset? abs)> _store = new();
    private readonly object _lock = new();
    private readonly int _delayMs;

    public SlowDistributedCache(int delayMs = 300) => _delayMs = delayMs;

    public async Task<byte[]?> GetAsync(string key, CancellationToken token = default)
    {
        await Task.Delay(_delayMs, token);
        return Get(key);
    }

    public byte[]? Get(string key)
    {
        lock (_lock)
        {
            if (_store.TryGetValue(key, out var e))
            {
                if (e.abs == null || e.abs > DateTimeOffset.UtcNow) return e.data;
                _store.Remove(key);
            }
            return null;
        }
    }

    public void Set(string key, byte[] value, DistributedCacheEntryOptions opts)
    {
        DateTimeOffset? abs = opts.AbsoluteExpiration
            ?? (opts.AbsoluteExpirationRelativeToNow.HasValue
                ? DateTimeOffset.UtcNow + opts.AbsoluteExpirationRelativeToNow.Value
                : opts.SlidingExpiration.HasValue
                    ? DateTimeOffset.UtcNow + opts.SlidingExpiration.Value
                    : null);
        lock (_lock) { _store[key] = (value, abs); }
    }

    public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions opts,
        CancellationToken token = default)
    { Set(key, value, opts); return Task.CompletedTask; }

    public void Remove(string key) { lock (_lock) { _store.Remove(key); } }
    public Task RemoveAsync(string key, CancellationToken token = default)
    { Remove(key); return Task.CompletedTask; }

    public void Refresh(string key) { }
    public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;
}

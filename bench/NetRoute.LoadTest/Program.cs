using System.Diagnostics;
using System.Net.Http.Headers;

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: NetRoute.LoadTest <url> [concurrency] [seconds] [bearerToken] [warmupSeconds]");
    return 2;
}

var url = args[0];
var concurrency = args.Length > 1 ? int.Parse(args[1]) : 64;
var seconds = args.Length > 2 ? int.Parse(args[2]) : 10;
var token = args.Length > 3 && args[3] != "-" ? args[3] : null;
var warmup = args.Length > 4 ? int.Parse(args[4]) : 2;

using var handler = new SocketsHttpHandler { MaxConnectionsPerServer = concurrency, PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
using var client = new HttpClient(handler);
if (token is not null)
{
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
}

async Task<(long[] Latencies, Dictionary<int, long> Codes)> RunAsync(int durationSeconds)
{
    var deadline = Stopwatch.GetTimestamp() + durationSeconds * Stopwatch.Frequency;
    var workers = Enumerable.Range(0, concurrency).Select(_ => Task.Run(async () =>
    {
        var latencies = new List<long>(1 << 16);
        var codes = new Dictionary<int, long>();
        while (Stopwatch.GetTimestamp() < deadline)
        {
            var started = Stopwatch.GetTimestamp();
            int code;
            try
            {
                using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                await response.Content.ReadAsByteArrayAsync();
                code = (int)response.StatusCode;
            }
            catch (HttpRequestException)
            {
                code = 0;
            }

            latencies.Add(Stopwatch.GetTimestamp() - started);
            codes[code] = codes.GetValueOrDefault(code) + 1;
        }

        return (latencies, codes);
    })).ToArray();

    var results = await Task.WhenAll(workers);
    var all = results.SelectMany(r => r.latencies).ToArray();
    Array.Sort(all);
    var merged = new Dictionary<int, long>();
    foreach (var (_, codes) in results)
    {
        foreach (var (code, n) in codes)
        {
            merged[code] = merged.GetValueOrDefault(code) + n;
        }
    }

    return (all, merged);
}

if (warmup > 0)
{
    await RunAsync(warmup);
}

var sw = Stopwatch.StartNew();
var (sorted, statuses) = await RunAsync(seconds);
sw.Stop();

double Ms(double q) => sorted.Length == 0 ? 0 : sorted[(int)Math.Min(sorted.Length - 1, q * sorted.Length)] * 1000.0 / Stopwatch.Frequency;

Console.WriteLine($"url={url} concurrency={concurrency} duration={sw.Elapsed.TotalSeconds:0.0}s");
Console.WriteLine($"requests={sorted.Length} rps={sorted.Length / sw.Elapsed.TotalSeconds:0}");
Console.WriteLine($"latency ms  p50={Ms(0.50):0.00}  p95={Ms(0.95):0.00}  p99={Ms(0.99):0.00}  max={Ms(1):0.00}");
Console.WriteLine("status: " + string.Join(", ", statuses.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}")));
return 0;

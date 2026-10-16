// ADR-0015: the P6 intraday overlay's transport, measured. One server process (this program with `serve`) pushes
// the same simulated marks (Desk.Data MarkSimulator, 1 s ticks) three ways; this process runs N clients per
// transport for a while and reports, per client-minute: bytes on the wire (both directions, HTTP headers and
// framing included, counted at the socket), requests (each one spends a rate-limiter token and passes the auth
// handler), mark latency (receive time - the mark's event_ts, p50/p95/max), and the server's CPU time and allocations.
// Every request carries a bearer token of --token-bytes characters (the Identity token's measured size), as in
// production (ADR-0024).
//
//   dotnet run -c Release --project perf/MarksTransport -- [--clients 50] [--seconds 60] [--bonds 100] [--token-bytes 0]
//
// Local only: the server listens on 127.0.0.1 and is stopped at the end. Prints a markdown table.
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Desk.Data.Marks;

var opts = Options.Parse(args);
if (opts.Serve)
{
    await Server.RunAsync(opts);
    return 0;
}

var port = FreePort();
using var server = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, ["serve", "--port", port.ToString(CultureInfo.InvariantCulture), "--bonds", opts.Bonds.ToString(CultureInfo.InvariantCulture)])
{
    RedirectStandardOutput = true,
})!;
try
{
    var baseUri = new Uri($"http://127.0.0.1:{port}");
    await WaitUpAsync(baseUri);
    var token = new string('t', opts.TokenBytes);
    Console.WriteLine($"MarksTransport: {opts.Clients} clients × {opts.Seconds} s per transport, {opts.Bonds} bonds, 1 s ticks, bearer token {opts.TokenBytes} chars");
    Console.WriteLine();
    Console.WriteLine("| Transport | Bytes/client-min (down + up) | Requests/client-min | Latency p50 / p95 / max (ms) | Marks/client-min | Server CPU ms/client-min | Server alloc KB/client-min |");
    Console.WriteLine("|---|---|---|---|---|---|---|");
    foreach (var transport in new[] { "sse", "ws", "poll-1s", "poll-5s" })
    {
        var result = await Bench.RunAsync(transport, baseUri, opts, token, server);
        Console.WriteLine(result.Row(opts));
    }
    return 0;
}
finally
{
    server.Kill(entireProcessTree: true);
}

static int FreePort()
{
    using var l = new TcpListener(IPAddress.Loopback, 0);
    l.Start();
    return ((IPEndPoint)l.LocalEndpoint).Port;
}

static async Task WaitUpAsync(Uri baseUri)
{
    using var http = new HttpClient { BaseAddress = baseUri };
    for (var i = 0; i < 100; i++)
    {
        try { if ((await http.GetAsync("/stats")).IsSuccessStatusCode) return; }
        catch (HttpRequestException) { }
        await Task.Delay(100);
    }
    throw new InvalidOperationException("server did not start");
}

internal sealed record Options(bool Serve, int Port, int Clients, int Seconds, int Bonds, int TokenBytes)
{
    public static Options Parse(string[] args)
    {
        int Int(string name, int fallback)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? int.Parse(args[i + 1], CultureInfo.InvariantCulture) : fallback;
        }
        return new(args.FirstOrDefault() == "serve", Int("--port", 0), Int("--clients", 50), Int("--seconds", 60), Int("--bonds", 100), Int("--token-bytes", 0));
    }
}

/// <summary>The server side: the same universe and simulator as the API, three transports.</summary>
internal static class Server
{
    private static readonly MarkSimulator Sim = new(TimeSpan.FromSeconds(1));

    public static async Task RunAsync(Options opts)
    {
        var bonds = Enumerable.Range(1, opts.Bonds).Select(i => new MarkedBond(1000 + i, 90 + i % 15, 150 + i)).ToArray();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{opts.Port}");
        var app = builder.Build();
        app.UseWebSockets();

        app.MapGet("/stats", () => Results.Text(string.Create(CultureInfo.InvariantCulture,
            $"{GC.GetTotalAllocatedBytes(precise: true)} {Process.GetCurrentProcess().TotalProcessorTime.TotalMilliseconds}")));

        // SSE: one response per client; each tick writes the marks that changed (the API's format).
        app.MapGet("/sse", async (HttpContext http, CancellationToken ct) =>
        {
            http.Response.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            var after = long.MinValue;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var now = Sim.TickAt(DateTimeOffset.UtcNow);
                    var sb = new StringBuilder();
                    foreach (var m in Sim.ChangedBetween(bonds, after, now))
                        sb.Append("event: mark\nid: ").Append(m.Seq).Append("\ndata: ").Append(JsonSerializer.Serialize(m, Json.Web)).Append("\n\n");
                    after = now;
                    if (sb.Length > 0)
                    {
                        await http.Response.WriteAsync(sb.ToString(), ct);
                        await http.Response.Body.FlushAsync(ct);
                    }
                    await Task.Delay(Sim.TimeOf(now + 1) - DateTimeOffset.UtcNow, ct);
                }
            }
            catch (OperationCanceledException) { }
        });

        // WebSockets: one connection per client; one text message per mark.
        app.Map("/ws", async (HttpContext http, CancellationToken ct) =>
        {
            using var ws = await http.WebSockets.AcceptWebSocketAsync();
            var after = long.MinValue;
            try
            {
                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    var now = Sim.TickAt(DateTimeOffset.UtcNow);
                    foreach (var m in Sim.ChangedBetween(bonds, after, now))
                        await ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(m, Json.Web), WebSocketMessageType.Text, true, ct);
                    after = now;
                    await Task.Delay(Sim.TimeOf(now + 1) - DateTimeOffset.UtcNow, ct);
                }
            }
            catch (Exception e) when (e is OperationCanceledException or WebSocketException) { }
        });

        // Polling: the client asks what changed after its last seq.
        app.MapGet("/poll", (long? after) =>
        {
            var now = Sim.TickAt(DateTimeOffset.UtcNow);
            return Results.Json(new Poll(now, [.. Sim.ChangedBetween(bonds, after ?? long.MinValue, now)]), Json.Web);
        });

        await app.RunAsync();
    }
}

internal sealed record Poll(long Seq, Mark[] Marks);

internal static class Json
{
    public static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
}

/// <summary>Bytes read and written by every socket the handler opens.</summary>
internal sealed class WireCounter
{
    private long _down, _up;
    public long Down => Interlocked.Read(ref _down);
    public long Up => Interlocked.Read(ref _up);

    public SocketsHttpHandler Handler() => new()
    {
        ConnectCallback = async (ctx, ct) =>
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            await socket.ConnectAsync(ctx.DnsEndPoint, ct);
            return new Counting(new NetworkStream(socket, ownsSocket: true), this);
        },
        PooledConnectionLifetime = Timeout.InfiniteTimeSpan,
    };

    private sealed class Counting(Stream inner, WireCounter c) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
        public override int Read(byte[] buffer, int offset, int count) => Add(ref c._down, inner.Read(buffer, offset, count));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => Add(ref c._down, await inner.ReadAsync(buffer, ct));
        public override void Write(byte[] buffer, int offset, int count) { Add(ref c._up, count); inner.Write(buffer, offset, count); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) { Add(ref c._up, buffer.Length); return inner.WriteAsync(buffer, ct); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }

        private static int Add(ref long field, int n)
        {
            Interlocked.Add(ref field, n);
            return n;
        }
    }
}

internal sealed record Result(string Transport, int Clients, double Minutes, long Down, long Up, long Requests, long Marks, double[] LatenciesMs, double CpuMs, long AllocBytes)
{
    public string Row(Options o)
    {
        var perClientMin = Clients * Minutes;
        var sorted = LatenciesMs.Order().ToArray();
        double P(double q) => sorted.Length == 0 ? double.NaN : sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(q * sorted.Length) - 1)];
        return string.Create(CultureInfo.InvariantCulture,
            $"| {Transport} | {(Down + Up) / perClientMin / 1024:0.0} KB ({Down / perClientMin / 1024:0.0} + {Up / perClientMin / 1024:0.0}) | {Requests / perClientMin:0.0} | {P(0.5):0} / {P(0.95):0} / {sorted.LastOrDefault():0} | {Marks / perClientMin:0} | {CpuMs / perClientMin:0.0} | {AllocBytes / perClientMin / 1024:0} |");
    }
}

internal static class Bench
{
    public static async Task<Result> RunAsync(string transport, Uri baseUri, Options o, string token, Process server)
    {
        var wire = new WireCounter();
        using var http = new HttpClient(wire.Handler()) { BaseAddress = baseUri, Timeout = Timeout.InfiniteTimeSpan };
        if (token.Length > 0) http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        using var stats = new HttpClient { BaseAddress = baseUri };
        var before = await StatsAsync(stats);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(o.Seconds));
        var latencies = new System.Collections.Concurrent.ConcurrentBag<double>();
        long requests = 0, marks = 0;
        var started = Stopwatch.GetTimestamp();

        // Latency counts only marks made after the client connected: the catch-up on connect carries each bond's
        // current mark, whose event_ts is up to 14 ticks old by design.
        void Seen(Mark m, long connected)
        {
            if (m.Seq > connected) latencies.Add((DateTimeOffset.UtcNow - m.EventTs).TotalMilliseconds);
            Interlocked.Increment(ref marks);
        }

        async Task ClientAsync(int i)
        {
            // Spread the starts over a second, as real users would be.
            await Task.Delay(i * 1000 / o.Clients);
            var after = long.MinValue;
            var connected = new MarkSimulator(TimeSpan.FromSeconds(1)).TickAt(DateTimeOffset.UtcNow);
            try
            {
                switch (transport)
                {
                    case "sse":
                        Interlocked.Increment(ref requests);
                        using (var res = await http.GetAsync("/sse", HttpCompletionOption.ResponseHeadersRead, stop.Token))
                        await foreach (var item in System.Net.ServerSentEvents.SseParser.Create(await res.Content.ReadAsStreamAsync(stop.Token)).EnumerateAsync(stop.Token))
                            Seen(JsonSerializer.Deserialize<Mark>(item.Data, Json.Web)!, connected);
                        break;
                    case "ws":
                        Interlocked.Increment(ref requests);
                        using (var ws = new ClientWebSocket())
                        {
                            if (token.Length > 0) ws.Options.SetRequestHeader("Authorization", "Bearer " + token);
                            await ws.ConnectAsync(new Uri($"ws://{baseUri.Authority}/ws"), new HttpMessageInvoker(wire.Handler()), stop.Token);
                            var buffer = new byte[64 * 1024];
                            while (!stop.IsCancellationRequested)
                            {
                                var r = await ws.ReceiveAsync(buffer, stop.Token);
                                Seen(JsonSerializer.Deserialize<Mark>(buffer.AsSpan(0, r.Count), Json.Web)!, connected);
                            }
                        }
                        break;
                    default:
                        var every = TimeSpan.FromSeconds(transport == "poll-1s" ? 1 : 5);
                        while (!stop.IsCancellationRequested)
                        {
                            Interlocked.Increment(ref requests);
                            var poll = await http.GetFromJsonAsync<Poll>($"/poll?after={after}", Json.Web, stop.Token);
                            foreach (var m in poll!.Marks) Seen(m, connected);
                            after = poll.Seq;
                            await Task.Delay(every, stop.Token);
                        }
                        break;
                }
            }
            catch (Exception e) when (e is OperationCanceledException or WebSocketException or HttpRequestException or IOException) { }
        }

        await Task.WhenAll(Enumerable.Range(0, o.Clients).Select(ClientAsync));
        var minutes = Stopwatch.GetElapsedTime(started).TotalMinutes;
        var after = await StatsAsync(stats);
        return new Result(transport, o.Clients, minutes, wire.Down, wire.Up, requests, marks, [.. latencies], after.CpuMs - before.CpuMs, after.Alloc - before.Alloc);
    }

    private static async Task<(long Alloc, double CpuMs)> StatsAsync(HttpClient stats)
    {
        var parts = (await stats.GetStringAsync("/stats")).Split(' ');
        return (long.Parse(parts[0], CultureInfo.InvariantCulture), double.Parse(parts[1], CultureInfo.InvariantCulture));
    }
}

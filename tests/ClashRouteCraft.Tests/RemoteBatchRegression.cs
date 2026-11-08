using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ClashRouteCraft.Core;
using static ClashRouteCraft.Core.Data;

static class RemoteBatchRegression
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("URL batch bounds concurrency, reuses connections and reports each original row", async () =>
        {
            using var server = new BatchServer(block: true); using var fixture = new Fixture();
            var sources = Enumerable.Range(0, 9).Select(i => O("url", server.Url + i)).ToArray();
            var callbacks = new ConcurrentDictionary<int, RemoteRuleCheck>();
            var pending = RemoteRuleValidation.CheckBatchAsync(fixture.Root, sources, O("trust_env", false), (i, r) => { Check(callbacks.TryAdd(i, r), "Duplicate callback"); return Task.CompletedTask; });
            await server.FourStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Check(server.Requests == 4 && !pending.IsCompleted, "Batch is serial or exceeded four requests");
            server.Release.TrySetResult();
            var results = await pending;
            Check(results.Length == 9 && results.All(x => x.Success) && callbacks.Count == 9, "Missing or failed result");
            Check(server.MaxActive <= 4 && server.Connections <= 4 && server.Requests == 9, "Connection pool was not reused or concurrency escaped");
        });
        await test("URL batch deduplicates requests but isolates headers, parsers, failures and warnings", async () =>
        {
            using var server = new BatchServer(); using var fixture = new Fixture();
            var sources = new[] { O("url", server.Url), O("url", server.Url), O("url", server.Url, "format", "mrs"),
                O("url", server.Url, "headers", O("X-Test", "different")), O("url", server.Url + "bad"),
                O("url", server.Url + "warning"), O("url", server.Url + "unavailable"), O("url", server.Url + "unavailable") };
            var before = Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories);
            var results = await RemoteRuleValidation.CheckBatchAsync(fixture.Root, sources, O("trust_env", false));
            Check(server.Requests == 5, "Identical requests downloaded more than once or differing headers were merged");
            Check(results[0].Success && results[1].Success && !results[2].Success && results[3].Success && !results[4].Success, "Per-row parser or failure leaked");
            Check(results[5].Success && results[5].SkippedCount == 1 && results[0].SkippedCount == 0, "Warnings leaked across parallel jobs");
            Check(!results[6].Success && results[6].Message.Contains("403") && results[6] == results[7], "Download failure not shared correctly");
            Check(before.SequenceEqual(Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories)), "Validation wrote cache or configuration");
            Check((await RemoteRuleValidation.CheckAsync(fixture.Root, sources[0], O("trust_env", false))).Success && server.Requests == 6, "Next batch reused stale data");
        });
        await test("URL batch cancellation stops queued work and suppresses completed callbacks", async () =>
        {
            using var server = new BatchServer(block: true); using var fixture = new Fixture(); using var cancel = new CancellationTokenSource();
            int callbacks = 0;
            var sources = Enumerable.Range(0, 12).Select(i => O("url", server.Url + i)).ToArray();
            var pending = RemoteRuleValidation.CheckBatchAsync(fixture.Root, sources, O("trust_env", false), (_, _) => { Interlocked.Increment(ref callbacks); return Task.CompletedTask; }, cancel.Token);
            await server.FourStarted.Task.WaitAsync(TimeSpan.FromSeconds(10)); cancel.Cancel();
            try { await pending; throw new Exception("Cancellation was swallowed"); } catch (OperationCanceledException) { }
            Check(server.Requests == 4 && callbacks == 0, "Cancelled batch sent queued requests or callbacks");
        });
    }

    sealed class BatchServer : IDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly CancellationTokenSource stop = new();
        public readonly TaskCompletionSource FourStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Requests, Connections, MaxActive;
        int active;
        public string Url => "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/";
        public BatchServer(bool block = false) { if (!block) Release.SetResult(); listener.Start(); _ = Accept(); }
        async Task Accept()
        {
            try { while (!stop.IsCancellationRequested) { var client = await listener.AcceptTcpClientAsync(stop.Token); Interlocked.Increment(ref Connections); _ = Respond(client); } }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
        }
        async Task Respond(TcpClient client)
        {
            using (client)
            try
            {
                var stream = client.GetStream(); using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
                while (await reader.ReadLineAsync(stop.Token) is { Length: > 0 } line)
                {
                    while (await reader.ReadLineAsync(stop.Token) is { Length: > 0 }) { }
                    Interlocked.Increment(ref Requests); int current = Interlocked.Increment(ref active);
                    lock (this) MaxActive = Math.Max(MaxActive, current);
                    if (current == 4) FourStarted.TrySetResult();
                    try
                    {
                        await Release.Task.WaitAsync(stop.Token);
                        string body = line.Contains("/bad ") ? "<html>invalid</html>" : line.Contains("/warning ") ? "DOMAIN,valid.example\nDOMAIN,bad name.example" : "DOMAIN,valid.example";
                        int status = line.Contains("/unavailable ") ? 403 : 200;
                        var bytes = Encoding.UTF8.GetBytes(body);
                        await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Test\r\nContent-Length: {bytes.Length}\r\nConnection: keep-alive\r\n\r\n"), stop.Token);
                        await stream.WriteAsync(bytes, stop.Token);
                    }
                    finally { Interlocked.Decrement(ref active); }
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException) { }
        }
        public void Dispose() { stop.Cancel(); listener.Stop(); }
    }
}

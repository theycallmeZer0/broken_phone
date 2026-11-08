using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BrokenPhone;

// Compile as a separate C# 5 console executable beside the BrokenPhone.exe being
// measured. Arguments: an existing payload file and the JSON result destination.
internal static class DownloadBenchmark
{
    public sealed class Result
    {
        public bool passed;
        public long bytes;
        public string sha256, assemblyVersion, assemblyBuild, error;
        public int minimumWorkerThreads;
        public string timingScope = "Loopback only. Excludes room setup and independent output verification; swarm includes helper discovery, the initial cold chunk plan, transfer and Fetch's final hash. No forced GC or throttling.";
        public List<Scenario> scenarios = new List<Scenario>();
    }
    public sealed class Scenario
    {
        public string name, description;
        public double milliseconds;
        public int receivers, planRequests, httpRequests, payloadStreams;
        public long bytes;
        public bool verified, exactProgress;
        public string[] outputHashes;
        public long[] finalProgress;
        public int[] progressCalls;
    }
    sealed class Progress
    {
        readonly object gate = new object();
        readonly long expected;
        public long Received;
        public int Calls;
        public Progress(long bytes) { expected = bytes; }
        public void Update(long received, long total)
        {
            lock (gate)
            {
                if (total != expected || received < Received || received > expected) throw new InvalidDataException("Incorrect download progress.");
                Received = received; Calls++;
            }
        }
        public void Verify() { if (Calls == 0 || Received != expected) throw new InvalidDataException("Progress did not finish at the exact payload length."); }
    }
    sealed class ObservedClient : IDisposable
    {
        readonly object gate = new object();
        readonly RoomClient client = new RoomClient();
        RoomState state;
        string failure;
        public RoomClient Client { get { return client; } }
        public ObservedClient()
        {
            client.Received += message => { lock (gate) { if (message.state != null) state = message.state; if (message.kind == "error" || message.kind == "kicked") failure = message.text; Monitor.PulseAll(gate); } };
            client.Disconnected += reason => { lock (gate) { failure = reason; Monitor.PulseAll(gate); } };
        }
        public void Connect(RoomServer server, bool host, string name)
        {
            client.Connect("127.0.0.1", server.Port, server.Code, name, host ? server.Secret : "");
            Wait(value => value.players.Any(player => player.id == client.Id));
        }
        public void Wait(Func<RoomState, bool> condition)
        {
            var timer = Stopwatch.StartNew();
            lock (gate)
                while (true)
                {
                    if (failure != null) throw new IOException(failure);
                    if (state != null && condition(state)) return;
                    int remaining = 15000 - (int)timer.ElapsedMilliseconds;
                    if (remaining <= 0) throw new TimeoutException("Room state synchronization timed out.");
                    Monitor.Wait(gate, remaining);
                }
        }
        public void Dispose() { client.Dispose(); }
    }
    sealed class PayloadServer : IDisposable
    {
        readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        readonly ConcurrentBag<TcpClient> clients = new ConcurrentBag<TcpClient>();
        readonly string payload;
        readonly long bytes;
        readonly CancellationToken token;
        public readonly Task Completion;
        public int Requests, Streams;
        public string Url { get { return "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/payload"; } }
        public PayloadServer(string file, long length, int requests, CancellationToken cancellation)
        {
            payload = file; bytes = length; token = cancellation; listener.Start(); Completion = Serve(requests);
        }
        async Task Serve(int requests)
        {
            var handlers = new List<Task>();
            using (token.Register(listener.Stop))
            {
                for (int i = 0; i < requests; i++)
                {
                    var socket = await listener.AcceptTcpClientAsync().ConfigureAwait(false); clients.Add(socket); handlers.Add(Reply(socket));
                }
                await Task.WhenAll(handlers).ConfigureAwait(false);
            }
        }
        async Task Reply(TcpClient socket)
        {
            using (socket)
            using (token.Register(socket.Close))
            using (var stream = socket.GetStream())
            {
                socket.NoDelay = true;
                var header = new MemoryStream(); var requestBuffer = new byte[1024]; bool complete = false;
                while (!complete)
                {
                    int count = await stream.ReadAsync(requestBuffer, 0, requestBuffer.Length, token).ConfigureAwait(false);
                    if (count == 0) throw new EndOfStreamException();
                    header.Write(requestBuffer, 0, count);
                    if (header.Length > 8192) throw new InvalidDataException("HTTP request headers are too large.");
                    complete = Encoding.ASCII.GetString(header.GetBuffer(), 0, (int)header.Length).Contains("\r\n\r\n");
                }
                string request = Encoding.ASCII.GetString(header.GetBuffer(), 0, (int)header.Length); header.Dispose();
                if (!request.StartsWith("GET /payload HTTP/", StringComparison.Ordinal)) throw new InvalidDataException("Unexpected HTTP request.");
                Interlocked.Increment(ref Requests);
                byte[] response = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: " + bytes + "\r\nContent-Type: application/octet-stream\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(response, 0, response.Length, token).ConfigureAwait(false);
                using (var input = File.OpenRead(payload))
                {
                    Interlocked.Increment(ref Streams); var buffer = new byte[65536]; int count;
                    while ((count = await input.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false)) > 0)
                        await stream.WriteAsync(buffer, 0, count, token).ConfigureAwait(false);
                }
            }
        }
        public void Dispose() { listener.Stop(); foreach (var socket in clients) socket.Close(); }
    }
    static readonly ConcurrentBag<string> ownedFiles = new ConcurrentBag<string>();
    static string Hash(string file) { using (var input = File.OpenRead(file)) using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "").ToLowerInvariant(); }
    static Scenario Verify(Scenario result, string[] files, Progress[] progress, string hash, long length)
    {
        result.outputHashes = files.Select(file => { if (new FileInfo(file).Length != length) throw new InvalidDataException("Incorrect output length."); return Hash(file); }).ToArray();
        if (result.outputHashes.Any(value => value != hash)) throw new InvalidDataException("Output SHA256 differs from the payload.");
        foreach (var value in progress) value.Verify();
        result.finalProgress = progress.Select(value => value.Received).ToArray(); result.progressCalls = progress.Select(value => value.Calls).ToArray();
        result.verified = result.exactProgress = true; return result;
    }
    static async Task<Scenario> Http(string payload, string root, string hash, long length, int count)
    {
        var progress = Enumerable.Range(0, count).Select(i => new Progress(length)).ToArray();
        var files = Enumerable.Range(0, count).Select(i => Path.Combine(root, "http-" + count + "-" + i + ".bin")).ToArray();
        foreach (string file in files) ownedFiles.Add(file);
        using (var cancel = new CancellationTokenSource(120000))
        using (var server = new PayloadServer(payload, length, count, cancel.Token))
        {
            string url = server.Url; var timer = Stopwatch.StartNew();
            await Task.WhenAll(Enumerable.Range(0, count).Select(i => Transfers.Download(url, files[i], cancel.Token, progress[i].Update))).ConfigureAwait(false);
            await server.Completion.ConfigureAwait(false); timer.Stop();
            if (server.Requests != count || server.Streams != count) throw new InvalidDataException("HTTP did not open exactly one payload stream per receiver.");
            return Verify(new Scenario { name = count == 1 ? "http_one" : "http_two", description = "Transfers.Download from a loopback TcpListener; includes connection, HTTP response and output completion", milliseconds = timer.Elapsed.TotalMilliseconds, receivers = count, bytes = length * count, httpRequests = server.Requests, payloadStreams = server.Streams }, files, progress, hash, length);
        }
    }
    static async Task<TcpClient> OpenPeer(PackPeerEndpoint source, CancellationToken token)
    {
        var socket = new TcpClient { NoDelay = true, ReceiveTimeout = 30000, SendTimeout = 30000 };
        try
        {
            using (token.Register(socket.Close))
            {
                var connecting = socket.ConnectAsync(source.host ? "127.0.0.1" : source.address, source.port);
                if (await Task.WhenAny(connecting, Task.Delay(10000, token)).ConfigureAwait(false) != connecting) { token.ThrowIfCancellationRequested(); throw new TimeoutException("Pack peer connection timed out."); }
                await connecting.ConfigureAwait(false); return socket;
            }
        }
        catch { socket.Close(); throw; }
    }
    static async Task<string> Fetch(ObservedClient receiver, Pack remote, string donorId, Progress progress, CancellationToken token, Action planRequested)
    {
        var store = new PackPeerStore(); store.Select(remote.contentKey);
        var download = new PackSwarmDownload(store, async (asset, cancellation) =>
        {
            planRequested(); var plan = await receiver.Client.PackSourcesFor(remote, asset, cancellation).ConfigureAwait(false);
            if (!plan.peers.Any(peer => !peer.host && peer.id == donorId) || !plan.peers.Any(peer => peer.host)) throw new InvalidDataException("Ready donor and host are missing from the transfer plan.");
            return plan;
        }, OpenPeer);
        string file = await download.Fetch(remote, "video", ".download-benchmark", progress.Update, token).ConfigureAwait(false);
        ownedFiles.Add(file); return file;
    }
    static async Task<Scenario> Swarm(string payload, string hash, long length, int count)
    {
        var pack = new Pack { id = "download-benchmark", title = "Download benchmark", contentKey = hash, videoHash = hash, videoBytes = length, video = payload, root = Path.GetDirectoryName(payload), lines = new List<Line> { new Line { start = 0, end = 1, role = "Voice" } } };
        var room = new RoomServer(pack, 0); var clients = new List<ObservedClient>();
        try
        {
            var host = new ObservedClient(); clients.Add(host); host.Connect(room, true, "Host");
            var donor = new ObservedClient(); clients.Add(donor); donor.Connect(room, false, "Donor");
            var receivers = new ObservedClient[count];
            for (int i = 0; i < count; i++) { receivers[i] = new ObservedClient(); clients.Add(receivers[i]); receivers[i].Connect(room, false, "Receiver " + i); }
            var remote = pack.PublicCopy(); donor.Client.SharePack(remote, pack);
            // pack_share precedes this marker on the same connection. Seeing the
            // marker proves registration without querying/warming the chunk plan.
            string marker = "benchmark-donor-" + Guid.NewGuid().ToString("N"); donor.Client.Send(new Message { kind = "chat", text = marker });
            foreach (var receiver in receivers) receiver.Wait(value => value.players.Count == count + 2 && value.chat.Any(line => line.text == marker));
            var progress = Enumerable.Range(0, count).Select(i => new Progress(length)).ToArray(); int plans = 0;
            using (var cancel = new CancellationTokenSource(120000))
            {
                var timer = Stopwatch.StartNew();
                string[] files = await Task.WhenAll(Enumerable.Range(0, count).Select(i => Fetch(receivers[i], remote, donor.Client.Id, progress[i], cancel.Token, () => Interlocked.Increment(ref plans)))).ConfigureAwait(false);
                timer.Stop();
                return Verify(new Scenario { name = count == 1 ? "swarm_one" : "swarm_two", description = "PackSwarmDownload over real room/peer TCP; includes helper discovery and initial cold chunk plan; host plus ready donor; no room-channel fallback", milliseconds = timer.Elapsed.TotalMilliseconds, receivers = count, bytes = length * count, planRequests = plans }, files, progress, hash, length);
            }
        }
        finally { room.Dispose(); foreach (var client in clients) client.Dispose(); }
    }
    public static int Main(string[] args)
    {
        if (args.Length != 2) { Console.Error.WriteLine("Usage: DownloadBenchmark.exe <payload-file> <result-json-file>"); return 2; }
        var result = new Result(); string root = null; string resultFile = Path.GetFullPath(args[1]);
        try
        {
            int workers, io; ThreadPool.GetMinThreads(out workers, out io); result.minimumWorkerThreads = Math.Max(workers, 32);
            if (!ThreadPool.SetMinThreads(result.minimumWorkerThreads, io)) throw new InvalidOperationException("Cannot configure the shared-process benchmark worker pool.");
            Paths.Prepare(); root = Path.Combine(Paths.Cache, "download-benchmark-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            string payload = Path.GetFullPath(args[0]); result.bytes = new FileInfo(payload).Length;
            if (result.bytes <= 0 || result.bytes > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("Payload must contain 1 byte through 2 GiB.");
            result.sha256 = Hash(payload); var assembly = typeof(RoomServer).Assembly;
            result.assemblyVersion = assembly.GetName().Version.ToString(); result.assemblyBuild = assembly.ManifestModule.ModuleVersionId.ToString("N");
            result.scenarios.Add(Http(payload, root, result.sha256, result.bytes, 1).GetAwaiter().GetResult());
            result.scenarios.Add(Http(payload, root, result.sha256, result.bytes, 2).GetAwaiter().GetResult());
            result.scenarios.Add(Swarm(payload, result.sha256, result.bytes, 1).GetAwaiter().GetResult());
            result.scenarios.Add(Swarm(payload, result.sha256, result.bytes, 2).GetAwaiter().GetResult());
            result.passed = true;
        }
        catch (Exception error) { result.error = error.ToString(); }
        finally
        {
            foreach (string file in ownedFiles) try { File.Delete(file); } catch (IOException) { }
            if (root != null) try { Directory.Delete(root, false); } catch (IOException) { }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(resultFile)); File.WriteAllText(resultFile, Json.Write(result));
        return result.passed ? 0 : 1;
    }
}

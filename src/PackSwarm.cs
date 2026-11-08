using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace BrokenPhone
{
    public sealed class PackPeerEndpoint
    {
        public string id, address, token;
        public int port;
        public ulong steamId;
        public bool host;
    }
    public sealed class PackTransferPlan
    {
        public string key, asset, hash;
        public long bytes;
        public string[] chunks;
        public PackPeerEndpoint[] peers;
    }
    public static class PackTransferFiles
    {
        public const int ChunkSize = 256 * 1024;
        public static PackSourceFile Expected(Pack pack, string asset)
        {
            int index;
            if (PackSources.Native(pack))
            {
                if (asset != null && asset.StartsWith("packfile:") && Int32.TryParse(asset.Substring(9), out index) && index >= 0 && index < pack.sourceFiles.Count) return pack.sourceFiles[index];
            }
            else
            {
                if (asset == "video") return new PackSourceFile { hash = pack.videoHash, bytes = pack.videoBytes };
                if (asset == "background" && pack.backgroundBytes > 0) return new PackSourceFile { hash = pack.backgroundHash, bytes = pack.backgroundBytes };
            }
            throw new InvalidDataException("Этот файл не входит в выбранный пак.");
        }
        public static string Local(Pack pack, string asset)
        {
            var file = Expected(pack, asset);
            return PackSources.Native(pack) ? PackSources.SafeFile(pack.sourceRoot, file.path) : asset == "video" ? pack.video : pack.background;
        }
        public static string Hash(byte[] bytes) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
        public static bool IsHash(string value) { return value != null && value.Length == 64 && value.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f'); }
        public static string[] Chunks(string path)
        {
            var hashes = new List<string>();
            using (var input = File.OpenRead(path))
            using (var sha = SHA256.Create())
            {
                var bytes = new byte[(int)Math.Min(ChunkSize, input.Length)];
                while (input.Position < input.Length)
                {
                    int length = (int)Math.Min(bytes.Length, input.Length - input.Position), read = 0;
                    while (read < length) { int count = input.Read(bytes, read, length - read); if (count == 0) throw new EndOfStreamException(); read += count; }
                    hashes.Add(BitConverter.ToString(sha.ComputeHash(bytes, 0, length)).Replace("-", "").ToLowerInvariant());
                }
            }
            return hashes.ToArray();
        }
    }
    public sealed class PackPeerStore
    {
        sealed class SharedFile { public string Path, Hash; public long Bytes; public bool Complete; public PackTransferPlan Plan; public readonly HashSet<int> Chunks = new HashSet<int>(); }
        readonly object gate = new object();
        readonly Dictionary<string, SharedFile> files = new Dictionary<string, SharedFile>();
        string key;
        public event Action Available;
        public long SentBytes;
        public void Select(string selected) { lock (gate) { if (key == selected) return; key = selected; files.Clear(); } }
        public void Share(Pack remote, Pack local)
        {
            if (remote.contentKey != local.contentKey) throw new InvalidDataException("Нельзя раздавать другую версию пака.");
            var assets = PackSources.Native(remote) ? Enumerable.Range(0, remote.sourceFiles.Count).Select(i => "packfile:" + i) : remote.backgroundBytes > 0 ? new[] { "video", "background" } : new[] { "video" };
            var complete = new Dictionary<string, SharedFile>();
            foreach (string asset in assets)
            {
                var expected = PackTransferFiles.Expected(remote, asset); string path;
                if (PackSources.Native(remote)) path = PackSources.SafeFile(local.sourceRoot, expected.path);
                else path = asset == "video" ? local.video : local.background;
                if (PackSources.Matches(path, expected, CancellationToken.None)) complete[asset] = new SharedFile { Path = path, Hash = expected.hash, Bytes = expected.bytes, Complete = true };
            }
            lock (gate) { if (key != remote.contentKey) return; foreach (var file in complete) files[file.Key] = file.Value; }
            if (complete.Count > 0) Notify();
        }
        public void Begin(string selected, string asset, string path, long bytes)
        {
            lock (gate) { if (key == selected) files[asset] = new SharedFile { Path = path, Bytes = bytes }; }
        }
        public void Verified(string selected, string asset, string path, int chunk)
        {
            bool first = false;
            lock (gate) { SharedFile file; if (key != selected || !files.TryGetValue(asset, out file) || file.Path != path) return; first = file.Chunks.Count == 0; file.Chunks.Add(chunk); }
            if (first) Notify();
        }
        public void Remove(string selected, string asset, string path) { lock (gate) { SharedFile file; if (key == selected && files.TryGetValue(asset, out file) && file.Path == path) files.Remove(asset); } }
        public PackTransferPlan Plan(string selected, string asset)
        {
            SharedFile file;
            lock (gate)
            {
                if (key != selected || !files.TryGetValue(asset ?? "", out file) || !file.Complete) throw new IOException("Файл выбранного пака пока недоступен.");
                if (file.Plan != null) return file.Plan;
            }
            if (!PackSources.Matches(file.Path, new PackSourceFile { hash = file.Hash, bytes = file.Bytes }, CancellationToken.None)) throw new IOException("Файл выбранного пака изменился.");
            var plan = new PackTransferPlan { key = selected, asset = asset, bytes = file.Bytes, hash = file.Hash, chunks = PackTransferFiles.Chunks(file.Path) };
            lock (gate)
            {
                SharedFile current;
                if (key != selected || !files.TryGetValue(asset, out current) || current != file) throw new IOException("Выбранный пак изменился.");
                file.Plan = plan; return plan;
            }
        }
        void Notify() { var available = Available; if (available != null) available(); }
        public byte[] Read(string selected, string asset, int chunk)
        {
            lock (gate)
            {
                SharedFile file;
                if (key != selected || !files.TryGetValue(asset ?? "", out file) || chunk < 0 || (long)chunk * PackTransferFiles.ChunkSize >= file.Bytes || !file.Complete && !file.Chunks.Contains(chunk)) throw new IOException("Часть пака пока недоступна.");
                using (var input = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    input.Position = (long)chunk * PackTransferFiles.ChunkSize;
                    var data = new byte[(int)Math.Min(PackTransferFiles.ChunkSize, file.Bytes - input.Position)]; int read = 0;
                    while (read < data.Length) { int count = input.Read(data, read, data.Length - read); if (count == 0) throw new EndOfStreamException(); read += count; }
                    Interlocked.Add(ref SentBytes, data.Length); return data;
                }
            }
        }
    }
    public sealed class PackPeerServer : IDisposable
    {
        readonly TcpListener listener = new TcpListener(IPAddress.Any, 0);
        readonly ConcurrentDictionary<TcpClient, bool> sockets = new ConcurrentDictionary<TcpClient, bool>();
        readonly PackPeerStore store;
        volatile bool disposed;
        public readonly string Token = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        public int Port { get { return ((IPEndPoint)listener.LocalEndpoint).Port; } }
        public PackPeerServer(PackPeerStore content)
        {
            store = content; listener.Start(32); Task.Run((Action)Accept);
        }
        void Accept()
        {
            while (!disposed)
                try
                {
                    var socket = listener.AcceptTcpClient(); if (sockets.Count >= 32) { socket.Close(); continue; }
                    sockets[socket] = true; Task.Run(() => Serve(socket));
                }
                catch (SocketException) { if (disposed) return; }
                catch (ObjectDisposedException) { return; }
        }
        void Serve(TcpClient socket)
        {
            try
            {
                socket.ReceiveTimeout = 15000; socket.SendTimeout = 10000; socket.NoDelay = true;
                using (socket) using (var stream = socket.GetStream())
                    while (!disposed)
                    {
                        var request = Wire.Read(stream);
                        if ((request.kind != "pack_chunk" && request.kind != "pack_plan") || request.secret != Token) return;
                        Message reply;
                        try { reply = request.kind == "pack_plan" ? new Message { kind = "pack_plan", packPlan = store.Plan(request.code, request.asset) } : new Message { kind = "pack_chunk", data = Convert.ToBase64String(store.Read(request.code, request.asset, request.index)) }; }
                        catch (IOException error) { reply = new Message { kind = request.kind, text = error.Message }; }
                        Wire.Send(stream, socket, reply);
                    }
            }
            catch (Exception) { }
            finally { bool ignored; sockets.TryRemove(socket, out ignored); socket.Close(); }
        }
        public void Dispose() { disposed = true; listener.Stop(); foreach (var socket in sockets.Keys) socket.Close(); }
    }
    public sealed class PackSwarmDownload
    {
        readonly PackPeerStore store;
        readonly Func<string, CancellationToken, Task<PackTransferPlan>> plan;
        readonly Func<PackPeerEndpoint, CancellationToken, Task<TcpClient>> connect;
        readonly Func<PackPeerEndpoint, string, int, CancellationToken, Task<byte[]>> readChunk;
        public PackSwarmDownload(PackPeerStore content, Func<string, CancellationToken, Task<PackTransferPlan>> sources, Func<PackPeerEndpoint, CancellationToken, Task<TcpClient>> open, Func<PackPeerEndpoint, string, int, CancellationToken, Task<byte[]>> read = null) { store = content; plan = sources; connect = open; readChunk = read; }
        public async Task<string> Fetch(Pack remote, string asset, string extension, Action<long, long> progress, CancellationToken token)
        {
            var expected = PackTransferFiles.Expected(remote, asset); var current = await plan(asset, token).ConfigureAwait(false);
            Validate(current, remote, asset, expected);
            string path = Path.Combine(Paths.Cache, "swarm-" + Guid.NewGuid().ToString("N") + extension);
            object fileGate = new object(), planGate = new object(); long received = 0, refreshAt = Playback.Now; int next = -1;
            using (var cancel = CancellationTokenSource.CreateLinkedTokenSource(token))
            using (var refreshGate = new SemaphoreSlim(1, 1))
            {
                try
                {
                    using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite))
                    {
                        output.SetLength(expected.bytes); store.Begin(remote.contentKey, asset, path, expected.bytes);
                        Func<int, Task> worker = async workerIndex =>
                        {
                            var connections = new Dictionary<string, TcpClient>();
                            var failed = new HashSet<string>();
                            try
                            {
                                int index;
                                while ((index = Interlocked.Increment(ref next)) < current.chunks.Length)
                                {
                                    cancel.Token.ThrowIfCancellationRequested();
                                    if (Playback.Now - Interlocked.Read(ref refreshAt) > 1000 && await refreshGate.WaitAsync(0, cancel.Token).ConfigureAwait(false))
                                    {
                                        try { var updated = await plan(asset, cancel.Token).ConfigureAwait(false); Validate(updated, remote, asset, expected); lock (planGate) current = updated; Interlocked.Exchange(ref refreshAt, Playback.Now); }
                                        finally { refreshGate.Release(); }
                                    }
                                    PackTransferPlan snapshot; lock (planGate) snapshot = current;
                                    var helpers = snapshot.peers.Where(p => !p.host).ToArray();
                                    var candidates = helpers.Skip(index % Math.Max(1, helpers.Length)).Concat(helpers.Take(index % Math.Max(1, helpers.Length))).Concat(snapshot.peers.Where(p => p.host)).Where(p => !failed.Contains(p.id)).ToArray();
                                    byte[] data = null;
                                    foreach (var source in candidates)
                                    {
                                        cancel.Token.ThrowIfCancellationRequested(); TcpClient socket = null;
                                        try
                                        {
                                            byte[] bytes;
                                            if (readChunk != null) bytes = await readChunk(source, asset, index, cancel.Token).ConfigureAwait(false);
                                            else
                                            {
                                                if (!connections.TryGetValue(source.id, out socket)) { socket = await connect(source, cancel.Token).ConfigureAwait(false); connections[source.id] = socket; }
                                                using (cancel.Token.Register(socket.Close))
                                                {
                                                    var stream = socket.GetStream(); Wire.Send(stream, socket, new Message { kind = "pack_chunk", secret = source.token, code = remote.contentKey, asset = asset, index = index });
                                                    var reply = Wire.Read(stream); if (reply.kind != "pack_chunk") throw new IOException("Неверный ответ участника."); if (reply.data == null) continue;
                                                    bytes = Convert.FromBase64String(reply.data);
                                                }
                                            }
                                            int size = (int)Math.Min(PackTransferFiles.ChunkSize, expected.bytes - (long)index * PackTransferFiles.ChunkSize);
                                            if (bytes == null || bytes.Length != size || PackTransferFiles.Hash(bytes) != snapshot.chunks[index]) throw new InvalidDataException("Повреждённая часть пака.");
                                            data = bytes; break;
                                        }
                                        catch (Exception) { if (socket != null) socket.Close(); connections.Remove(source.id); failed.Add(source.id); cancel.Token.ThrowIfCancellationRequested(); }
                                    }
                                    if (data == null) throw new IOException("Не удалось получить часть пака. Проверь соединение с участниками.");
                                    lock (fileGate)
                                    {
                                        output.Position = (long)index * PackTransferFiles.ChunkSize; output.Write(data, 0, data.Length); output.Flush();
                                        store.Verified(remote.contentKey, asset, path, index); received += data.Length; if (progress != null) progress(received, expected.bytes);
                                    }
                                }
                            }
                            catch { cancel.Cancel(); throw; }
                            finally { foreach (var socket in connections.Values) socket.Close(); }
                        };
                        await Task.WhenAll(Enumerable.Range(0, Math.Min(4, Math.Max(1, current.chunks.Length))).Select(i => Task.Run(() => worker(i), cancel.Token))).ConfigureAwait(false);
                    }
                    token.ThrowIfCancellationRequested();
                    if (PackContent.FileHash(path, token) != expected.hash) throw new InvalidDataException("Не совпала контрольная сумма собранного файла пака.");
                    return path;
                }
                catch { store.Remove(remote.contentKey, asset, path); if (File.Exists(path)) File.Delete(path); throw; }
            }
        }
        static void Validate(PackTransferPlan plan, Pack remote, string asset, PackSourceFile file)
        {
            if (plan == null || plan.key != remote.contentKey || plan.asset != asset || plan.hash != file.hash || plan.bytes != file.bytes || plan.chunks == null || file.bytes < 0 || file.bytes > 2L * 1024 * 1024 * 1024 || plan.chunks.Length != (file.bytes + PackTransferFiles.ChunkSize - 1) / PackTransferFiles.ChunkSize || plan.chunks.Any(h => !PackTransferFiles.IsHash(h)) || plan.peers == null || plan.peers.Length == 0 || plan.peers.Length > 8 || plan.peers.Any(p => p == null || String.IsNullOrEmpty(p.id) || !PackTransferFiles.IsHash(p.token) || p.port <= 0 || p.port > 65535)) throw new InvalidDataException("Некорректный план загрузки пака.");
        }
    }
}

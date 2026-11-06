using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BrokenPhone
{
    public static class Wire
    {
        public static Message Read(NetworkStream stream)
        {
            var header = ReadBytes(stream, 4);
            int size = BitConverter.ToInt32(header, 0);
            if (size < 2 || size > 20 * 1024 * 1024) throw new InvalidDataException("Недопустимый размер сообщения.");
            var message = Json.Read<Message>(Encoding.UTF8.GetString(ReadBytes(stream, size))); message.wireBytes = size; return message;
        }
        static byte[] ReadBytes(Stream stream, int size)
        {
            var bytes = new byte[size]; int offset = 0;
            while (offset < size) { int read = stream.Read(bytes, offset, size - offset); if (read == 0) throw new EndOfStreamException(); offset += read; }
            return bytes;
        }
        public static void Send(NetworkStream stream, object gate, Message message)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(Json.Write(message));
            lock (gate) { stream.Write(BitConverter.GetBytes(bytes.Length), 0, 4); stream.Write(bytes, 0, bytes.Length); }
        }
    }

    public sealed class RoomServer : IDisposable
    {
        sealed class Peer
        {
            public int PackRevision = -1;
            public string Id; public TcpClient Socket; public NetworkStream Stream; public object SendGate = new object(); public readonly ConcurrentDictionary<string, CancellationTokenSource> Transfers = new ConcurrentDictionary<string, CancellationTokenSource>();
            public void Send(Message message) { Wire.Send(Stream, SendGate, message); }
        }
        readonly TcpListener listener;
        readonly object gate = new object();
        int packRevision;
        readonly List<Peer> peers = new List<Peer>();
        readonly Dictionary<int, string> takes = new Dictionary<int, string>();
        readonly RoomState state = new RoomState();
        Pack pack;
        volatile bool disposed;
        string sessionFolder, resultFile;
        public readonly string Secret = Guid.NewGuid().ToString("N");
        public int Port { get { return ((IPEndPoint)listener.LocalEndpoint).Port; } }
        public string Code { get { return state.code; } }
        public RoomServer(Pack initialPack, int port)
        {
            pack = initialPack; state.pack = pack.PublicCopy(); state.code = Guid.NewGuid().ToString("N").Substring(0, 6).ToUpperInvariant();
            RoleCasting.NewPack(state);
            listener = new TcpListener(IPAddress.Any, port); listener.Start(16);
            Task.Run((Action)AcceptLoop);
        }
        public static string Address()
        {
            return Connection.Addresses().First().Address;
        }
        void AcceptLoop()
        {
            while (!disposed)
                try { var socket = listener.AcceptTcpClient(); Task.Run(() => Handle(socket)); }
                catch (SocketException) { if (!disposed) Thread.Sleep(100); }
                catch (ObjectDisposedException) { break; }
        }
        void Handle(TcpClient socket)
        {
            var peer = new Peer { Socket = socket, Stream = socket.GetStream(), Id = Guid.NewGuid().ToString("N") };
            socket.ReceiveTimeout = 15000; socket.SendTimeout = 15000; socket.NoDelay = true;
            bool joined = false;
            try
            {
                var hello = Wire.Read(peer.Stream);
                lock (gate)
                {
                    if (hello.kind != "join" || !String.Equals(hello.code, state.code, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Неверный код комнаты.");
                    if (hello.protocol != Playback.Protocol) throw new InvalidOperationException("Версии игры различаются. Установите одинаковую версию игры на всех компьютерах.");
                    if (state.phase != "lobby" || state.players.Count >= 8) throw new InvalidOperationException("Комната заполнена или раунд уже начался.");
                    bool isHost = hello.secret == Secret && IPAddress.IsLoopback(((IPEndPoint)socket.Client.RemoteEndPoint).Address);
                    if (!isHost && !state.players.Any(p => p.host)) throw new InvalidOperationException("Ведущий ещё не подключился.");
                    if (isHost && state.players.Any(p => p.host)) throw new InvalidOperationException("Ведущий уже подключён.");
                    peers.Add(peer); joined = true;
                    state.players.Add(new Player { id = peer.Id, owner = peer.Id, steamId = hello.steamId, name = Clean(hello.text, 64, "Игрок"), host = isHost, ready = false, color = state.players.Count % 8 });
                    RoleCasting.Refresh(state);
                    peer.Send(new Message { kind = "welcome", id = peer.Id });
                    Chat("Студия", state.players.Last().name + " в гримёрке"); Broadcast();
                }
                socket.ReceiveTimeout = 0;
                while (!disposed) Process(peer, Wire.Read(peer.Stream));
            }
            catch (Exception error)
            {
                if (!disposed && !(error is EndOfStreamException) && !(error is IOException))
                    try { peer.Send(new Message { kind = "error", text = error.Message }); } catch { }
            }
            finally
            {
                lock (gate)
                {
                    if (joined)
                    {
                        bool hostLeft = state.players.Any(p => p.id == peer.Id && p.host);
                        peers.Remove(peer); state.players.RemoveAll(p => p.owner == peer.Id);
                        RoleCasting.Refresh(state);
                        if (state.phase != "lobby") { state.phase = "lobby"; state.round = Guid.NewGuid().ToString("N"); RoleCasting.NewPack(state); Chat("Студия", "Игрок отключился. Раунд остановлен, записи сохранены."); }
                        if (hostLeft) { foreach (var other in peers.ToArray()) other.Socket.Close(); }
                        else Broadcast();
                    }
                }
                socket.Close();
            }
        }
        static string Clean(string text, int max, string fallback)
        {
            text = new string((text ?? "").Where(c => !Char.IsControl(c)).ToArray()).Trim();
            return text.Length == 0 ? fallback : text.Substring(0, Math.Min(text.Length, max));
        }
        void Chat(string name, string text)
        {
            state.chat.Add(new ChatLine { name = name, text = text, time = DateTime.Now.ToString("HH:mm") });
            if (state.chat.Count > 100) state.chat.RemoveAt(0);
        }
        void Broadcast()
        {
            state.serverTime = Playback.Now;
            foreach (var peer in peers.ToArray())
                try { peer.Send(new Message { kind = "state", state = state.ForPacket(peer.PackRevision != packRevision) }); peer.PackRevision = packRevision; } catch { peer.Socket.Close(); }
        }
        void Host(Player player) { if (!player.host) throw new InvalidOperationException("Это действие доступно ведущему."); }
        void Lobby() { if (state.phase != "lobby") throw new InvalidOperationException("Сначала вернитесь в лобби."); }
        void Casting() { if (state.phase != "casting") throw new InvalidOperationException("Выбор ролей начинается после «Начать игру»."); }
        void CurrentCasting(Message message)
        {
            Casting();
            if (message.round != state.round || message.index != state.roleVersion) throw new InvalidOperationException("Роли изменились. Проверь распределение и нажми «Начать озвучку» ещё раз.");
        }
        void Active(Peer peer, Message message)
        {
            if (state.phase != "recording" || state.round != message.round || state.turn != message.index || state.Active == null || state.Active.owner != peer.Id)
                throw new InvalidOperationException("Сейчас очередь другого игрока.");
        }
        void Process(Peer peer, Message message)
        {
            try
            {
                lock (gate)
                {
                    var player = state.players.First(p => p.id == peer.Id);
                    switch (message.kind)
                    {
                        case "asset_cancel": CancellationTokenSource transfer; if (peer.Transfers.TryGetValue(message.id ?? "", out transfer)) transfer.Cancel(); return;
                        case "ping": peer.Send(new Message { kind = "pong", clientTime = message.clientTime, serverTime = Playback.Now }); return;
                        case "chat": Chat(player.name, Clean(message.text, 240, "…")); break;
                        case "rename": Lobby(); player.name = Clean(message.text, 64, "Игрок"); break;
                        case "ready": Lobby(); player.ready = message.flag; break;
                        case "role_mode":
                            Host(player); Casting();
                            if (message.text != "auto") throw new InvalidOperationException("Выбор ролей уже доступен каждому игроку.");
                            RoleCasting.Automatic(state);
                            Chat("Студия", "Роли распределены автоматически по количеству реплик. Можно изменить свой выбор."); break;
                        case "role_select":
                            Casting();
                            var selectedRole = state.roles.FirstOrDefault(role => role.role == message.id);
                            if (selectedRole == null) throw new InvalidOperationException("Этой роли нет в выбранном паке.");
                            if (!String.IsNullOrEmpty(selectedRole.playerId) && selectedRole.playerId != player.id) throw new InvalidOperationException("Эту роль уже выбрал другой игрок.");
                            string assigned = message.flag ? player.id : "";
                            if (selectedRole.playerId == assigned) break;
                            selectedRole.playerId = assigned; state.roleMode = "manual"; RoleCasting.Refresh(state); break;
                        case "local":
                            throw new InvalidOperationException("Игра поддерживает только сетевых участников. Друг должен подключиться со своего компьютера.");
                        case "kick":
                            Host(player); Lobby(); var victim = state.players.FirstOrDefault(p => p.id == message.id && !p.host);
                            if (victim == null) return;
                            var connection = peers.FirstOrDefault(p => p.Id == victim.owner);
                            state.players.RemoveAll(p => p.id == victim.id);
                            RoleCasting.Refresh(state);
                            if (connection != null) { connection.Send(new Message { kind = "kicked", text = "Ведущий исключил вас из комнаты." }); connection.Socket.Close(); }
                            break;
                        case "pack":
                            Host(player); Lobby();
                            if (message.pack == null || !File.Exists(message.pack.video)) throw new InvalidDataException("Файл выбранного пака недоступен.");
                            pack = PackLibrary.Load(String.IsNullOrEmpty(message.pack.root) ? Path.GetDirectoryName(message.pack.video) : message.pack.root, message.pack.workshopId); pack.preview = message.pack.preview; state.pack = pack.PublicCopy(); packRevision++;
                            RoleCasting.NewPack(state);
                            Chat("Студия", "Выбран пак «" + pack.title + "»"); break;
                        case "start":
                            if (!pack.Selected || !File.Exists(pack.video)) throw new InvalidOperationException("Пак не выбран. Сначала выбери пак в мастерской.");
                            Host(player); Lobby(); if (state.players.Count < 2) throw new InvalidOperationException("Нужны хотя бы два игрока.");
                            if (state.roles.Count < state.players.Count) throw new InvalidOperationException("В паке меньше ролей, чем игроков. Выбери другой пак: каждому участнику нужна своя роль.");
                            if (state.players.Any(p => !p.ready)) throw new InvalidOperationException("Дождитесь готовности всех игроков.");
                            state.turn = 0; state.heard = true; state.performing = false; state.round = Guid.NewGuid().ToString("N"); takes.Clear(); resultFile = null;
                            state.resultReady = new string[0]; state.playbackVersion = 0; state.playing = false; state.playbackPosition = 0; state.resultDuration = 0;
                            state.order = new string[0]; state.phase = "casting"; RoleCasting.NewPack(state);
                            Chat("Студия", "Выбирайте персонажей. Когда все роли распределены, ведущий запускает озвучку."); break;
                        case "cast_start":
                            Host(player); CurrentCasting(message);
                            if (!RoleCasting.Complete(state)) throw new InvalidOperationException("Распределите все роли: каждому игроку нужна хотя бы одна роль.");
                            state.order = RoleCasting.Order(state);
                            sessionFolder = Path.Combine(Paths.Sessions, state.round); Directory.CreateDirectory(sessionFolder);
                            File.WriteAllText(Path.Combine(sessionFolder, "pack.json"), Json.Write(pack));
                            File.WriteAllText(Path.Combine(sessionFolder, "roles.json"), Json.Write(new { mode = state.roleMode, roles = state.roles, players = state.players.Select(member => new { member.id, member.name }).ToArray() }));
                            foreach (var member in state.players) { member.prepared = false; member.downloadProgress = 0; member.loadStatus = "Ожидаем загрузку"; }
                            state.phase = "loading"; break;
                        case "pack_progress":
                        case "pack_ready":
                            if (state.phase != "loading" || message.round != state.round || message.id != pack.contentKey) return;
                            if (message.kind == "pack_ready") { player.prepared = true; player.downloadProgress = 1; player.loadStatus = "Готов"; }
                            else if (!Double.IsNaN(message.position) && !Double.IsInfinity(message.position)) { player.downloadProgress = Math.Max(0, Math.Min(1, message.position)); player.loadStatus = Clean(message.text, 120, "Загрузка"); }
                            if (state.players.All(p => p.prepared)) { state.phase = "recording"; Chat("Студия", "Пак готов у всех. Начинаем озвучку!"); }
                            break;
                        case "pack_failed":
                            if (state.phase != "loading" || message.round != state.round) return;
                            state.phase = "lobby"; RoleCasting.NewPack(state);
                            Chat("Студия", player.name + ": не удалось подготовить пак. " + Clean(message.text, 160, "Попробуйте ещё раз.")); break;
                        case "heard": Active(peer, message); state.heard = true; break;
                        case "recording": Active(peer, message); state.performing = message.flag; break;
                        case "take":
                            Active(peer, message);
                            byte[] bytes = Convert.FromBase64String(message.data ?? "");
                            Audio.Validate(bytes, pack.lines[state.turn].end - pack.lines[state.turn].start);
                            string takePath = Path.Combine(sessionFolder, "take-" + state.turn + ".wav"); File.WriteAllBytes(takePath, bytes); takes[state.turn] = takePath;
                            state.turn++; state.heard = false; state.performing = false;
                            if (state.turn == state.order.Length)
                            {
                                state.phase = "rendering"; string round = state.round;
                                var renderPack = Json.Read<Pack>(Json.Write(pack)); var renderTakes = Enumerable.Range(0, state.order.Length).Select(i => takes[i]).ToArray(); string output = Path.Combine(sessionFolder, "result.mp4");
                                Task.Run(() => FinishRound(round, renderPack, renderTakes, output));
                            }
                            break;
                        case "reset": Host(player); state.phase = "lobby"; state.performing = false; state.round = Guid.NewGuid().ToString("N"); RoleCasting.NewPack(state); break;
                        case "result_ready":
                            if (state.phase != "result" || message.round != state.round) throw new InvalidOperationException("Этот просмотр уже завершён.");
                            if (!state.resultReady.Contains(peer.Id)) state.resultReady = state.resultReady.Concat(new[] { peer.Id }).ToArray();
                            break;
                        case "playback":
                            Host(player); if (state.phase != "result" || message.round != state.round || !state.resultReady.Contains(player.id)) throw new InvalidOperationException("Дождись загрузки видео у ведущего.");
                            if (state.playbackVersion == 0 && message.text != "start") throw new InvalidOperationException("Ведущий должен запустить просмотр.");
                            long at = Playback.Now + 350; double position = Playback.Position(state, at);
                            switch (message.text)
                            {
                                case "start": if (state.playbackVersion != 0) return; position = 0; state.playing = true; at += 350; break;
                                case "pause": state.playing = false; break;
                                case "resume": state.playing = true; break;
                                case "restart": position = 0; state.playing = true; at += 400; break;
                                case "seek": if (Double.IsNaN(message.position) || Double.IsInfinity(message.position)) throw new InvalidDataException("Неверная позиция."); position = Math.Max(0, Math.Min(state.resultDuration, message.position)); break;
                                case "end": if (message.index != state.playbackVersion) return; position = state.resultDuration; state.playing = false; at = Playback.Now; break;
                                default: throw new InvalidDataException("Неизвестное управление просмотром.");
                            }
                            state.playbackPosition = position; state.playbackAt = at; state.playbackVersion++; break;
                        case "asset":
                            if ((message.asset ?? "").StartsWith("original:"))
                            {
                                int line; if (!Int32.TryParse(message.asset.Substring(9), out line) || state.phase != "recording" || line < state.turn || line > RoleCasting.BlockEnd(state) || state.Active.owner != peer.Id) throw new InvalidOperationException("Оригинал этой реплики недоступен.");
                                var originalPack = pack; string originalPath = Path.Combine(sessionFolder, "original-" + line + "-" + Guid.NewGuid().ToString("N") + ".wav");
                                Task.Run(() => { try { Audio.Original(originalPack, line, originalPath); Transfer(peer, message, originalPath); } catch (Exception error) { try { peer.Send(new Message { kind = "asset_error", id = message.id, text = error.Message }); } catch { } } }); return;
                            }
                            try { string path = ResolveAsset(peer, message); QueueTransfer(peer, message, path); }
                            catch (Exception error) { peer.Send(new Message { kind = "asset_error", id = message.id, text = error.Message }); }
                            return;
                        default: throw new InvalidDataException("Неизвестная команда.");
                    }
                    Broadcast();
                }
            }
            catch (Exception error) { peer.Send(new Message { kind = message.kind == "asset" ? "asset_error" : "error", id = message.id, text = error.Message }); }
        }
        string ResolveAsset(Peer peer, Message message)
        {
            int sourceIndex;
            if ((message.asset ?? "").StartsWith("packfile:") && Int32.TryParse(message.asset.Substring(9), out sourceIndex))
            {
                if (message.code != pack.contentKey || sourceIndex < 0 || sourceIndex >= pack.sourceFiles.Count || String.IsNullOrEmpty(pack.sourceRoot)) throw new InvalidDataException("Этот файл пака недоступен.");
                return PackSources.SafeFile(pack.sourceRoot, pack.sourceFiles[sourceIndex].path);
            }
            if (message.asset == "video") return pack.video;
            if (message.asset == "background" && !String.IsNullOrEmpty(pack.background)) return pack.background;
            if (message.asset == "result" && state.phase == "result" && File.Exists(resultFile)) return resultFile;
            if (state.phase == "result" && (message.asset == "result_voices" || message.asset == "result_movie")) return Path.Combine(sessionFolder, message.asset == "result_voices" ? "result-voices.m4a" : "result-movie.m4a");
            int index;
            if (message.asset != null && message.asset.StartsWith("take:") && Int32.TryParse(message.asset.Substring(5), out index) && takes.ContainsKey(index))
            {
                if (state.phase == "result" || (state.phase == "recording" && state.Active.owner == peer.Id && index == state.turn - 1)) return takes[index];
            }
            throw new InvalidOperationException("Эта запись сейчас недоступна.");
        }
        void QueueTransfer(Peer peer, Message request, string path)
        {
            var cancellation = new CancellationTokenSource(); peer.Transfers[request.id] = cancellation; Task.Run(() => Transfer(peer, request, path, cancellation));
        }
        void Transfer(Peer peer, Message request, string path, CancellationTokenSource cancellation = null)
        {
            try
            {
                using (var stream = File.OpenRead(path))
                {
                    peer.Send(new Message { kind = "asset_begin", id = request.id, text = stream.Length.ToString() });
                    var buffer = new byte[96 * 1024]; int size;
                    while ((size = stream.Read(buffer, 0, buffer.Length)) > 0 && !disposed && (cancellation == null || !cancellation.IsCancellationRequested))
                        peer.Send(new Message { kind = "asset_chunk", id = request.id, data = Convert.ToBase64String(buffer, 0, size) });
                    if (cancellation == null || !cancellation.IsCancellationRequested) peer.Send(new Message { kind = "asset_end", id = request.id });
                }
            }
            catch (Exception error) { try { peer.Send(new Message { kind = "asset_error", id = request.id, text = error.Message }); } catch { } }
            finally { CancellationTokenSource ignored; peer.Transfers.TryRemove(request.id, out ignored); }
        }
        void FinishRound(string round, Pack renderPack, string[] renderTakes, string output)
        {
            string error = null;
            double duration = 0;
            try { Audio.Render(renderPack, renderTakes, output); duration = PackLibrary.Duration(output); } catch (Exception e) { error = e.Message; }
            lock (gate)
            {
                if (disposed || state.round != round) return;
                if (error != null) { state.phase = "lobby"; RoleCasting.NewPack(state); Chat("Студия", "Не удалось собрать видео: " + error + ". Записи сохранены."); }
                else { resultFile = output; state.phase = "result"; state.resultDuration = duration; state.resultMovieKind = Audio.MovieKind(renderPack); state.resultReady = new string[0]; Chat("Студия", "Дубль смонтирован. Просмотр запускает ведущий; сохранить MP4 может каждый."); }
                Broadcast();
            }
        }
        public void Dispose()
        {
            disposed = true; listener.Stop(); lock (gate) foreach (var peer in peers.ToArray()) peer.Socket.Close();
        }
    }

    public sealed class RoomClient : IDisposable
    {
        internal int fullPackUpdates, lightStateUpdates;
        internal long stateBytes;
        Pack knownPack;
        sealed class Download
        {
            public FileStream Stream; public long Size; public string Path; public Action<long, long> Progress; public readonly object Gate = new object();
            public TaskCompletionSource<string> Completion = new TaskCompletionSource<string>();
        }
        readonly TcpClient socket = new TcpClient();
        readonly object sendGate = new object();
        readonly ConcurrentDictionary<string, Download> downloads = new ConcurrentDictionary<string, Download>();
        NetworkStream stream;
        volatile bool disposed;
        Timer clock;
        long clockOffset, bestRtt = Int64.MaxValue;
        bool clockSynced;
        public long ServerNow { get { return Playback.Now + Interlocked.Read(ref clockOffset); } }
        public string Id { get; private set; }
        public event Action<Message> Received;
        public event Action<string> Disconnected;
        public void Connect(string host, int port, string code, string name, string secret, ulong steamId = 0)
        {
            var pending = socket.ConnectAsync(host, port); if (!pending.Wait(7000)) throw new TimeoutException("Комната не отвечает. Проверьте адрес и подключение.");
            socket.NoDelay = true; socket.SendTimeout = 15000; stream = socket.GetStream();
            Send(new Message { kind = "join", code = code, text = name, secret = secret, protocol = Playback.Protocol, steamId = steamId }); Task.Run((Action)ReadLoop);
            clock = new Timer(delegate { try { if (!disposed) Send(new Message { kind = "ping", clientTime = Playback.Now }); } catch { } }, null, 200, 3000);
        }
        public void Send(Message message) { if (disposed || stream == null) throw new IOException("Нет подключения к комнате."); Wire.Send(stream, sendGate, message); }
        void ReadLoop()
        {
            string reason = "Соединение с ведущим закрыто.";
            try
            {
                while (!disposed)
                {
                    Message message = Wire.Read(stream);
                    if (message.kind == "welcome") Id = message.id;
                    if (message.state != null)
                    {
                        if (message.state.pack != null) { fullPackUpdates++; knownPack = message.state.pack; }
                        else { lightStateUpdates++; if (knownPack == null) throw new InvalidDataException("Не получены данные выбранного пака."); message.state.pack = knownPack; }
                        Interlocked.Add(ref stateBytes, message.wireBytes);
                    }
                    if (message.kind == "pong")
                    {
                        long now = Playback.Now, rtt = Math.Max(0, now - message.clientTime);
                        if (!clockSynced || rtt - bestRtt < 20) { bestRtt = Math.Min(bestRtt, rtt); Interlocked.Exchange(ref clockOffset, message.serverTime - (message.clientTime + now) / 2); clockSynced = true; }
                        continue;
                    }
                    if (message.state != null && !clockSynced) Interlocked.Exchange(ref clockOffset, message.state.serverTime - Playback.Now);
                    if (message.kind.StartsWith("asset_")) { ReceiveAsset(message); continue; }
                    if (message.kind == "kicked" || (message.kind == "error" && Id == null)) reason = message.text;
                    var handler = Received; if (handler != null) handler(message);
                }
            }
            catch (Exception error) { if (!(error is EndOfStreamException)) reason += " " + error.Message; }
            finally
            {
                foreach (var pair in downloads) { lock (pair.Value.Gate) { if (pair.Value.Stream != null) pair.Value.Stream.Dispose(); } pair.Value.Completion.TrySetException(new IOException(reason)); }
                downloads.Clear();
                if (!disposed) { var handler = Disconnected; if (handler != null) handler(reason); }
            }
        }
        void ReceiveAsset(Message message)
        {
            Download pending; if (!downloads.TryGetValue(message.id ?? "", out pending)) return;
            lock (pending.Gate)
            try
            {
                if (message.kind == "asset_error") throw new IOException(message.text);
                if (message.kind == "asset_begin")
                {
                    long size; if (pending.Stream != null || !Int64.TryParse(message.text, out size) || size < 0 || size > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("Недопустимый размер файла.");
                    pending.Size = size; pending.Stream = File.Create(pending.Path); if (pending.Progress != null) pending.Progress(0, size);
                }
                else if (message.kind == "asset_chunk")
                {
                    if (pending.Stream == null) throw new InvalidDataException("Нет начала передачи.");
                    byte[] bytes = Convert.FromBase64String(message.data); if (pending.Stream.Length + bytes.Length > pending.Size) throw new InvalidDataException("Файл превышает заявленный размер.");
                    pending.Stream.Write(bytes, 0, bytes.Length); if (pending.Progress != null) pending.Progress(pending.Stream.Length, pending.Size);
                }
                else if (message.kind == "asset_end")
                {
                    if (pending.Stream == null || pending.Stream.Length != pending.Size) throw new InvalidDataException("Файл загружен не полностью.");
                    pending.Stream.Dispose(); pending.Stream = null; Download ignored; downloads.TryRemove(message.id, out ignored); pending.Completion.TrySetResult(pending.Path);
                }
            }
            catch (Exception error)
            {
                if (pending.Stream != null) pending.Stream.Dispose(); Download ignored; downloads.TryRemove(message.id, out ignored); pending.Completion.TrySetException(error);
            }
        }
        public async Task<string> Asset(string asset, string extension, Action<long, long> progress = null, CancellationToken cancellation = default(CancellationToken), string packKey = null)
        {
            string id = Guid.NewGuid().ToString("N"); var pending = new Download { Path = Path.Combine(Paths.Cache, id + extension), Progress = progress };
            downloads[id] = pending;
            try
            {
                Send(new Message { kind = "asset", id = id, asset = asset, code = packKey });
                var timeout = Task.Delay(600000, cancellation);
                if (await Task.WhenAny(pending.Completion.Task, timeout) != pending.Completion.Task) { cancellation.ThrowIfCancellationRequested(); throw new TimeoutException("Передача файла заняла больше десяти минут."); }
                return await pending.Completion.Task;
            }
            finally { Download ignored; downloads.TryRemove(id, out ignored); lock (pending.Gate) { if (pending.Stream != null) pending.Stream.Dispose(); } if (!pending.Completion.Task.IsCompleted) try { Send(new Message { kind = "asset_cancel", id = id }); } catch (IOException) { } if (!pending.Completion.Task.IsCompleted && File.Exists(pending.Path)) try { File.Delete(pending.Path); } catch (IOException) { } }
        }
        public void Dispose() { disposed = true; if (clock != null) clock.Dispose(); socket.Close(); }
    }
}

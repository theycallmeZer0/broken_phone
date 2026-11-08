using System;
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
    public static partial class SelfTests
    {
        static void RejectVersionJoin(RoomServer server, int protocol, string version, string build)
        {
            using (var socket = new TcpClient())
            {
                socket.ReceiveTimeout = socket.SendTimeout = 4000;
                socket.Connect(IPAddress.Loopback, server.Port);
                var stream = socket.GetStream();
                Wire.Send(stream, socket, new Message { kind = "join", code = server.Code, text = "Несовместимый игрок", protocol = protocol, version = version, build = build });
                var reply = Wire.Read(stream);
                Check(reply.kind == "error" && !String.IsNullOrEmpty(reply.text), "Другая версия получила приветствие вместо отказа входа.");
                Check(stream.ReadByte() == -1, "Соединение другой версии осталось открытым после отказа.");
            }
        }
        static void RejectUnstampedServer(List<string> report)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            int states = 0, disconnects = 0;
            bool closed = false;
            var legacy = Task.Run(() =>
            {
                using (var socket = listener.AcceptTcpClient())
                {
                    socket.ReceiveTimeout = socket.SendTimeout = 8000;
                    var stream = socket.GetStream(); var hello = Wire.Read(stream);
                    Check(hello.kind == "join" && hello.version == GameVersion.Version && hello.build == GameVersion.Build, "Клиент не передал точную версию и сборку.");
                    var welcome = Encoding.UTF8.GetBytes(Json.Write(new Message { kind = "welcome", id = "legacy-host" }));
                    var state = Encoding.UTF8.GetBytes(Json.Write(new Message { kind = "state", state = new RoomState { code = "ABC123", pack = new Pack { title = "Legacy room", contentKey = "legacy" } } }));
                    // Queue both frames together so ignoring the welcome stamp
                    // would expose a real incompatible room state to the client.
                    byte[] frames = BitConverter.GetBytes(welcome.Length).Concat(welcome).Concat(BitConverter.GetBytes(state.Length)).Concat(state).ToArray();
                    stream.Write(frames, 0, frames.Length);
                    // A legacy host has no trustworthy stamp; the current client must
                    // close the connection before accepting any room state.
                    try { closed = stream.ReadByte() == -1; }
                    catch (IOException) { closed = true; }
                }
            });
            try
            {
                using (var client = new RoomClient())
                {
                    client.Received += message => { if (message.state != null) Interlocked.Increment(ref states); };
                    client.Disconnected += reason => Interlocked.Increment(ref disconnects);
                    bool refused = false;
                    try { client.Connect("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, "ABC123", "Current client", ""); }
                    catch (Exception) { refused = true; }
                    Check(refused && String.IsNullOrEmpty(client.Id) && states == 0, "Клиент принял сервер без точной версии и сборки.");
                    Check(legacy.Wait(9000) && closed, "Клиент не закрыл несовместимое начальное соединение.");
                    Check(disconnects == 0, "Неуспешный вход вызвал событие отключения уже подключённого игрока.");
                }
                report.Add("PASS: client sends the exact version/build and refuses an unstamped server before accepting room state; failed initial handshakes close without a joined-player disconnect event");
            }
            finally { listener.Stop(); }
        }
        public static int VersionRegression()
        {
            var report = new List<string>();
            try
            {
                using (var server = new RoomServer(new Pack { title = "Проверка одинаковой версии" }, 0))
                using (var host = new Inbox())
                using (var guest = new Inbox())
                {
                    host.Client.Connect("127.0.0.1", server.Port, server.Code, "Host", server.Secret);
                    host.Wait(state => state.players.Count == 1);
                    host.Client.Send(new Message { kind = "ready", flag = true });
                    host.Wait(state => state.players.Count == 1 && state.players[0].ready);
                    RejectVersionJoin(server, Playback.Protocol + 1, GameVersion.Version, GameVersion.Build);
                    RejectVersionJoin(server, Playback.Protocol, "0.0.0.0", GameVersion.Build);
                    RejectVersionJoin(server, Playback.Protocol, GameVersion.Version, new string('0', 32));
                    RejectVersionJoin(server, Playback.Protocol, null, null);
                    RejectVersionJoin(server, Playback.Protocol, GameVersion.Version, null);
                    host.Client.Send(new Message { kind = "chat", text = "version-check-barrier" });
                    host.Wait(state => state.chat.Any(line => line.text == "version-check-barrier"));
                    Check(host.State.players.Count == 1 && host.State.players[0].ready && host.State.phase == "lobby", "Отказ другой версии изменил состав комнаты или сбросил готовность ведущего.");
                    report.Add("PASS: real TCP joins with another protocol, version, build, or missing stamps are rejected before welcome and player insertion; existing lobby readiness is preserved");

                    guest.Client.Connect("127.0.0.1", server.Port, server.Code, "Same build guest", "");
                    host.Wait(state => state.players.Count == 2); guest.Wait(state => state.players.Count == 2);
                    Check(guest.Messages.Any(message => message.kind == "welcome" && message.version == GameVersion.Version && message.build == GameVersion.Build && message.protocol == Playback.Protocol), "Принятый гость не получил подтверждение точной версии и сборки сервера.");
                    Check(host.State.players.All(player => !player.ready), "Обычный вход той же сборки нарушил обновление готовности.");
                    report.Add("PASS: an ordinary client of the exact same version/build joins successfully and verifies the stamped server welcome");
                }
                RejectUnstampedServer(report);
                return 0;
            }
            catch (Exception error) { report.Add("FAIL: " + error); return 1; }
            finally { File.WriteAllLines(Path.Combine(Paths.Base, "version-test.txt"), report); }
        }
    }
}

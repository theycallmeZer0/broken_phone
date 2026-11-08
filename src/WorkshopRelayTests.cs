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
        static bool RelayFails(Action action)
        {
            try { action(); return false; }
            catch (InvalidDataException) { return true; }
            catch (IOException) { return true; }
        }
        static void RelayDisconnectRegression(Pack source, List<string> report)
        {
            var pending = PendingWorkshop.Create(new Pack { workshopId = source.workshopId, title = source.title });
            using (var server = new RoomServer(pending, 0)) using (var host = new Inbox()) using (var donor = new TcpClient())
            {
                ConnectSwarm(host, server, true); donor.ReceiveTimeout = donor.SendTimeout = 4000;
                donor.Connect(IPAddress.Loopback, server.Port); var stream = donor.GetStream();
                Wire.Send(stream, donor, new Message { kind = "join", code = server.Code, text = "Disconnecting donor", protocol = Playback.Protocol, version = GameVersion.Version, build = GameVersion.Build });
                Check(Wire.Read(stream).kind == "welcome", "Не подключился источник проверки отключения.");
                host.Wait(state => state.players.Count == 2);
                Wire.Send(stream, donor, new Message { kind = "workshop_offer", code = pending.contentKey, pack = source.PublicCopy(), packPeer = new PackPeerEndpoint { port = 12345, token = new string('a', 64) } });
                var offer = WaitWorkshopMessage(host, message => message.kind == "workshop_offer");
                var fetching = host.Client.WorkshopRelayPlan(offer.pack, offer.packPeer, "video", CancellationToken.None);
                Message forwarded;
                do { forwarded = Wire.Read(stream); } while (forwarded.kind != "workshop_relay_request");
                donor.Close();
                Check(Task.WhenAny(fetching, Task.Delay(3000)).GetAwaiter().GetResult() == fetching && RelayFails(() => fetching.GetAwaiter().GetResult()), "Отключение источника не завершило ожидающую передачу без долгого таймаута.");
                report.Add("PASS: a donor disconnect immediately fails its in-flight relay request and clears its routes instead of waiting for the transfer timeout");
            }
        }
        public static int WorkshopRelayRegression()
        {
            var report = new List<string>();
            string root = Path.Combine(Paths.Cache, "workshop-relay-test-" + Guid.NewGuid().ToString("N")), installed = null;
            Directory.CreateDirectory(root);
            try
            {
                var source = PackLibrary.Load(Path.Combine(Paths.Base, "Assets", "Demo"), "");
                source.id = "workshop-relay-" + Guid.NewGuid().ToString("N"); source.workshopId = "9911999";
                source.title = "Workshop relay " + Guid.NewGuid().ToString("N"); source.root = root; source.background = "";
                string video = Path.Combine(root, "video.mp4"); File.Copy(source.video, video); source.video = video;
                // A valid MP4 free box makes the fixture large and unique without changing its video.
                byte[] padding = new byte[2 * 1024 * 1024]; new Random(Guid.NewGuid().GetHashCode()).NextBytes(padding);
                using (var output = new FileStream(video, FileMode.Append))
                {
                    byte[] size = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(padding.Length + 8));
                    output.Write(size, 0, size.Length); output.Write(Encoding.ASCII.GetBytes("free"), 0, 4); output.Write(padding, 0, padding.Length);
                }
                PackContent.Describe(source, CancellationToken.None);
                installed = Path.Combine(Paths.Packs, "room-" + source.contentKey);
                var metadata = new Pack { workshopId = source.workshopId, title = source.title };
                var pending = PendingWorkshop.Create(metadata);
                using (var server = new RoomServer(pending, 0)) using (var host = new Inbox()) using (var donor = new Inbox())
                {
                    ConnectSwarm(host, server, true); ConnectSwarm(donor, server, false);
                    donor.Client.OfferWorkshopPack(pending.contentKey, source);
                    var offer = WaitWorkshopMessage(host, message => message.kind == "workshop_offer");
                    var inaccessible = Json.Read<PackPeerEndpoint>(Json.Write(offer.packPeer));
                    var unavailable = new TcpListener(IPAddress.Loopback, 0); unavailable.Start();
                    inaccessible.port = ((IPEndPoint)unavailable.LocalEndpoint).Port; unavailable.Stop();
                    inaccessible.address = "127.0.0.1";
                    bool fallback = false;
                    var prepared = host.Client.PrepareWorkshopOffer(offer.pack, inaccessible, progress => { if (progress.Stage.Contains("соединение комнаты")) fallback = true; }, CancellationToken.None, null).GetAwaiter().GetResult();
                    Check(fallback && prepared.contentKey == source.contentKey && PackContent.FileHash(prepared.video) == source.videoHash && server.WorkshopRelayBytes == source.videoBytes && donor.Client.PackUploadBytes == source.videoBytes && server.PackUploadBytes == 0, "Недоступный порт участника не переключился на проверенную передачу по каналу комнаты.");
                    report.Add("PASS: host with no Steam/source recovers an offered pack through the existing room connection when the donor sharing port is unreachable; all chunks and final SHA-256 match and no host pack file is used");

                    long sent = donor.Client.PackUploadBytes;
                    Check(RelayFails(() => donor.Client.WorkshopRelayPlan(offer.pack, offer.packPeer, "video", CancellationToken.None).GetAwaiter().GetResult()), "Гость запросил начальную передачу вместо ведущего.");
                    Check(RelayFails(() => host.Client.WorkshopRelayPlan(offer.pack, offer.packPeer, "take:0", CancellationToken.None).GetAwaiter().GetResult()), "Передача открыла личную запись.");
                    var wrong = offer.pack.PublicCopy(); wrong.contentKey = new string('0', 64);
                    Check(RelayFails(() => host.Client.WorkshopRelayPlan(wrong, offer.packPeer, "video", CancellationToken.None).GetAwaiter().GetResult()), "Передача открыла файлы другого предложенного пака.");
                    Check(donor.Client.PackUploadBytes == sent, "Отклонённые запросы начали передачу файлов.");
                    report.Add("PASS: room relay rejects non-host requests, private recordings, and unoffered content keys without sending donor bytes");

                    Directory.Delete(installed, true);
                    byte[] original = File.ReadAllBytes(video); byte[] changed = (byte[])original.Clone(); changed[0] ^= 1; File.WriteAllBytes(video, changed);
                    bool corrupt = RelayFails(() => host.Client.PrepareWorkshopOffer(offer.pack, inaccessible, progress => { }, CancellationToken.None, null).GetAwaiter().GetResult());
                    Check(corrupt && !Directory.Exists(installed), "Повреждённые части резервной передачи установились как готовый пак.");
                    File.WriteAllBytes(video, original);
                    report.Add("PASS: corrupted donor bytes are rejected by the relay chunk hashes and cannot create an installed pack");

                    var before = new HashSet<string>(Directory.GetFiles(Paths.Cache, "swarm-*"));
                    using (var cancel = new CancellationTokenSource())
                    {
                        bool cancelled = false;
                        try { host.Client.PrepareWorkshopOffer(offer.pack, inaccessible, progress => { if (progress.Received > 0) cancel.Cancel(); }, cancel.Token, null).GetAwaiter().GetResult(); }
                        catch (OperationCanceledException) { cancelled = true; }
                        Check(cancelled && !Directory.Exists(installed) && Directory.GetFiles(Paths.Cache, "swarm-*").All(before.Contains), "Отмена резервной передачи оставила установленный или частичный пак.");
                    }
                    host.Client.Send(new Message { kind = "workshop_select", pack = metadata });
                    host.Wait(state => state.pack.pendingDownload && state.pack.contentKey != pending.contentKey);
                    Check(RelayFails(() => host.Client.WorkshopRelayPlan(offer.pack, offer.packPeer, "video", CancellationToken.None).GetAwaiter().GetResult()), "Предложение старого выбора доступно после смены пака.");
                    report.Add("PASS: cancelling an active relay removes partial files; choosing another selection invalidates old offers");
                }
                RelayDisconnectRegression(source, report);
                return 0;
            }
            catch (Exception error) { report.Add("FAIL: " + error); return 1; }
            finally
            {
                if (installed != null && Directory.Exists(installed)) Directory.Delete(installed, true);
                if (Directory.Exists(root)) Directory.Delete(root, true);
                File.WriteAllLines(Path.Combine(Paths.Base, "workshop-relay-test.txt"), report);
            }
        }
    }
}

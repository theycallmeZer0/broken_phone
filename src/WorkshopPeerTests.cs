using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace BrokenPhone
{
    public static partial class SelfTests
    {
        public static int WorkshopPeerRegression()
        {
            var report = new List<string>();
            string root = Path.Combine(Paths.Cache, "workshop-peer-test-" + Guid.NewGuid().ToString("N")), preparedFolder = null;
            Directory.CreateDirectory(root);
            try
            {
                var source = PackLibrary.Load(Path.Combine(Paths.Base, "Assets", "Demo"), "");
                source.id = "workshop-peer-test-" + Guid.NewGuid().ToString("N"); source.workshopId = "9911888";
                source.title = "Workshop peer " + Guid.NewGuid().ToString("N"); source.root = root;
                string video = Path.Combine(root, "video.mp4"); File.Copy(source.video, video);
                using (var output = new FileStream(video, FileMode.Append)) { byte[] tag = Encoding.ASCII.GetBytes(Guid.NewGuid().ToString("N")); output.Write(tag, 0, tag.Length); }
                source.video = video; source.background = ""; PackContent.Describe(source, CancellationToken.None);
                preparedFolder = Path.Combine(Paths.Packs, "room-" + source.contentKey);
                var pending = new Pack { pendingDownload = true, id = "workshop-" + source.workshopId, workshopId = source.workshopId, title = source.title, contentKey = "workshop-" + Guid.NewGuid().ToString("N") };
                using (var server = new RoomServer(pending, 0)) using (var host = new Inbox()) using (var donor = new Inbox())
                {
                    ConnectSwarm(host, server, true); ConnectSwarm(donor, server, false);
                    donor.Client.OfferWorkshopPack(pending.contentKey, source);
                    Message offered = null, message; DateTime deadline = DateTime.UtcNow.AddSeconds(5);
                    while (offered == null && DateTime.UtcNow < deadline)
                    {
                        while (host.Messages.TryDequeue(out message)) if (message.kind == "workshop_offer") offered = message;
                        if (offered == null) Thread.Sleep(10);
                    }
                    Check(offered != null && offered.code == pending.contentKey && offered.pack.contentKey == source.contentKey && offered.packPeer.id == donor.Client.Id, "Ведущий не получил предложение пака от участника.");
                    Check(offered.packPeer.address == "127.0.0.1" && String.IsNullOrEmpty(offered.pack.root) && String.IsNullOrEmpty(offered.pack.sourceRoot), "Предложение раскрывает локальные пути или подменяет адрес участника.");
                    donor.Client.Send(new Message { kind = "chat", text = "keep-workshop-offer" });
                    donor.Wait(s => s.chat.Any(c => c.text == "keep-workshop-offer"));
                    var prepared = host.Client.PrepareWorkshopOffer(offered.pack, offered.packPeer, p => { }, CancellationToken.None, null).GetAwaiter().GetResult();
                    Check(prepared.contentKey == source.contentKey && PackContent.FileHash(prepared.video) == source.videoHash && donor.Client.PackUploadBytes == source.videoBytes && server.PackUploadBytes == 0, "Ведущий не получил выбранный пак напрямую от участника без Steam.");
                    Check(host.State.pack.pendingDownload, "Получение предложения самостоятельно заменило выбор ведущего.");
                    report.Add("PASS: a guest offers a cached Workshop pack; the authenticated offer survives subsequent lobby updates; a host with no local source prepares exact verified bytes directly from the guest without Steam or host pack traffic");
                    using (var socket = new TcpClient())
                    {
                        socket.Connect(offered.packPeer.address, offered.packPeer.port); var stream = socket.GetStream();
                        Wire.Send(stream, socket, new Message { kind = "pack_plan", secret = offered.packPeer.token, code = "obsolete-selection", asset = "video" });
                        Check(Wire.Read(stream).packPlan == null, "Устаревший ключ получил план файла.");
                        Wire.Send(stream, socket, new Message { kind = "pack_plan", secret = offered.packPeer.token, code = source.contentKey, asset = "take:0" });
                        Check(Wire.Read(stream).packPlan == null, "План раздачи открыл личную запись.");
                    }
                    using (var cancel = new CancellationTokenSource())
                    {
                        cancel.Cancel(); bool cancelled = false; long sent = donor.Client.PackUploadBytes;
                        try { host.Client.PrepareWorkshopOffer(offered.pack, offered.packPeer, p => { }, cancel.Token, null).GetAwaiter().GetResult(); }
                        catch (OperationCanceledException) { cancelled = true; }
                        Check(cancelled && donor.Client.PackUploadBytes == sent, "Отменённое предложение начало передачу файлов.");
                    }
                    report.Add("PASS: obsolete content keys and private recording plans are rejected; cancelled bootstrap starts no transfer");
                }
                return 0;
            }
            catch (Exception error) { report.Add("FAIL: " + error); return 1; }
            finally
            {
                if (preparedFolder != null && Directory.Exists(preparedFolder)) Directory.Delete(preparedFolder, true);
                if (Directory.Exists(root)) Directory.Delete(root, true);
                File.WriteAllLines(Path.Combine(Paths.Base, "workshop-peer-test.txt"), report);
            }
        }
    }
}

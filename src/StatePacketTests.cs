using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BrokenPhone
{
    public static partial class SelfTests
    {
        public static int StatePackets()
        {
            var report = new List<string>();
            try
            {
                var pack = PackLibrary.Load(Path.Combine(Paths.Base, "Assets", "Demo"), "");
                using (var server = new RoomServer(pack, 0)) using (var host = new Inbox()) using (var guest = new Inbox())
                {
                    host.Client.Connect("127.0.0.1", server.Port, server.Code, "Host", server.Secret); host.Wait(s => s.players.Count == 1);
                    guest.Client.Connect("127.0.0.1", server.Port, server.Code, "Guest", ""); host.Wait(s => s.players.Count == 2); guest.Wait(s => s.players.Count == 2);
                    int full = guest.Client.fullPackUpdates, light = guest.Client.lightStateUpdates; long bytes = guest.Client.stateBytes;
                    for (int i = 0; i < 5; i++) host.Client.Send(new Message { kind = "chat", text = "packet-check-" + i });
                    guest.Wait(s => s.chat.Any(line => line.text == "packet-check-4"));
                    Check(guest.Client.fullPackUpdates == full && guest.Client.lightStateUpdates >= light + 5, "Полные данные пака и обложка повторно передаются с каждым изменением состояния.");
                    Check(guest.State.pack.contentKey == pack.contentKey && guest.State.pack.lines.Count == pack.lines.Count, "Клиент потерял метаданные пака между короткими обновлениями.");
                    report.Add("PASS: five live room updates reuse cached pack metadata; full cover and file manifest are not retransmitted; total update bytes=" + (guest.Client.stateBytes - bytes));
                    host.Client.Send(new Message { kind = "pack", pack = pack }); guest.Wait(s => guest.Client.fullPackUpdates > full);
                    Check(guest.State.pack != null && guest.State.pack.contentKey == pack.contentKey, "Повторный выбор пака не передаёт полные метаданные.");
                    report.Add("PASS: explicit pack selection sends a fresh complete description even when the content key is unchanged");
                }
            }
            catch (Exception error) { report.Add("FAIL: " + error); }
            File.WriteAllLines(Path.Combine(Paths.Base, "state-packets-test.txt"), report); return report.Any(line => line.StartsWith("FAIL:")) ? 1 : 0;
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BrokenPhone
{
    public static partial class SelfTests
    {
        public static int SteamWorkshopIntegration(string id)
        {
            var report = new List<string>();
            try
            {
                using (var steam = new SteamBridge(true))
                using (var cancellation = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(90)))
                {
                    Check(steam.Available, steam.Status); report.Add("PASS: current logged-in Steam client attached; AppID " + steam.AppId + "; no separate login");
                    string folder = steam.DownloadWorkshopItem(id, progress => { }, cancellation.Token).GetAwaiter().GetResult();
                    var pack = PackLibrary.Import(folder, id, cancellation.Token);
                    Check(pack.DisplayLines > 0 && File.Exists(pack.video), "Загруженный пак не содержит видео и реплик.");
                    Check(steam.Available && steam.AppId == 480 && !String.IsNullOrEmpty(steam.RefreshName()), "Основная сессия Steam 480 потеряна после фоновой загрузки.");
                    report.Add("PASS: native SteamUGC download completed and imported into own Packs; item=" + id + "; lines=" + pack.DisplayLines + "; roles=" + pack.DisplayRoles);
                    report.Add("PASS: the main Steam session remains attached to AppID 480 after the Workshop worker exits");
                }
                return 0;
            }
            catch (Exception error) { report.Add("FAIL: " + error.GetBaseException().Message); return 1; }
            finally { File.WriteAllLines(Path.Combine(Paths.Base, "steam-workshop-test.txt"), report); }
        }
        public static int SteamIntegration()
        {
            var report = new List<string>(); SteamBridge active = null;
            try
            {
                Check(SteamBridge.ValidCode("A1B2C3") && SteamBridge.ValidCode("a1b2c3") && !SteamBridge.ValidCode("ABC") && !SteamBridge.ValidCode("ZZZZZZ"), "Неверная проверка кода комнаты.");
                using (var steam = new SteamBridge(true))
                {
                    active = steam; Check(steam.Available, steam.Status); report.Add("PASS: signed Valve SDK initialized with a logged-in Steam profile; App ID " + steam.AppId);
                    SteamAvatar avatar = null; var avatarDeadline = DateTime.UtcNow.AddSeconds(25);
                    while ((avatar = steam.GetAvatar(steam.UserId)) == null && DateTime.UtcNow < avatarDeadline) System.Threading.Thread.Sleep(100);
                    Check(avatar != null && avatar.Rgba.Length == avatar.Width * avatar.Height * 4 && avatar.Rgba.Any(value => value != 0), "Steam не загрузил аватарку профиля.");
                    Check(Object.ReferenceEquals(avatar, steam.GetAvatar(steam.UserId)), "Повторное чтение аватарки не использует кэш.");
                    report.Add("PASS: actual profile avatar loaded through Steam image API on App ID " + steam.AppId + "; RGBA pixels and decoded-image cache verified");
                    Check(steam.Friends().All(friend => friend.Id != 0 && !String.IsNullOrEmpty(friend.Name)), "Steam вернул повреждённый список друзей."); report.Add("PASS: online Steam friends list read through SDK; invitations remain manual");
                    var pack = PackLibrary.Load(Path.Combine(Paths.Base, "Assets", "Demo"), "");
                    using (var server = new RoomServer(pack, 0))
                    using (var host = new Inbox())
                    using (var guest = new Inbox())
                    {
                        host.Client.Connect("127.0.0.1", server.Port, server.Code, "Steam host test", server.Secret, steam.UserId); host.Wait(s => s.players.Count == 1);
                        steam.HostRoom(server.Code, server.Port).GetAwaiter().GetResult();
                        Check(steam.Hosting && steam.LobbyId != 0 && steam.OwnerId == steam.UserId && steam.Code == server.Code, "Лобби Steam не подготовлено.");
                        ulong found = 0; var lookupDeadline = DateTime.UtcNow.AddSeconds(18);
                        while (found == 0)
                        {
                            try { found = steam.FindLobbyByCode(server.Code.ToLowerInvariant()).GetAwaiter().GetResult(); }
                            catch (IOException) { if (DateTime.UtcNow >= lookupDeadline) throw; System.Threading.Thread.Sleep(400); }
                        }
                        Check(found == steam.LobbyId, "Поиск по коду нашёл другую комнату.");
                        Check(steam.CodeForLobby(found).GetAwaiter().GetResult() == server.Code, "Из приглашения Steam не получен код комнаты.");
                        report.Add("PASS: real Steam lobby published and found by six-character code with exact game/code lookup, version/build validation and worldwide search; current room stays connected during lookup");
                        int tunnelPort = steam.OpenTunnel(steam.UserId);
                        guest.Client.Connect("127.0.0.1", tunnelPort, server.Code, "Steam P2P loopback test", "");
                        host.Wait(s => s.players.Count == 2, 10); guest.Wait(s => s.players.Count == 2, 10);
                        Check(host.State.players.First(player => player.host).steamId == steam.UserId && guest.State.players.First(player => player.host).steamId == steam.UserId, "Steam ID профиля не синхронизирован у участников.");
                        guest.Client.Send(new Message { kind = "chat", text = "SDK P2P transport test" }); host.Wait(s => s.chat.Any(c => c.text == "SDK P2P transport test"));
                        report.Add("PASS: real Steam Networking Sockets reliable tunnel carries game handshake, state and chat");
                        string video = guest.Client.Asset("video", ".mp4").GetAwaiter().GetResult();
                        Check(File.ReadAllBytes(video).SequenceEqual(File.ReadAllBytes(pack.video)), "Видео повреждено при передаче через Steam.");
                        host.Client.Send(new Message { kind = "ready", flag = true }); guest.Client.Send(new Message { kind = "ready", flag = true }); host.Wait(s => s.players.All(p => p.ready));
                        StartLoadedRound(host, guest);
                        for (int i = 0; i < 4; i++)
                        {
                            var player = i % 2 == 0 ? host : guest; int turn = i; player.Wait(s => s.phase == "recording" && s.turn == turn);
                            player.Client.Send(new Message { kind = "take", round = player.State.round, index = i, data = Convert.ToBase64String(ToneWave(.6, 330 + i * 110)) }); host.Wait(s => s.turn > turn);
                        }
                        guest.Wait(s => s.phase == "result"); host.Wait(s => s.phase == "result"); string final = guest.Client.Asset("result", ".mp4").GetAwaiter().GetResult(); CheckMixedVoices(final, pack);
                        host.Client.Send(new Message { kind = "result_ready", round = host.State.round }); host.Wait(s => s.resultReady.Contains(host.Client.Id));
                        guest.Client.Send(new Message { kind = "result_ready", round = guest.State.round }); host.Wait(s => Playback.EveryoneReady(s));
                        Check(host.State.playbackVersion == 0, "Просмотр запущен автоматически."); host.Client.Send(new Message { kind = "playback", text = "start", round = host.State.round }); guest.Wait(s => s.playbackVersion == 1);
                        report.Add("PASS: source video, all four takes, assembled voiced MP4 and shared playback state transmitted through actual Steam SDK P2P");
                    }
                    steam.LeaveRoom(); Check(steam.LobbyId == 0, "Лобби Steam не закрыто.");
                    report.Add("PASS: test lobby and P2P sessions closed; no friend invitations sent; remote two-account routing still requires a second computer");
                }
                File.WriteAllLines(Path.Combine(Paths.Base, "steam-test.txt"), report); return 0;
            }
            catch (Exception e) { report.Add("FAIL: " + e); if (active != null) report.Add("Transport state at failure: " + active.TransportDetails); File.WriteAllLines(Path.Combine(Paths.Base, "steam-test.txt"), report); return 1; }
        }
    }
}

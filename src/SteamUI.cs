using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace BrokenPhone
{
    public sealed partial class GameWindow
    {
        SteamBridge steam;
        DateTime nextPersonaCheck;
        readonly System.Windows.Threading.DispatcherTimer overlayFrames = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        bool overlayFrame;
        readonly System.Windows.Media.Brush overlayRefreshA = Brush("#01000000"), overlayRefreshB = Brush("#02000000");
        DateTime overlayHandoffUntil;
        bool SteamOverlayHandoff { get { return DateTime.UtcNow < overlayHandoffUntil || steam != null && steam.OverlayActive; } }
        void PrepareSteamHotkey()
        {
            // Steam owns its overlay shortcut. Calling ActivateGameOverlay here
            // opens a second UI while Steam is already processing Shift+Tab.
            overlayHandoffUntil = DateTime.UtcNow.AddSeconds(3);
            if (steam == null || !steam.Available || !steam.OverlayEnabled) Toast("Оверлей Steam пока недоступен. Включи его в клиенте Steam и перезапусти игру.");
        }
        public async Task OverlaySmoke()
        {
            var report = new System.Collections.Generic.List<string>();
            try
            {
                await Until(() => steam.Available && steam.LobbyId != 0, "Steam не подключился к игре.");
                await Until(() => steam.OverlayEnabled, "Оверлей не подключился к графике WPF.", 15);
                window.UpdateLayout(); var repaint = C<Border>("SteamOverlayRefresh");
                CheckOverlayClientCoverage("Steam overlay");
                if (repaint.IsHitTestVisible) throw new Exception("Слой обновления Steam перехватывает ввод.");
                report.Add("PASS: Steam overlay hook enabled after early SDK initialization and before WPF rendering");
                if (!steam.OpenOverlay(true)) throw new Exception("Steam не открыл приглашения.");
                await Until(() => steam.OverlayActive, "Нет подтверждения открытия оверлея от Steam.", 10);
                if (!SteamOverlayHandoff || window.Topmost) throw new Exception("Игра перекрывает оверлей или не передаёт фокус Steam.");
                await Task.Delay(100);
                if (repaint.Background == System.Windows.Media.Brushes.Transparent) throw new Exception("Полнооконная перерисовка Steam не работает.");
                report.Add("PASS: full-window nonzero-alpha repaint runs while Steam captures input; the game does not reclaim focus or force topmost");
                report.Add("PASS: actual Steam invite overlay activated and callback 331 received; no invitations sent");
            }
            catch (Exception error) { report.Add("FAIL: " + error.GetBaseException().Message); }
            finally { File.WriteAllLines(System.IO.Path.Combine(Paths.Base, "steam-overlay-test.txt"), report); window.Close(); }
        }
        void InitializeSteam(bool enable)
        {
            steam = enable && Program.StartupSteam != null ? Program.StartupSteam : new SteamBridge(Program.EnableSteam && enable);
            if (Object.ReferenceEquals(steam, Program.StartupSteam)) Program.StartupSteam = null;
            workshop.ClientDownload = (id, progress, cancellation) => steam.DownloadWorkshopItem(id, progress, cancellation);
            steam.JoinRequested += lobby => window.Dispatcher.BeginInvoke(new Action(async delegate { if (!disposed) await Guard(() => OpenSteamInvite(lobby)); }));
            Text("SteamNetworkStatus", steam.Status);
            steam.OverlayChanged += active => window.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (disposed) return;
                overlayHandoffUntil = active ? DateTime.MaxValue : DateTime.UtcNow.AddMilliseconds(300);
                if (active) window.Topmost = false;
            }));
            overlayFrames.Tick += delegate
            {
                var refresh = C<Border>("SteamOverlayRefresh");
                if (steam.OverlayNeedsPresent || steam.OverlayActive)
                {
                    // Steam requires a FULL window repaint. WPF otherwise presents
                    // only dirty rectangles and can paint its UI over the overlay.
                    overlayFrame = !overlayFrame; refresh.Background = overlayFrame ? overlayRefreshA : overlayRefreshB;
                }
                else if (refresh.Background != System.Windows.Media.Brushes.Transparent) refresh.Background = System.Windows.Media.Brushes.Transparent;
            };
            overlayFrames.Start(); window.Closed += delegate { overlayFrames.Stop(); };
            B("ConnectSteamButton").Click += async delegate
            {
                if (state != null && state.phase != "lobby") { Toast("Steam можно подключить после завершения раунда."); return; }
                await Guard(async delegate
                {
                    if (!steam.Available)
                    {
                        string executable = Convert.ToString(Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamExe", ""));
                        if (!File.Exists(executable)) { Toast("Запусти Steam, затем нажми эту кнопку ещё раз."); return; }
                        Process.Start(new ProcessStartInfo(executable) { WindowStyle = ProcessWindowStyle.Hidden });
                        steam.Dispose(); await Task.Delay(2500); InitializeSteamAgain();
                    }
                    if (steam.Available) { await CreateRoom(); RefreshConnection(); Toast("Steam подключён. Приглашай друзей в лобби."); }
                    Text("SteamNetworkStatus", steam.Status);
                });
            };
        }
        void InitializeSteamAgain()
        {
            steam = new SteamBridge(true); steam.JoinRequested += lobby => window.Dispatcher.BeginInvoke(new Action(async delegate { if (!disposed) await Guard(() => JoinSteam(lobby)); }));
            RenderPlayerName();
        }
        string PlayerName
        {
            get
            {
                string name = steam != null && steam.Available ? steam.RefreshName() : null;
                if (String.IsNullOrWhiteSpace(name)) name = String.IsNullOrWhiteSpace(settings.Name) ? Environment.UserName : settings.Name;
                return name;
            }
        }
        void RenderPlayerName()
        {
            TBox("NameInput").Text = PlayerName; TBox("NameInput").IsReadOnly = steam != null && steam.Available;
            Text("NameSourceHint", steam != null && steam.Available ? "Имя загружено из твоего профиля Steam. Изменить его можно в Steam." : "Steam не подключён. Запусти клиент Steam и подключи его ниже.");
        }
        void SyncSteamName()
        {
            if (steam == null || !steam.Available || Me == null || state.phase != "lobby" || DateTime.UtcNow < nextPersonaCheck) return;
            nextPersonaCheck = DateTime.UtcNow.AddSeconds(5);
            string current = PlayerName;
            if (Me.name != current) { Send(new Message { kind = "rename", text = current }); if (currentPage == "Settings") RenderPlayerName(); }
        }
        public async Task JoinSteam(ulong lobby)
        {
            if (!steam.Available) throw new IOException("Запусти Steam и подключи его в настройках игры.");
            if (state != null && state.phase != "lobby") throw new IOException("Дождись завершения текущего раунда.");
            await steam.CodeForLobby(lobby);
            if (disposed) return;
            if (state != null && state.phase != "lobby") throw new IOException("Дождись завершения текущего раунда.");
            Disconnect(); Exception failure = null;
            try
            {
                int port = await steam.JoinRoom(lobby);
                await Connect("127.0.0.1", port, steam.Code, ""); Page("Lobby"); RefreshConnection();
            }
            catch (Exception error) { failure = error; }
            if (failure != null) { await CreateRoom(); throw failure; }
        }
        bool SteamRoute
        {
            get { return steam.Available && steam.LobbyId != 0; }
        }
        void InviteFriends()
        {
            if (!SteamRoute) { Invite(); return; }
            overlayHandoffUntil = DateTime.UtcNow.AddSeconds(3);
            if (steam.OpenOverlay(true)) return;
            var popup = new Window { Title = "Пригласить друзей Steam", Owner = window, Width = 520, Height = 500, WindowStartupLocation = WindowStartupLocation.CenterOwner, FontFamily = window.FontFamily, ShowInTaskbar = false, Background = Brush("#17151D"), Foreground = Brush("#F2EEF4") };
            popup.Resources.MergedDictionaries.Add(window.Resources);
            Theme.ApplyWindow(popup);
            var panel = new StackPanel { Margin = new Thickness(22) }; popup.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            panel.Children.Add(new TextBlock { Text = "Друзья в Steam", FontSize = 25, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 12) });
            panel.Children.Add(new TextBlock { Text = "Друг запускает ту же сборку Broken Phone, затем принимает приглашение. Нужен запущенный Steam; VPN не требуется.", Foreground = Brush("#BDB2CA"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) });
            var copy = SmallButton("Копировать код комнаты", Invite); copy.Margin = new Thickness(0, 0, 0, 14); panel.Children.Add(copy);
            var friends = steam.Friends();
            if (friends.Count == 0) panel.Children.Add(new TextBlock { Text = "Сейчас нет друзей онлайн. Можно отправить скопированную ссылку.", TextWrapping = TextWrapping.Wrap, Foreground = Brush("#BDB2CA") });
            foreach (var friend in friends)
            {
                var selected = friend; var row = new DockPanel { Margin = new Thickness(0, 5, 0, 5) };
                var invite = SmallButton("Пригласить", delegate { Toast(steam.Invite(selected.Id) ? "Приглашение отправлено: " + selected.Name : "Steam не отправил приглашение."); }); DockPanel.SetDock(invite, Dock.Right); row.Children.Add(invite);
                row.Children.Add(new TextBlock { Text = friend.Name, VerticalAlignment = VerticalAlignment.Center, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 10, 0) }); panel.Children.Add(row);
            }
            popup.ShowDialog();
        }
        void LobbyNavigation()
        {
            if (state != null && state.phase == "result") { if (IsHost) Send(new Message { kind = "reset" }); else Toast("Новый раунд начинает ведущий."); }
            else Page("Lobby");
        }
    }
}

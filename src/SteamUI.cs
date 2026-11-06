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
        void InitializeSteam(bool enable)
        {
            steam = new SteamBridge(Program.EnableSteam && enable);
            steam.JoinRequested += lobby => window.Dispatcher.BeginInvoke(new Action(async delegate { if (!disposed) await Guard(() => JoinSteam(lobby)); }));
            Text("SteamNetworkStatus", steam.Status);
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
            Text("NameSourceHint", steam != null && steam.Available ? "Имя загружено из твоего профиля Steam. Изменить его можно в Steam." : "Steam не подключён. Для LAN/Radmin можно указать своё имя.");
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
            if (!steam.Available) throw new IOException("Запусти Steam и нажми «Подключить Steam» в настройках.");
            if (state != null && state.phase != "lobby") throw new IOException("Дождись завершения текущего раунда.");
            Disconnect(); Exception failure = null;
            try
            {
                int port = await steam.JoinRoom(lobby); string invitation = steam.Invitation;
                await Connect("127.0.0.1", port, steam.Code, "", invitation); Page("Lobby"); RefreshConnection();
            }
            catch (Exception error) { failure = error; }
            if (failure != null) { await CreateRoom(); throw failure; }
        }
        bool SteamRoute
        {
            get { var address = C<ComboBox>("NetworkCombo").SelectedItem as NetworkAddress; return steam.Available && steam.LobbyId != 0 && (server == null || address != null && address.Steam); }
        }
        void InviteFriends()
        {
            if (!SteamRoute) { Invite(); return; }
            var popup = new Window { Title = "Пригласить друзей Steam", Owner = window, Width = 520, Height = 500, WindowStartupLocation = WindowStartupLocation.CenterOwner, FontFamily = window.FontFamily, ShowInTaskbar = false, Background = Brush("#141720"), Foreground = Brush("#EFF1FA") };
            popup.Resources.MergedDictionaries.Add(window.Resources);
            Theme.ApplyWindow(popup);
            var panel = new StackPanel { Margin = new Thickness(22) }; popup.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            panel.Children.Add(new TextBlock { Text = "Друзья в Steam", FontSize = 25, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 12) });
            panel.Children.Add(new TextBlock { Text = "Друг запускает Broken Phone 0.13, затем принимает приглашение. Нужен запущенный Steam; VPN не требуется.", Foreground = Brush("#AAB4CC"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) });
            var copy = SmallButton("Копировать ссылку на комнату", Invite); copy.Margin = new Thickness(0, 0, 0, 14); panel.Children.Add(copy);
            var friends = steam.Friends();
            if (friends.Count == 0) panel.Children.Add(new TextBlock { Text = "Сейчас нет друзей онлайн. Можно отправить скопированную ссылку.", TextWrapping = TextWrapping.Wrap, Foreground = Brush("#AAB4CC") });
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

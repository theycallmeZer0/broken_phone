using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace BrokenPhone
{
    public sealed partial class GameWindow
    {
        sealed class LobbyPlayerControls
        {
            public Grid Row;
            public Border Avatar;
            public TextBlock Name, Status, Ready;
            public StackPanel Actions;
            public Button Kick;
            public int Color, AvatarVersion;
            public ulong SteamId;
            public BitmapSource AvatarImage;
        }
        readonly Dictionary<string, LobbyPlayerControls> lobbyPlayers = new Dictionary<string, LobbyPlayerControls>();
        readonly Grid[] lobbyEmptyRows = new Grid[8];
        readonly List<ChatLine> renderedChat = new List<ChatLine>();
        RoomClient renderedLobbyRoom;
        string renderedLobbyCode;

        void EnsureLobbyRenderingRoom()
        {
            if (renderedLobbyRoom == client && renderedLobbyCode == state.code) return;
            renderedLobbyRoom = client; renderedLobbyCode = state.code;
            lobbyPlayers.Clear(); Array.Clear(lobbyEmptyRows, 0, lobbyEmptyRows.Length);
            C<StackPanel>("PlayersList").Children.Clear();
            renderedChat.Clear(); C<StackPanel>("ChatMessages").Children.Clear();
        }
        static Grid LobbyRow()
        {
            var row = new Grid { Height = 54, Margin = new Thickness(0, 0, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
            return row;
        }
        BitmapSource LobbyAvatarImage(ulong steamId)
        {
            AvatarBitmap bitmap;
            return steamId != 0 && steamAvatars.TryGetValue(steamId, out bitmap) ? bitmap.Image : null;
        }
        LobbyPlayerControls CreateLobbyPlayer(Player player)
        {
            var controls = new LobbyPlayerControls { Row = LobbyRow(), Color = player.color, SteamId = player.steamId, AvatarVersion = avatarVersion, AvatarImage = LobbyAvatarImage(player.steamId) };
            controls.Avatar = Avatar(player, 44); controls.Avatar.VerticalAlignment = VerticalAlignment.Center; controls.Row.Children.Add(controls.Avatar);
            var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) }; Grid.SetColumn(label, 1);
            controls.Name = new TextBlock { FontSize = 16, FontWeight = FontWeights.Bold, TextTrimming = TextTrimming.CharacterEllipsis };
            controls.Status = new TextBlock { FontSize = 9, Foreground = Brush("#858478"), TextTrimming = TextTrimming.CharacterEllipsis };
            label.Children.Add(controls.Name); label.Children.Add(controls.Status); controls.Row.Children.Add(label);
            controls.Actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(controls.Actions, 2);
            controls.Ready = new TextBlock { FontSize = 19, VerticalAlignment = VerticalAlignment.Center }; controls.Actions.Children.Add(controls.Ready); controls.Row.Children.Add(controls.Actions);
            return controls;
        }
        void UpdateLobbyPlayer(LobbyPlayerControls controls, Player player)
        {
            string name = player.name + (client != null && player.id == client.Id ? " · ты" : "");
            if (controls.Name.Text != name) controls.Name.Text = name;
            if (!Object.Equals(controls.Name.ToolTip, player.name)) controls.Name.ToolTip = player.name;
            bool showProgress = state.pack.Selected || state.pack.pendingDownload;
            string status = (player.host ? "Ведущий" : "По сети") + (showProgress && !String.IsNullOrEmpty(player.loadStatus) ? " · " + (player.downloadProgress > 0 && player.downloadProgress < 1 ? (player.downloadProgress * 100).ToString("0") + "% · " : "") + player.loadStatus : "");
            if (controls.Status.Text != status) controls.Status.Text = status;
            if (!Object.Equals(controls.Status.ToolTip, player.loadStatus)) controls.Status.ToolTip = player.loadStatus;
            string ready = player.ready ? "✓" : "·";
            if (controls.Ready.Text != ready) { controls.Ready.Text = ready; controls.Ready.Foreground = Brush(player.ready ? "#638352" : "#A19B8E"); }
            bool canKick = IsHost && !player.host;
            if (canKick && controls.Kick == null)
            {
                string id = player.id;
                controls.Kick = SmallButton("×", delegate { Send(new Message { kind = "kick", id = id }); }); controls.Kick.Padding = new Thickness(5, 0, 0, 0); controls.Kick.BorderThickness = new Thickness(0);
                controls.Actions.Children.Add(controls.Kick);
            }
            else if (!canKick && controls.Kick != null) { controls.Actions.Children.Remove(controls.Kick); controls.Kick = null; }
            if (controls.Color != player.color || controls.SteamId != player.steamId || controls.AvatarVersion != avatarVersion)
            {
                var image = LobbyAvatarImage(player.steamId);
                if (controls.Color != player.color || controls.SteamId != player.steamId || !Object.ReferenceEquals(controls.AvatarImage, image))
                {
                    controls.Row.Children.Remove(controls.Avatar);
                    controls.Avatar = Avatar(player, 44); controls.Avatar.VerticalAlignment = VerticalAlignment.Center; controls.Row.Children.Insert(0, controls.Avatar);
                }
                controls.Color = player.color; controls.SteamId = player.steamId; controls.AvatarVersion = avatarVersion; controls.AvatarImage = image;
            }
        }
        Grid EmptyLobbyRow(int slot)
        {
            if (lobbyEmptyRows[slot] != null) return lobbyEmptyRows[slot];
            var row = LobbyRow(); row.Height = 40;
            row.Children.Add(new Border { Width = 31, Height = 31, CornerRadius = new CornerRadius(10), BorderBrush = Brush("#D8D2C6"), BorderThickness = new Thickness(1), Child = new TextBlock { Text = "+", FontSize = 17, Foreground = Brush("#C2BCB0"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } });
            var label = new TextBlock { Text = "Место для друга", Foreground = Brush("#B0A99A"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) }; Grid.SetColumn(label, 1); row.Children.Add(label);
            lobbyEmptyRows[slot] = row; return row;
        }
        void RenderLobbyPlayers()
        {
            EnsureLobbyRenderingRoom();
            var panel = C<StackPanel>("PlayersList");
            for (int i = 0; i < lobbyEmptyRows.Length; i++)
            {
                Grid row;
                if (i < state.players.Count)
                {
                    var player = state.players[i]; LobbyPlayerControls controls;
                    if (!lobbyPlayers.TryGetValue(player.id, out controls)) { controls = CreateLobbyPlayer(player); lobbyPlayers.Add(player.id, controls); }
                    UpdateLobbyPlayer(controls, player); row = controls.Row;
                }
                else row = EmptyLobbyRow(i);
                if (i < panel.Children.Count && Object.ReferenceEquals(panel.Children[i], row)) continue;
                int previous = panel.Children.IndexOf(row); if (previous >= 0) panel.Children.RemoveAt(previous);
                panel.Children.Insert(i, row);
            }
            while (panel.Children.Count > lobbyEmptyRows.Length) panel.Children.RemoveAt(lobbyEmptyRows.Length);
            foreach (string id in lobbyPlayers.Keys.Where(id => !state.players.Any(player => player.id == id)).ToArray()) lobbyPlayers.Remove(id);
        }
        static bool SameChatLine(ChatLine left, ChatLine right)
        {
            return left.name == right.name && left.text == right.text && left.time == right.time;
        }
        bool SameChatRange(int previousStart, int length)
        {
            for (int i = 0; i < length; i++) if (!SameChatLine(renderedChat[previousStart + i], state.chat[i])) return false;
            return true;
        }
        TextBlock ChatMessage(ChatLine chat)
        {
            var line = new TextBlock { FontSize = 15, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 6, 6) };
            line.Inlines.Add(new System.Windows.Documents.Run(chat.time + "  ") { Foreground = Brush("#8F829E"), FontSize = 10 });
            if (chat.name != "Студия") line.Inlines.Add(new System.Windows.Documents.Run(chat.name + ": ") { Foreground = Brush("#C4B1FF"), FontWeight = FontWeights.Bold });
            line.Inlines.Add(new System.Windows.Documents.Run(chat.text) { Foreground = Brush(chat.name == "Студия" ? "#8B97B1" : "#E6EAF4") });
            return line;
        }
        void RenderLobbyChat()
        {
            EnsureLobbyRenderingRoom();
            // The room appends messages and keeps its last 100. Retain the overlap
            // by value because each state packet contains fresh ChatLine objects.
            int overlap = Math.Min(renderedChat.Count, state.chat.Count);
            while (overlap > 0 && !SameChatRange(renderedChat.Count - overlap, overlap)) overlap--;
            int remove = renderedChat.Count - overlap;
            if (remove == 0 && overlap == state.chat.Count) return;
            var scroll = C<ScrollViewer>("ChatScroll"); bool bottom = scroll.ScrollableHeight - scroll.VerticalOffset < 18;
            var messages = C<StackPanel>("ChatMessages");
            if (remove > 0) { renderedChat.RemoveRange(0, remove); messages.Children.RemoveRange(0, remove); }
            for (int i = overlap; i < state.chat.Count; i++)
            {
                var chat = state.chat[i]; renderedChat.Add(new ChatLine { name = chat.name, text = chat.text, time = chat.time }); messages.Children.Add(ChatMessage(chat));
            }
            if (bottom) scroll.ScrollToEnd();
        }
    }
}

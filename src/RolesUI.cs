using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BrokenPhone
{
    public sealed partial class GameWindow
    {
        sealed class RoleCardControls { public Button Choose; public Border Avatar; public TextBlock Owner; }
        readonly Dictionary<string, RoleCardControls> roleControls = new Dictionary<string, RoleCardControls>();
        string renderedRoles;

        void InitializeRoles()
        {
            B("AutoRolesButton").Click += delegate { Send(new Message { kind = "role_mode", text = "auto" }); };
            B("BeginDubbingButton").Click += delegate { Send(new Message { kind = "cast_start", round = state.round, index = state.roleVersion }); };
            B("CancelCastingButton").Click += delegate { Send(new Message { kind = "reset" }); };
            C<WrapPanel>("RoleCards").SizeChanged += delegate { UpdateRoleLayout(); };
        }

        void RenderRoles()
        {
            if (state == null) return;
            bool complete = RoleCasting.Complete(state);
            int mine = state.roles.Count(role => client != null && role.playerId == client.Id), assigned = state.roles.Count(role => !String.IsNullOrEmpty(role.playerId));
            int lines = state.roles.Where(role => client != null && role.playerId == client.Id).Sum(role => role.lines);
            if (state.phase == "lobby")
            {
                B("ReadyButton").IsEnabled = state.pack.Selected;
                bool enough = state.roles.Count >= state.players.Count;
                B("StartButton").IsEnabled = IsHost && state.pack.Selected && enough && downloadCancellation == null && state.players.Count >= 2 && state.players.All(player => player.ready);
                if (state.pack.pendingDownload) Text("StartHint", "Загружаем выбранный пак. Статус виден рядом с каждым игроком.");
                else if (!state.pack.Selected) Text("StartHint", "Пак не выбран. Выбери его в мастерской.");
                else if (!enough) Text("StartHint", "Ролей: " + state.roles.Count + ", игроков: " + state.players.Count + ". Выбери пак с ролью для каждого.");
                Text("ReadyHint", "Выбор пака → загрузка у игроков → готовность → выбор ролей → озвучка.");
            }
            if (currentPage != "Roles") return;
            Text("RolesPackTitle", state.pack.title.ToUpperInvariant());
            Text("RolesSubtitle", "Ролей: " + state.roles.Count + " · Реплик: " + state.pack.DisplayLines + ". Все реплики персонажа озвучивает один игрок.");
            Text("RolesModeHint", "Выбирайте желаемые роли в карточках. Ведущий может распределить их автоматически.");
            B("AutoRolesButton").Background = Brush("#BDB1CD"); B("AutoRolesButton").Foreground = Brush("#201B2E"); B("AutoRolesButton").IsEnabled = IsHost;
            Text("RolesStatus", "Роли: " + assigned + " / " + state.roles.Count + " · С ролью: " + state.players.Count(player => RoleCasting.HasRole(state, player.id)) + " / " + state.players.Count + " · Твоих реплик: " + lines);
            Text("RolesHelp", mine == 0 ? "Выбери хотя бы одну роль. Можно взять несколько персонажей." : complete ? "Все роли распределены. Ведущий может начать озвучку; до старта можно изменить выбор." : "Можно взять несколько ролей. У каждого игрока должна быть хотя бы одна роль.");
            B("BeginDubbingButton").Visibility = B("CancelCastingButton").Visibility = IsHost ? Visibility.Visible : Visibility.Collapsed;
            B("BeginDubbingButton").IsEnabled = state.phase == "casting" && complete;
            string key = Json.Write(new { state.roles, players = state.players.Select(player => new { player.id, player.name, player.color, player.steamId }).ToArray(), avatarVersion, me = client.Id });
            if (key == renderedRoles) return; renderedRoles = key;
            var panel = C<WrapPanel>("RoleCards"); panel.Children.Clear(); roleControls.Clear();
            foreach (var role in state.roles)
            {
                string roleName = role.role; var owner = state.players.FirstOrDefault(player => player.id == role.playerId); bool owned = owner != null && owner.id == client.Id;
                var card = new Border { Style = (Style)window.Resources["Card"], Padding = new Thickness(20), MinHeight = 228, Tag = roleName, BorderBrush = Brush(owned ? "#C5B4D2" : "#4A4154"), ToolTip = roleName };
                var body = new Grid(); card.Child = body;
                foreach (double height in new[] { 44.0, 24, 54, 10, 0 }) body.RowDefinitions.Add(new RowDefinition { Height = height == 0 ? GridLength.Auto : new GridLength(height) });
                body.Children.Add(new TextBlock { Text = roleName, FontSize = 20, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis });
                var count = new TextBlock { Text = "Реплик: " + role.lines, Foreground = Brush("#CFB3D9"), FontSize = 12 }; Grid.SetRow(count, 1); body.Children.Add(count);
                var row = new Grid(); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) }); row.ColumnDefinitions.Add(new ColumnDefinition()); Grid.SetRow(row, 2); body.Children.Add(row);
                var controls = new RoleCardControls();
                if (owner != null)
                {
                    controls.Avatar = Avatar(owner, 48); controls.Avatar.Tag = owner.id; controls.Avatar.ToolTip = owner.name;
                    controls.Avatar.HorizontalAlignment = HorizontalAlignment.Left; controls.Avatar.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(controls.Avatar);
                }
                else row.Children.Add(new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(18), BorderBrush = Brush("#4A536C"), BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Child = new TextBlock { Text = "+", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = Brush("#8F829E"), FontSize = 21 } });
                controls.Owner = new TextBlock { Text = owner == null ? "Свободна" : owner.name + (owned ? " · ты" : ""), FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = owner == null ? "Роль пока не выбрана" : owner.name, Foreground = Brush(owner == null ? "#8F829E" : "#F2EEF4") }; Grid.SetColumn(controls.Owner, 1); row.Children.Add(controls.Owner);
                controls.Choose = SmallButton(owned ? "Снять выбор" : owner == null ? "Выбрать роль" : "Занята", delegate { Send(new Message { kind = "role_select", id = roleName, flag = !owned }); });
                controls.Owner.FontSize = 16; controls.Owner.FontWeight = FontWeights.Bold;
                controls.Choose.IsEnabled = state.phase == "casting" && (owned || owner == null); controls.Choose.FontSize = 15; controls.Choose.Padding = new Thickness(14, 12, 14, 12); controls.Choose.MinHeight = 48;
                controls.Choose.Background = Brush(owned ? "#C5B4D2" : "#35303F"); controls.Choose.Foreground = Brush(owned ? "#2A1D2B" : "#CDD2E5");
                Grid.SetRow(controls.Choose, 4); body.Children.Add(controls.Choose); panel.Children.Add(card); roleControls.Add(roleName, controls);
            }
            UpdateRoleLayout();
        }

        void UpdateRoleLayout()
        {
            var panel = C<WrapPanel>("RoleCards"); if (panel.ActualWidth < 100) return;
            int columns = Math.Max(1, Math.Min(4, (int)((panel.ActualWidth + 16) / 296))); double width = Math.Floor((panel.ActualWidth - (columns - 1) * 16) / columns);
            double available = C<Grid>("RoleViewport").ActualHeight; if (available < 1) return;
            int rows = Math.Max(1, (int)((available + 16) / 244)); roleCapacity = columns * rows;
            int pages = Math.Max(1, (int)Math.Ceiling(panel.Children.Count / (double)roleCapacity)); rolePage = Math.Min(rolePage, pages - 1);
            for (int i = 0; i < panel.Children.Count; i++)
            {
                var card = (Border)panel.Children[i]; card.Width = width; card.Height = Math.Min(276, (available - (rows - 1) * 16) / rows); card.Margin = new Thickness(0, 0, (i + 1) % columns == 0 ? 0 : 16, 16); card.Visibility = i / roleCapacity == rolePage ? Visibility.Visible : Visibility.Collapsed;
            }
            C<StackPanel>("RolePagination").Visibility = pages > 1 ? Visibility.Visible : Visibility.Collapsed;
            Text("RolePageLabel", (rolePage + 1) + " / " + pages); B("PreviousRolesButton").IsEnabled = rolePage > 0; B("NextRolesButton").IsEnabled = rolePage + 1 < pages;
        }
    }
}

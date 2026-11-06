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
            B("ChooseRolesButton").Click += delegate { OpenRoles(); };
            B("AutoRolesButton").Click += delegate { Send(new Message { kind = "role_mode", text = "auto" }); };
            B("ManualRolesButton").Click += delegate { Send(new Message { kind = "role_mode", text = "manual" }); };
            B("RolesDoneButton").Click += delegate { Page("Lobby"); };
            C<WrapPanel>("RoleCards").SizeChanged += delegate { UpdateRoleLayout(); };
        }

        void OpenRoles()
        {
            if (state == null || state.phase != "lobby") { Toast("Роли выбираются перед началом раунда."); return; }
            Page("Roles"); RenderRoles();
        }

        void RenderRoles()
        {
            if (state == null) return;
            bool manual = state.roleMode == "manual", complete = RoleCasting.Complete(state);
            int mine = state.roles.Count(role => client != null && role.playerId == client.Id), assigned = state.roles.Count(role => !String.IsNullOrEmpty(role.playerId));
            int lines = state.roles.Where(role => client != null && role.playerId == client.Id).Sum(role => role.lines);
            Text("RolesLobbySummary", (manual ? "Выбираем сами" : "Распределено автоматически") + " · Ролей: " + assigned + " / " + state.roles.Count + " · Твоих: " + mine);
            B("ChooseRolesButton").Content = manual ? "Выбрать роли  →" : "Роли и режим  →"; B("ChooseRolesButton").IsEnabled = state.phase == "lobby";
            if (state.phase == "lobby")
            {
                B("ReadyButton").IsEnabled = complete;
                B("StartButton").IsEnabled = IsHost && complete && downloadCancellation == null && state.players.Count >= 2 && state.players.All(player => player.ready);
                if (!complete) Text("StartHint", "Выберите все роли: " + assigned + " из " + state.roles.Count + ".");
                Text("ReadyHint", mine == 0 ? "У тебя нет роли. Можешь выбрать персонажа или смотреть раунд." : "Твоих ролей: " + mine + " · Реплик: " + lines + ". Каждый персонаж сохраняет свой голос.");
            }
            if (currentPage != "Roles") return;
            Text("RolesPackTitle", state.pack.title.ToUpperInvariant());
            Text("RolesSubtitle", "Ролей: " + state.roles.Count + " · Реплик: " + state.pack.DisplayLines + ". Все реплики персонажа озвучивает один игрок.");
            Text("RolesModeHint", IsHost ? "Выбери способ распределения для всей комнаты." : "Способ распределения выбирает ведущий.");
            foreach (string name in new[] { "AutoRolesButton", "ManualRolesButton" })
            {
                bool selected = name == (manual ? "ManualRolesButton" : "AutoRolesButton"); var button = B(name);
                button.Background = Brush(selected ? "#B7A7DF" : "#252C3D"); button.Foreground = Brush(selected ? "#201B2E" : "#CDD2E5"); button.IsEnabled = IsHost;
            }
            Text("RolesStatus", "Выбрано: " + assigned + " / " + state.roles.Count + " · Твоих ролей: " + mine + " · Реплик: " + lines);
            Text("RolesHelp", manual ? "Можно взять несколько ролей. Нажми «Снять выбор», чтобы освободить роль и выбрать другую." : "Роли распределены с учётом количества реплик. Для своего выбора ведущий может включить ручной режим.");
            string key = Json.Write(new { state.roleMode, state.roles, players = state.players.Select(player => new { player.id, player.name, player.color }).ToArray(), me = client.Id });
            if (key == renderedRoles) return; renderedRoles = key;
            var panel = C<WrapPanel>("RoleCards"); double offset = C<ScrollViewer>("RolesScroll").VerticalOffset; panel.Children.Clear(); roleControls.Clear();
            foreach (var role in state.roles)
            {
                string roleName = role.role; var owner = state.players.FirstOrDefault(player => player.id == role.playerId); bool owned = owner != null && owner.id == client.Id;
                var card = new Border { Style = (Style)window.Resources["Card"], Padding = new Thickness(19), Height = 227, Tag = roleName, BorderBrush = Brush(owned ? "#99D8B0" : "#39435B"), ToolTip = roleName };
                var body = new Grid(); card.Child = body;
                foreach (double height in new[] { 50.0, 26, 53, 17, 40 }) body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(height) });
                body.Children.Add(new TextBlock { Text = roleName, FontSize = 20, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis });
                var count = new TextBlock { Text = "Реплик: " + role.lines, Foreground = Brush("#BDAAFF"), FontSize = 12 }; Grid.SetRow(count, 1); body.Children.Add(count);
                var row = new Grid(); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) }); row.ColumnDefinitions.Add(new ColumnDefinition()); Grid.SetRow(row, 2); body.Children.Add(row);
                var controls = new RoleCardControls();
                if (owner != null)
                {
                    controls.Avatar = Avatar(owner.color, 36); controls.Avatar.CornerRadius = new CornerRadius(18); controls.Avatar.Clip = new EllipseGeometry(new Point(18, 18), 18, 18); controls.Avatar.Tag = owner.id; controls.Avatar.ToolTip = owner.name;
                    controls.Avatar.HorizontalAlignment = HorizontalAlignment.Left; controls.Avatar.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(controls.Avatar);
                }
                else row.Children.Add(new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(18), BorderBrush = Brush("#4A536C"), BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Child = new TextBlock { Text = "+", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = Brush("#78849E"), FontSize = 21 } });
                controls.Owner = new TextBlock { Text = owner == null ? "Свободна" : owner.name + (owned ? " · ты" : ""), FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = owner == null ? "Роль пока не выбрана" : owner.name, Foreground = Brush(owner == null ? "#78849E" : "#EFF1FA") }; Grid.SetColumn(controls.Owner, 1); row.Children.Add(controls.Owner);
                controls.Choose = SmallButton(owned ? "Снять выбор" : owner == null ? "Выбрать роль" : "Занята", delegate { Send(new Message { kind = "role_select", id = roleName, flag = !owned }); });
                controls.Choose.IsEnabled = manual && (owned || owner == null); controls.Choose.FontSize = 13; controls.Choose.Padding = new Thickness(12, 9, 12, 9);
                controls.Choose.Background = Brush(owned ? "#99D8B0" : "#2C3345"); controls.Choose.Foreground = Brush(owned ? "#16251E" : "#CDD2E5");
                if (!manual) controls.Choose.Content = "Назначена автоматически";
                Grid.SetRow(controls.Choose, 4); body.Children.Add(controls.Choose); panel.Children.Add(card); roleControls.Add(roleName, controls);
            }
            UpdateRoleLayout(); C<ScrollViewer>("RolesScroll").ScrollToVerticalOffset(offset);
        }

        void UpdateRoleLayout()
        {
            var panel = C<WrapPanel>("RoleCards"); if (panel.ActualWidth < 100) return;
            int columns = Math.Max(1, Math.Min(4, (int)((panel.ActualWidth + 16) / 296))); double width = Math.Floor((panel.ActualWidth - (columns - 1) * 16) / columns);
            for (int i = 0; i < panel.Children.Count; i++) { var card = (Border)panel.Children[i]; card.Width = width; card.Margin = new Thickness(0, 0, (i + 1) % columns == 0 ? 0 : 16, 16); }
        }
    }
}

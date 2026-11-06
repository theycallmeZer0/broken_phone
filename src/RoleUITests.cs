using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;

namespace BrokenPhone
{
    public sealed partial class GameWindow
    {
        public async Task RolesSmoke()
        {
            Window otherWindow = null; GameWindow other = null; var report = new List<string>();
            try
            {
                await Until(() => Me != null, "Ведущий не подключился.");
                using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MainWindow.xaml")) otherWindow = (Window)XamlReader.Load(stream);
                otherWindow.ShowInTaskbar = false; otherWindow.WindowStartupLocation = WindowStartupLocation.Manual; otherWindow.Left = -10000;
                other = new GameWindow(otherWindow, false); other.settings.Name = "Тестовый друг"; other.settings.Volume = 0; otherWindow.Show();
                await other.Connect("127.0.0.1", server.Port, server.Code, ""); await Until(() => state.players.Count == 2 && other.Me != null, "Гость не подключился.");
                OpenRoles(); other.OpenRoles();
                if (roleControls.Count != 2 || roleControls.Values.Any(card => card.Avatar == null || card.Choose.IsEnabled) || other.B("ManualRolesButton").IsEnabled) throw new Exception("Автоматические роли или права ведущего отображаются неверно.");
                foreach (Border card in C<WrapPanel>("RoleCards").Children) if (!((Grid)card.Child).Children.OfType<TextBlock>().Any(label => label.Text == "Реплик: 2")) throw new Exception("Нет количества реплик в карточке роли.");
                B("ManualRolesButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => state.roleMode == "manual" && other.state.roleMode == "manual" && roleControls.Values.All(card => card.Avatar == null), "Режим не переключился у игроков.");
                roleControls["Режиссёр"].Choose.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); other.roleControls["Актёр"].Choose.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => RoleCasting.Complete(state) && RoleCasting.Complete(other.state) && other.roleControls["Режиссёр"].Avatar != null && roleControls["Актёр"].Avatar != null, "Выбор не передался между окнами.");
                if (!Object.Equals(other.roleControls["Режиссёр"].Avatar.Tag, client.Id) || !Object.Equals(roleControls["Актёр"].Avatar.Tag, other.client.Id) || !(roleControls["Актёр"].Avatar.Clip is EllipseGeometry) || roleControls["Актёр"].Choose.IsEnabled) throw new Exception("Неверная аватарка или доступна занятая роль.");
                window.UpdateLayout(); SaveScreen("roles-manual-preview.png"); other.SaveScreen("roles-guest-preview.png");
                report.Add("PASS: two actual WPF windows share manual/automatic mode, per-role line counts, actual participant labels and circular owner avatars; occupied role disabled for other players");
                B("ReadyButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); other.B("ReadyButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => state.players.All(player => player.ready), "Нет готовности.");
                other.roleControls["Актёр"].Choose.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => !RoleCasting.Complete(state) && state.players.All(player => !player.ready), "Не сброшена готовность при снятии роли.");
                if (B("ReadyButton").IsEnabled || B("StartButton").IsEnabled) throw new Exception("Можно начать с незанятой ролью.");
                roleControls["Актёр"].Choose.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => state.roles.All(role => role.playerId == client.Id), "Нельзя выбрать несколько ролей.");
                roleControls["Режиссёр"].Choose.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => other.roleControls["Режиссёр"].Choose.IsEnabled, "Роль не освобождена для перевыбора.");
                other.roleControls["Режиссёр"].Choose.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => RoleCasting.Complete(state) && state.roles.First(role => role.role == "Режиссёр").playerId == other.client.Id, "Роль не перевыбрана.");
                report.Add("PASS: role release, multiple roles per player, reselection by another player and ready/start gate update immediately in both windows");
                B("RolesDoneButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); other.B("RolesDoneButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                B("ReadyButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); other.B("ReadyButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => state.players.All(player => player.ready), "Нет готовности после перевыбора.");
                B("StartButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => state.phase == "recording" && sourcePrepared && other.sourcePrepared, "Пак не подготовился у всех.");
                if (IsTurn || !other.IsTurn || !other.B("RecordButton").IsEnabled || other.C<TextBlock>("RoleCaption").Text != "РЕЖИССЁР" || B("LobbyTab").IsEnabled) throw new Exception("Первый ход не принадлежит выбранному исполнителю роли.");
                SaveScreen("roles-recording-preview.png"); report.Add("PASS: after the loading barrier the selected role owner records first, independently of connection order; other player waits and active-round lobby return stays disabled");
            }
            catch (Exception error) { report.Add("FAIL: " + error); }
            finally { File.WriteAllLines(Path.Combine(Paths.Base, "roles-ui-test.txt"), report); if (other != null) other.Dispose(); if (otherWindow != null) otherWindow.Close(); window.Close(); }
        }
    }
}

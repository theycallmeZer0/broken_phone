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
        async Task StartAutomaticRoles(GameWindow other)
        {
            await Until(() => state.phase == "casting" && other.state.phase == "casting" && currentPage == "Roles" && other.currentPage == "Roles", "Выбор ролей не открылся у всех после начала игры.");
            B("AutoRolesButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => RoleCasting.Complete(state) && RoleCasting.Complete(other.state), "Автоматическое распределение не завершилось.");
            await Until(() => B("BeginDubbingButton").IsEnabled, "После распределения нельзя начать озвучку.");
            if (steam.Available) SaveScreen("steam-roles-preview.png");
            B("BeginDubbingButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }

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
                if (window.FindName("ChooseRolesButton") != null || !B("StartButton").Content.ToString().Contains("Начать игру") || state.roles.Any(role => !String.IsNullOrEmpty(role.playerId))) throw new Exception("Распределение ролей доступно до начала игры.");
                B("ReadyButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); other.B("ReadyButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => state.players.All(player => player.ready), "Нет готовности в лобби.");
                SaveScreen("game-lobby-preview.png"); B("StartButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => currentPage == "Roles" && other.currentPage == "Roles" && state.phase == "casting" && other.state.phase == "casting", "Обязательный выбор ролей не открылся у обоих игроков.");
                if (roleControls.Count != 2 || roleControls.Values.Any(card => card.Avatar != null || !card.Choose.IsEnabled) || B("BeginDubbingButton").IsEnabled || B("LobbyTab").IsEnabled || other.B("AutoRolesButton").IsEnabled || window.FindName("RolesReadyButton") != null || window.FindName("ManualRolesButton") != null) throw new Exception("Выбор не доступен сразу или остались лишние кнопки.");
                SaveScreen("roles-pending-preview.png");
                B("AutoRolesButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => RoleCasting.Complete(state) && RoleCasting.Complete(other.state), "Не работает автоматическое распределение.");
                await Until(() => B("BeginDubbingButton").IsEnabled && RoleCasting.Complete(other.state), "После автоматического распределения требуется подтверждение.");
                string guestRole = other.state.roles.First(role => role.playerId == other.client.Id).role;
                if (!other.roleControls[guestRole].Choose.IsEnabled) throw new Exception("Автоматически назначенную роль нельзя изменить.");
                other.roleControls[guestRole].Choose.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => !B("BeginDubbingButton").IsEnabled && other.roleControls[guestRole].Choose.IsEnabled, "Нельзя снять назначенную роль.");
                other.roleControls[guestRole].Choose.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => B("BeginDubbingButton").IsEnabled, "После перевыбора требуется подтверждение.");
                B("CancelCastingButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => currentPage == "Lobby" && other.currentPage == "Lobby" && state.roles.All(role => String.IsNullOrEmpty(role.playerId)), "Отмена не вернула всех в лобби.");
                report.Add("PASS: casting offers only automatic distribution and immediate role-card selection; no manual-mode or confirmation buttons; automatic assignments remain editable; host can start without confirmations");
                B("ReadyButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); other.B("ReadyButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => state.players.All(player => player.ready), "Нет готовности после отмены.");
                B("StartButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => currentPage == "Roles" && other.currentPage == "Roles", "Не открылся повторный выбор.");
                foreach (Border card in C<WrapPanel>("RoleCards").Children) if (!((Grid)card.Child).Children.OfType<TextBlock>().Any(label => label.Text == "Реплик: 2")) throw new Exception("Нет количества реплик в карточке роли.");
                roleControls["Режиссёр"].Choose.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); other.roleControls["Актёр"].Choose.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => RoleCasting.Complete(state) && RoleCasting.Complete(other.state) && other.roleControls["Режиссёр"].Avatar != null && roleControls["Актёр"].Avatar != null, "Выбор не передался между окнами.");
                if (!Object.Equals(other.roleControls["Режиссёр"].Avatar.Tag, client.Id) || !Object.Equals(roleControls["Актёр"].Avatar.Tag, other.client.Id) || !(roleControls["Актёр"].Avatar.Clip is EllipseGeometry) || roleControls["Актёр"].Choose.IsEnabled) throw new Exception("Неверная аватарка или доступна занятая роль.");
                await Until(() => B("BeginDubbingButton").IsEnabled, "Для старта требуется лишнее подтверждение.");
                window.UpdateLayout(); SaveScreen("roles-manual-preview.png"); other.SaveScreen("roles-guest-preview.png");
                other.roleControls["Актёр"].Choose.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => !RoleCasting.Complete(state) && state.players.All(player => !player.ready), "Не сброшены подтверждения при снятии роли.");
                if (B("BeginDubbingButton").IsEnabled) throw new Exception("Участник без роли может продолжить.");
                roleControls["Актёр"].Choose.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => state.roles.All(role => role.playerId == client.Id), "Нельзя взять несколько ролей.");
                if (RoleCasting.Complete(state) || B("BeginDubbingButton").IsEnabled) throw new Exception("Распределение допускает игрока без роли.");
                roleControls["Режиссёр"].Choose.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => other.roleControls["Режиссёр"].Choose.IsEnabled, "Роль не освобождена.");
                other.roleControls["Режиссёр"].Choose.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => RoleCasting.Complete(state) && state.roles.First(role => role.role == "Режиссёр").playerId == other.client.Id, "Роль не перевыбрана.");
                await Until(() => B("BeginDubbingButton").IsEnabled, "После перевыбора требуется подтверждение.");
                Page("Lobby"); if (currentPage != "Roles") throw new Exception("Обязательный этап можно закрыть переходом в лобби.");
                report.Add("PASS: desired roles, counts and circular owners synchronize; reselection needs no confirmation; all roles must be assigned and each participant must own a role; no navigation bypass");
                B("BeginDubbingButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => state.phase == "recording" && sourcePrepared && other.sourcePrepared, "Пак не подготовился у всех.");
                if (IsTurn || !other.IsTurn || !other.B("RecordButton").IsEnabled || other.C<TextBlock>("RoleCaption").Text != "РЕЖИССЁР" || B("LobbyTab").IsEnabled) throw new Exception("Первый ход не принадлежит выбранному исполнителю.");
                SaveScreen("roles-recording-preview.png"); report.Add("PASS: host starts directly from complete assignments; all-player pack loading and recording follow the selected character owners");
            }
            catch (Exception error) { report.Add("FAIL: " + error); }
            finally { File.WriteAllLines(Path.Combine(Paths.Base, "roles-ui-test.txt"), report); if (other != null) other.Dispose(); if (otherWindow != null) otherWindow.Close(); window.Close(); }
        }
    }
}

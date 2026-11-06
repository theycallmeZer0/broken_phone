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
        async Task ConfirmAutomaticRoles(GameWindow other)
        {
            await Until(() => state.phase == "casting" && other.state.phase == "casting" && currentPage == "Roles" && other.currentPage == "Roles", "Выбор ролей не открылся у всех после начала игры.");
            B("AutoRolesButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => RoleCasting.Complete(state) && RoleCasting.Complete(other.state), "Автоматическое распределение не завершилось.");
            B("RolesReadyButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); other.B("RolesReadyButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => state.players.All(player => player.ready), "Роли не подтверждены участниками.");
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
                if (state.roleMode != "pending" || roleControls.Count != 2 || roleControls.Values.Any(card => card.Avatar != null) || B("RolesReadyButton").IsEnabled || B("BeginDubbingButton").IsEnabled || B("LobbyTab").IsEnabled || other.B("ManualRolesButton").IsEnabled || window.FindName("RolesDoneButton") != null) throw new Exception("Этап ролей можно пропустить или он уже распределён.");
                SaveScreen("roles-pending-preview.png");
                B("AutoRolesButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => RoleCasting.Complete(state) && RoleCasting.Complete(other.state), "Не работает автоматическое распределение.");
                if (B("BeginDubbingButton").IsEnabled || !B("RolesReadyButton").IsEnabled || roleControls.Values.Any(card => card.Avatar == null)) throw new Exception("Автоматическое распределение обходит подтверждение роли.");
                B("RolesReadyButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); other.B("RolesReadyButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => B("BeginDubbingButton").IsEnabled, "Подтверждения не открывают озвучку.");
                B("CancelCastingButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => currentPage == "Lobby" && other.currentPage == "Lobby" && state.roles.All(role => String.IsNullOrEmpty(role.playerId)), "Отмена не вернула всех в лобби.");
                report.Add("PASS: Start Game opens mandatory casting in BOTH windows; no lobby role assignment or skip button; automatic roles require individual confirmation; host cancellation returns everyone to lobby");
                B("ReadyButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); other.B("ReadyButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => state.players.All(player => player.ready), "Нет готовности после отмены.");
                B("StartButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => currentPage == "Roles" && other.currentPage == "Roles", "Не открылся повторный выбор.");
                B("ManualRolesButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => state.roleMode == "manual" && other.state.roleMode == "manual", "Режим не переключился.");
                foreach (Border card in C<WrapPanel>("RoleCards").Children) if (!((Grid)card.Child).Children.OfType<TextBlock>().Any(label => label.Text == "Реплик: 2")) throw new Exception("Нет количества реплик в карточке роли.");
                roleControls["Режиссёр"].Choose.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); other.roleControls["Актёр"].Choose.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => RoleCasting.Complete(state) && RoleCasting.Complete(other.state) && other.roleControls["Режиссёр"].Avatar != null && roleControls["Актёр"].Avatar != null, "Выбор не передался между окнами.");
                if (!Object.Equals(other.roleControls["Режиссёр"].Avatar.Tag, client.Id) || !Object.Equals(roleControls["Актёр"].Avatar.Tag, other.client.Id) || !(roleControls["Актёр"].Avatar.Clip is EllipseGeometry) || roleControls["Актёр"].Choose.IsEnabled) throw new Exception("Неверная аватарка или доступна занятая роль.");
                B("RolesReadyButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); other.B("RolesReadyButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => state.players.All(player => player.ready), "Нет подтверждений ролей.");
                window.UpdateLayout(); SaveScreen("roles-manual-preview.png"); other.SaveScreen("roles-guest-preview.png");
                other.roleControls["Актёр"].Choose.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => !RoleCasting.Complete(state) && state.players.All(player => !player.ready), "Не сброшены подтверждения при снятии роли.");
                if (B("BeginDubbingButton").IsEnabled || other.B("RolesReadyButton").IsEnabled) throw new Exception("Участник без роли может продолжить.");
                roleControls["Актёр"].Choose.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => state.roles.All(role => role.playerId == client.Id), "Нельзя взять несколько ролей.");
                if (RoleCasting.Complete(state) || B("BeginDubbingButton").IsEnabled) throw new Exception("Распределение допускает игрока без роли.");
                roleControls["Режиссёр"].Choose.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => other.roleControls["Режиссёр"].Choose.IsEnabled, "Роль не освобождена.");
                other.roleControls["Режиссёр"].Choose.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => RoleCasting.Complete(state) && state.roles.First(role => role.role == "Режиссёр").playerId == other.client.Id, "Роль не перевыбрана.");
                B("RolesReadyButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => Me.ready, "Подтверждение ведущего не передано.");
                if (B("BeginDubbingButton").IsEnabled) throw new Exception("Ведущий обходит подтверждение гостя.");
                Page("Lobby"); if (currentPage != "Roles") throw new Exception("Обязательный этап можно закрыть переходом в лобби.");
                other.B("RolesReadyButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => B("BeginDubbingButton").IsEnabled, "Нет допуска к озвучке после перевыбора.");
                report.Add("PASS: manual role counts and circular owner avatars synchronize; every participant must own and confirm a role; reselection resets confirmations; no navigation bypass or spectator shortcut");
                B("BeginDubbingButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => state.phase == "recording" && sourcePrepared && other.sourcePrepared, "Пак не подготовился у всех.");
                if (IsTurn || !other.IsTurn || !other.B("RecordButton").IsEnabled || other.C<TextBlock>("RoleCaption").Text != "РЕЖИССЁР" || B("LobbyTab").IsEnabled) throw new Exception("Первый ход не принадлежит выбранному исполнителю.");
                SaveScreen("roles-recording-preview.png"); report.Add("PASS: confirmed cast proceeds through all-player pack loading to recording by the selected character owner");
            }
            catch (Exception error) { report.Add("FAIL: " + error); }
            finally { File.WriteAllLines(Path.Combine(Paths.Base, "roles-ui-test.txt"), report); if (other != null) other.Dispose(); if (otherWindow != null) otherWindow.Close(); window.Close(); }
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Controls;

namespace BrokenPhone
{
    public sealed partial class GameWindow
    {
        Grid[] LobbyTestRows() { return C<StackPanel>("PlayersList").Children.Cast<Grid>().ToArray(); }
        TextBlock[] LobbyTestMessages() { return C<StackPanel>("ChatMessages").Children.Cast<TextBlock>().ToArray(); }
        static TextBlock LobbyTestName(Grid row) { return ((StackPanel)row.Children[1]).Children.OfType<TextBlock>().First(); }
        static TextBlock LobbyTestStatus(Grid row) { return ((StackPanel)row.Children[1]).Children.OfType<TextBlock>().Last(); }
        static void LobbyRenderingCheck(bool condition, string failure) { if (!condition) throw new Exception(failure); }
        void CheckLobbyObjects(Grid[] rows, TextBlock[] messages)
        {
            var currentRows = LobbyTestRows(); var currentMessages = LobbyTestMessages();
            LobbyRenderingCheck(currentRows.Length == rows.Length && currentRows.Zip(rows, Object.ReferenceEquals).All(same => same), "Обновление комнаты пересоздало строки игроков.");
            LobbyRenderingCheck(currentMessages.Length == messages.Length && currentMessages.Zip(messages, Object.ReferenceEquals).All(same => same), "Обновление комнаты пересоздало неизменившийся чат.");
        }
        public async Task LobbyRenderingSmoke()
        {
            var report = new List<string>(); var guest = new RoomClient(); var other = new RoomClient();
            const ulong avatarId = 76561198000000123UL;
            try
            {
                await Until(() => Me != null, "Лобби не создано.");
                if (videoDownload != null) await videoDownload;
                LobbyRenderingCheck(state.pack.Selected, "Для проверки прогресса нужен тестовый пак.");
                await Task.Run(() => guest.Connect("127.0.0.1", server.Port, server.Code, "Первый гость", "", avatarId));
                await Until(() => state.players.Count == 2, "Первый гость не подключился.");
                var rows = LobbyTestRows(); var messages = LobbyTestMessages();
                var hostAvatar = rows[0].Children.OfType<Border>().First(); var guestAvatar = rows[1].Children.OfType<Border>().First();
                var previous = state;
                guest.Send(new Message { kind = "rename", text = "Первый гость" });
                await Until(() => !Object.ReferenceEquals(previous, state), "Не получен повторный state-пакет.");
                CheckLobbyObjects(rows, messages);
                for (int i = 1; i <= 12; i++)
                {
                    double progress = i / 20.0;
                    guest.Send(new Message { kind = "pack_download_progress", id = state.pack.contentKey, position = progress, text = "Получаем видео" });
                    await Until(() => state.players.Single(player => player.id == guest.Id).downloadProgress == progress, "Прогресс не дошёл до лобби.");
                    CheckLobbyObjects(rows, messages);
                    LobbyRenderingCheck(LobbyTestStatus(rows[1]).Text.Contains((progress * 100).ToString("0") + "%") && LobbyTestStatus(rows[1]).Text.Contains("Получаем видео"), "Строка гостя не обновила видимый прогресс.");
                    LobbyRenderingCheck(Object.ReferenceEquals(hostAvatar, rows[0].Children.OfType<Border>().First()) && Object.ReferenceEquals(guestAvatar, rows[1].Children.OfType<Border>().First()), "Прогресс пересоздал неизменившиеся аватарки.");
                }
                guest.Send(new Message { kind = "ready", flag = true });
                await Until(() => state.players.Single(player => player.id == guest.Id).ready, "Готовность гостя не обновилась.");
                CheckLobbyObjects(rows, messages);
                LobbyRenderingCheck(((StackPanel)rows[1].Children[2]).Children.OfType<TextBlock>().Single().Text == "✓", "Строка гостя не показала готовность.");
                guest.Send(new Message { kind = "rename", text = "Новое имя гостя" });
                await Until(() => state.players.Single(player => player.id == guest.Id).name == "Новое имя гостя", "Имя гостя не обновилось.");
                CheckLobbyObjects(rows, messages);
                LobbyRenderingCheck(LobbyTestName(rows[1]).Text == "Новое имя гостя" && ((StackPanel)rows[1].Children[2]).Children.OfType<Button>().Count() == 1, "Имя или кнопка исключения гостя не обновились.");
                report.Add("PASS: real TCP state packets preserve all eight row objects, existing chat messages and avatars while twelve progress updates, readiness and a rename update only their visible controls");

                var avatar = new SteamAvatar { Width = 1, Height = 1, Rgba = new byte[] { 120, 160, 200, 255 } };
                var bitmap = AvatarBitmapSource(avatar); steamAvatars[avatarId] = new AvatarBitmap { Data = avatar, Image = bitmap }; avatarVersion++;
                previous = state; guest.Send(new Message { kind = "rename", text = "Новое имя гостя" });
                await Until(() => !Object.ReferenceEquals(previous, state), "Не получен пакет для обновления аватарки.");
                CheckLobbyObjects(rows, messages);
                var updatedAvatar = rows[1].Children.OfType<Border>().First();
                LobbyRenderingCheck(!Object.ReferenceEquals(guestAvatar, updatedAvatar) && updatedAvatar.Child is Image && Object.ReferenceEquals(((Image)updatedAvatar.Child).Source, bitmap) && Object.ReferenceEquals(hostAvatar, rows[0].Children.OfType<Border>().First()), "Обновление аватарки Steam изменило чужую строку или не заменило нужную картинку.");
                report.Add("PASS: an avatar revision replaces only the changed Steam avatar; the owning row and other avatars remain intact");

                await Task.Run(() => other.Connect("127.0.0.1", server.Port, server.Code, "Второй гость", ""));
                await Until(() => state.players.Count == 3, "Второй гость не подключился.");
                var joinedRows = LobbyTestRows(); var otherRow = joinedRows[2];
                LobbyRenderingCheck(Object.ReferenceEquals(rows[0], joinedRows[0]) && Object.ReferenceEquals(rows[1], joinedRows[1]), "Вход игрока пересоздал строки присутствующих.");
                guest.Dispose();
                await Until(() => state.players.Count == 2 && state.players[1].id == other.Id, "Выход гостя не обновил список.");
                LobbyRenderingCheck(Object.ReferenceEquals(rows[0], LobbyTestRows()[0]) && Object.ReferenceEquals(otherRow, LobbyTestRows()[1]), "Выход игрока пересоздал оставшиеся строки вместо их перестановки.");
                report.Add("PASS: joining adds one player view and leaving moves the surviving player's existing row into its new position");

                string marker = "retained-chat-" + Guid.NewGuid().ToString("N");
                messages = LobbyTestMessages(); Send(new Message { kind = "chat", text = marker });
                await Until(() => state.chat.Last().text == marker, "Новое сообщение не пришло.");
                var appended = LobbyTestMessages();
                LobbyRenderingCheck(appended.Length == messages.Length + 1 && messages.Zip(appended, Object.ReferenceEquals).All(same => same), "Добавление сообщения пересоздало старую историю.");
                for (int i = 0; i < 104; i++) Send(new Message { kind = "chat", text = marker + ":" + i });
                await Until(() => state.chat.Count == 100 && state.chat.Last().text == marker + ":103", "История не заполнилась до 100 сообщений.");
                messages = LobbyTestMessages(); Send(new Message { kind = "chat", text = marker + ":104" });
                await Until(() => state.chat.Last().text == marker + ":104", "Последнее сообщение не пришло.");
                var trimmed = LobbyTestMessages();
                LobbyRenderingCheck(trimmed.Length == 100 && Enumerable.Range(0, 99).All(i => Object.ReferenceEquals(messages[i + 1], trimmed[i])), "Обрезка истории не сохранила 99 оставшихся сообщений.");
                report.Add("PASS: appending allocates only a new message; the 100-message sliding history retains all 99 overlapping TextBlock objects");

                var previousRows = LobbyTestRows(); var previousMessages = LobbyTestMessages();
                Disconnect();
                using (var room = new RoomServer(PackLibrary.Empty(), 0))
                using (var host = new RoomClient())
                {
                    await Task.Run(() => host.Connect("127.0.0.1", room.Port, room.Code, "Другой ведущий", room.Secret));
                    await Connect("127.0.0.1", room.Port, room.Code, "");
                    await Until(() => Me != null && state.players.Count == 2 && state.code == room.Code, "Не удалось перейти в другую комнату.");
                    LobbyRenderingCheck(!IsHost && !state.pack.Selected && !Object.ReferenceEquals(previousRows[0], LobbyTestRows()[0]) && !LobbyTestMessages().Any(message => previousMessages.Contains(message)), "Новая комната использовала старые строки или сообщения.");
                    LobbyRenderingCheck(LobbyTestName(LobbyTestRows()[1]).Text.EndsWith(" · ты") && !LobbyTestName(LobbyTestRows()[0]).Text.EndsWith(" · ты") && LobbyTestRows().Take(2).All(row => ((StackPanel)row.Children[2]).Children.OfType<Button>().Count() == 0), "После перехода неверно показаны свой игрок или права ведущего.");
                    previousRows = LobbyTestRows(); previousMessages = LobbyTestMessages();
                    Disconnect(); await Connect("127.0.0.1", room.Port, room.Code, "");
                    await Until(() => Me != null && state.players.Count == 2 && state.code == room.Code, "Не удалось переподключиться к той же комнате.");
                    LobbyRenderingCheck(!Object.ReferenceEquals(previousRows[0], LobbyTestRows()[0]) && !LobbyTestMessages().Any(message => previousMessages.Contains(message)), "Переподключение с тем же кодом не сбросило старое представление.");
                    Disconnect();
                }
                report.Add("PASS: switching rooms and reconnecting to the same room reset retained views, chat, own-player labels and host-only actions");
            }
            catch (Exception error) { report.Add("FAIL: " + error); }
            finally
            {
                guest.Dispose(); other.Dispose(); steamAvatars.Remove(avatarId); avatarVersion++;
                File.WriteAllLines(Path.Combine(Paths.Base, "lobby-rendering-ui-test.txt"), report); window.Close();
            }
        }
    }
}

using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace BrokenPhone
{
    public sealed partial class GameWindow
    {
        bool joiningRoom;
        bool normalizingJoinCode;
        string acceptedJoinCode = "";
        const string InvalidJoinCode = "Введи код комнаты из 6 символов.";
        void InitializeJoinDialog()
        {
            B("JoinRoomClose").Click += delegate { CloseJoinDialog(); };
            B("JoinRoomSubmit").Click += async delegate { await SubmitJoinDialog(); };
            B("JoinRoomPaste").Click += delegate
            {
                try { SetJoinCode(Clipboard.GetText()); TBox("JoinRoomInput").Focus(); TBox("JoinRoomInput").CaretIndex = TBox("JoinRoomInput").Text.Length; }
                catch (ExternalException) { JoinStatus("Не удалось прочитать буфер обмена. Попробуй ввести код вручную.", true); }
            };
            var input = TBox("JoinRoomInput");
            input.PreviewTextInput += delegate(object sender, System.Windows.Input.TextCompositionEventArgs e) { e.Handled = !SteamBridge.CodeCharacters(e.Text); };
            DataObject.AddPastingHandler(input, delegate(object sender, DataObjectPastingEventArgs e)
            {
                string value = e.DataObject.GetData(DataFormats.UnicodeText) as string;
                if (value == null || !SteamBridge.ValidCode(value.Trim())) { e.CancelCommand(); JoinStatus(InvalidJoinCode, true); }
                else e.DataObject = new DataObject(DataFormats.UnicodeText, value.Trim().ToUpperInvariant());
            });
            input.TextChanged += delegate { if (!joiningRoom && !normalizingJoinCode) SetJoinCode(input.Text); };
        }
        bool SetJoinCode(string value)
        {
            value = (value ?? "").Trim().ToUpperInvariant(); bool valid = value.Length <= 6 && SteamBridge.CodeCharacters(value);
            normalizingJoinCode = true;
            try { if (valid) acceptedJoinCode = value; TBox("JoinRoomInput").Text = acceptedJoinCode; }
            finally { normalizingJoinCode = false; }
            JoinStatus(valid ? "" : InvalidJoinCode, !valid); UpdateJoinControls(); return valid;
        }
        public async Task OpenSteamInvite(ulong lobby)
        {
            if (joiningRoom || state != null && state.phase != "lobby") return;
            string code = await steam.CodeForLobby(lobby);
            if (disposed || state != null && state.phase != "lobby") return;
            OpenJoinDialog(); SetJoinCode(code);
        }
        void OpenJoinDialog()
        {
            if (joiningRoom || state != null && state.phase != "lobby") return;
            C<Border>("JoinOverlay").Visibility = Visibility.Visible; C<Grid>("GameShell").IsEnabled = false;
            JoinStatus("", false); UpdateJoinControls();
            TBox("JoinRoomInput").Focus(); TBox("JoinRoomInput").SelectAll();
        }
        void CloseJoinDialog()
        {
            if (joiningRoom) return;
            C<Border>("JoinOverlay").Visibility = Visibility.Collapsed; C<Grid>("GameShell").IsEnabled = true; B("JoinButton").Focus();
        }
        void JoinStatus(string message, bool error)
        {
            Text("JoinRoomStatus", message); C<TextBlock>("JoinRoomStatus").Foreground = Brush(error ? "#E8B2CD" : "#BEB3C8");
            C<Border>("JoinRoomStatusPanel").Visibility = String.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        }
        void UpdateJoinControls()
        {
            TBox("JoinRoomInput").IsEnabled = B("JoinRoomPaste").IsEnabled = B("JoinRoomClose").IsEnabled = !joiningRoom;
            B("JoinRoomSubmit").IsEnabled = !joiningRoom && SteamBridge.ValidCode(TBox("JoinRoomInput").Text);
            B("JoinRoomSubmit").Content = joiningRoom ? "Входим в комнату…" : "Войти в комнату  →";
        }
        async Task SubmitJoinDialog()
        {
            if (joiningRoom || C<Border>("JoinOverlay").Visibility != Visibility.Visible) return;
            string code = TBox("JoinRoomInput").Text;
            if (!SteamBridge.ValidCode(code)) { JoinStatus(InvalidJoinCode, true); TBox("JoinRoomInput").Focus(); return; }
            if (!steam.Available) { JoinStatus("Запусти Steam и подключи его во вкладке «Игрок и звук» в настройках игры.", true); return; }
            if (String.Equals(code, steam.Code, StringComparison.OrdinalIgnoreCase)) { JoinStatus("Ты уже в этой комнате.", true); return; }
            joiningRoom = true; JoinStatus("Ищем комнату…", false); UpdateJoinControls();
            try
            {
                ulong lobby = await steam.FindLobbyByCode(code);
                if (lobby == steam.LobbyId) { JoinStatus("Ты уже в этой комнате.", true); return; }
                JoinStatus("Подключаемся к комнате…", false);
                await JoinSteam(lobby); joiningRoom = false; CloseJoinDialog();
            }
            catch (Exception error) { JoinStatus(error.GetBaseException().Message, true); }
            finally { joiningRoom = false; UpdateJoinControls(); }
        }
    }
}

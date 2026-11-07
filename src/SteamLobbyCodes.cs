using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace BrokenPhone
{
    public sealed partial class SteamBridge
    {
        readonly SemaphoreSlim lobbySearch = new SemaphoreSlim(1, 1);
        public static bool CodeCharacters(string text)
        {
            foreach (char value in text ?? "") if (!(value >= '0' && value <= '9') && !(value >= 'A' && value <= 'F') && !(value >= 'a' && value <= 'f')) return false;
            return text != null;
        }
        public static bool ValidCode(string text) { return text != null && text.Length == 6 && CodeCharacters(text); }
        string LobbyData(ulong lobby, string key) { return SteamNative.Utf8(SteamNative.SteamAPI_ISteamMatchmaking_GetLobbyData(matchmaking, lobby, key)); }
        bool MatchesCode(ulong lobby, string code)
        {
            return LobbyData(lobby, "bp_game") == "broken-phone" && LobbyData(lobby, "bp_protocol") == Playback.Protocol.ToString() && String.Equals(LobbyData(lobby, "bp_code"), code, StringComparison.OrdinalIgnoreCase);
        }
        public async Task<ulong> FindLobbyByCode(string code)
        {
            if (!ValidCode(code)) throw new ArgumentException("Введи код комнаты: 6 символов, цифры и буквы A–F.");
            code = code.ToUpperInvariant(); await lobbySearch.WaitAsync();
            try
            {
                int generation = roomGeneration;
                byte[] result = await Call(() =>
                {
                    SteamNative.SteamAPI_ISteamMatchmaking_AddRequestLobbyListStringFilter(matchmaking, "bp_game", "broken-phone", 0);
                    SteamNative.SteamAPI_ISteamMatchmaking_AddRequestLobbyListStringFilter(matchmaking, "bp_protocol", Playback.Protocol.ToString(), 0);
                    SteamNative.SteamAPI_ISteamMatchmaking_AddRequestLobbyListStringFilter(matchmaking, "bp_code", code, 0);
                    SteamNative.SteamAPI_ISteamMatchmaking_AddRequestLobbyListDistanceFilter(matchmaking, 3);
                    SteamNative.SteamAPI_ISteamMatchmaking_AddRequestLobbyListResultCountFilter(matchmaking, 10);
                    return SteamNative.SteamAPI_ISteamMatchmaking_RequestLobbyList(matchmaking);
                }, 510);
                var matches = new List<ulong>();
                lock (Gate)
                {
                    if (generation != roomGeneration || disposed) throw new OperationCanceledException();
                    uint count = BitConverter.ToUInt32(result, 0);
                    for (int i = 0; i < Math.Min(count, 10); i++)
                    {
                        ulong lobby = SteamNative.SteamAPI_ISteamMatchmaking_GetLobbyByIndex(matchmaking, i);
                        if (lobby != 0 && MatchesCode(lobby, code)) matches.Add(lobby);
                    }
                }
                if (matches.Count == 0) throw new IOException("Комната не найдена. Проверь код; возможно, игра уже началась или комната заполнена.");
                if (matches.Count > 1) throw new IOException("Код совпал у нескольких комнат. Попроси ведущего создать новую комнату.");
                return matches[0];
            }
            finally { lobbySearch.Release(); }
        }
        public async Task<string> CodeForLobby(ulong lobby)
        {
            lock (Gate) { if (!Available || disposed || !SteamNative.SteamAPI_ISteamMatchmaking_RequestLobbyData(matchmaking, lobby)) throw new IOException("Комната Steam недоступна."); }
            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                lock (Gate)
                {
                    if (disposed) throw new OperationCanceledException();
                    string code = LobbyData(lobby, "bp_code");
                    if (ValidCode(code) && MatchesCode(lobby, code)) return code.ToUpperInvariant();
                }
                await Task.Delay(100);
            }
            throw new IOException("Не удалось получить код комнаты. Попроси ведущего отправить код.");
        }
    }
}

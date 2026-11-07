using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BrokenPhone
{
    public sealed class SteamFriend { public ulong Id; public string Name; }
    public sealed partial class SteamBridge : IDisposable
    {
        sealed class Pending { public int Callback; public TaskCompletionSource<byte[]> Result = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously); }
        internal readonly object Gate = new object();
        readonly Dictionary<ulong, Pending> pending = new Dictionary<ulong, Pending>();
        IntPtr matchmaking, friends, networking;
        int pipe, roomGeneration;
        Task worker;
        volatile bool disposed;
        public bool Available { get; private set; }
        public bool OverlayActive { get; private set; }
        public string Status { get; private set; }
        public uint AppId { get; private set; }
        public ulong UserId { get; private set; }
        public ulong LobbyId { get; private set; }
        public ulong OwnerId { get; private set; }
        public string Name { get; private set; }
        public string RefreshName()
        {
            lock (Gate)
            {
                if (Available && !disposed)
                {
                    string current = SteamNative.Utf8(SteamNative.SteamAPI_ISteamFriends_GetPersonaName(friends));
                    if (!String.IsNullOrWhiteSpace(current)) Name = current;
                }
                return Name;
            }
        }
        public string Code { get; private set; }
        public bool Hosting { get; private set; }
        int hostPort;
        internal int sentPackets, receivedPackets, rejectedPackets, sessionRequests;
        internal string transportError = "";
        internal string TransportDetails { get { lock (Gate) return "sent=" + sentPackets + ", received=" + receivedPackets + ", rejected=" + rejectedPackets + ", requests=" + sessionRequests + ", tunnels=" + tunnels.Count + ", error=" + transportError; } }
        public event Action<ulong> JoinRequested;
        public event Action<bool> OverlayChanged;
        public SteamBridge(bool enable)
        {
            Status = "Steam не подключён"; if (!enable) return;
            bool initialized = false;
            try
            {
                var error = new StringBuilder(1024);
                int result = SteamNative.SteamAPI_InitFlat(error);
                if (result != 0) { Status = "Steam недоступен. Запусти Steam и нажми «Подключить Steam»."; return; }
                initialized = true;
                var user = SteamNative.SteamAPI_SteamUser_v023();
                if (!SteamNative.SteamAPI_ISteamUser_BLoggedOn(user)) throw new IOException("Войди в свой профиль Steam.");
                friends = SteamNative.SteamAPI_SteamFriends_v018(); matchmaking = SteamNative.SteamAPI_SteamMatchmaking_v009(); networking = SteamNative.SteamAPI_SteamNetworkingSockets_SteamAPI_v012();
                if (friends == IntPtr.Zero || matchmaking == IntPtr.Zero || networking == IntPtr.Zero) throw new IOException("Версия Steam API не поддерживается.");
                AppId = SteamNative.SteamAPI_ISteamUtils_GetAppID(SteamNative.SteamAPI_SteamUtils_v010());
                UserId = SteamNative.SteamAPI_ISteamUser_GetSteamID(user); Name = SteamNative.Utf8(SteamNative.SteamAPI_ISteamFriends_GetPersonaName(friends));
                SteamNative.SteamAPI_ISteamNetworkingSockets_InitAuthentication(networking);
                SteamNative.SteamAPI_ISteamNetworkingUtils_InitRelayNetworkAccess(SteamNative.SteamAPI_SteamNetworkingUtils_SteamAPI_v004());
                SteamNative.SteamAPI_ManualDispatch_Init(); pipe = SteamNative.SteamAPI_GetHSteamPipe(); Available = true;
                Status = "Steam подключён · " + (AppId == 480 ? "тестовый Spacewar (480)" : "App ID " + AppId);
                worker = Background(Poll);
            }
            catch (Exception e) { if (initialized) SteamNative.SteamAPI_Shutdown(); Status = "Steam недоступен: " + e.Message; }
        }
        static Task Background(Action action) { return Task.Factory.StartNew(action, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default); }
        async Task<byte[]> Call(Func<ulong> begin, int callback)
        {
            var request = new Pending { Callback = callback }; ulong id;
            lock (Gate) { if (!Available || disposed) throw new IOException(Status); id = begin(); if (id == 0) throw new IOException("Steam не принял запрос."); pending.Add(id, request); }
            try
            {
                if (await Task.WhenAny(request.Result.Task, Task.Delay(20000)) != request.Result.Task) throw new TimeoutException("Steam не ответил за 20 секунд.");
                return await request.Result.Task;
            }
            finally { lock (Gate) pending.Remove(id); }
        }
        void Poll()
        {
            while (!disposed)
            {
                try
                {
                    lock (Gate)
                    {
                        if (disposed) break; SteamNative.SteamAPI_ManualDispatch_RunFrame(pipe); SteamNative.Callback callback;
                        while (SteamNative.SteamAPI_ManualDispatch_GetNextCallback(pipe, out callback))
                        {
                            try { Dispatch(callback); } finally { SteamNative.SteamAPI_ManualDispatch_FreeLastCallback(pipe); }
                        }
                        ReceiveSocketPackets();
                    }
                }
                catch (Exception e) { Status = "Ошибка соединения Steam: " + e.Message; CloseTunnels(); }
                Thread.Sleep(8);
            }
        }
        void Dispatch(SteamNative.Callback callback)
        {
            if (callback.Id == 703 && callback.Size >= 16)
            {
                ulong id = unchecked((ulong)Marshal.ReadInt64(callback.Data)); Pending request;
                if (!pending.TryGetValue(id, out request)) return;
                int expected = Marshal.ReadInt32(callback.Data, 8), size = Marshal.ReadInt32(callback.Data, 12);
                if (expected != request.Callback || size < 1 || size > 16384) { request.Result.TrySetException(new IOException("Steam вернул неожиданный ответ.")); return; }
                IntPtr memory = Marshal.AllocHGlobal(size);
                try
                {
                    bool failed;
                    if (!SteamNative.SteamAPI_ManualDispatch_GetAPICallResult(pipe, id, memory, size, expected, out failed) || failed) request.Result.TrySetException(new IOException("Запрос Steam завершился ошибкой."));
                    else { var bytes = new byte[size]; Marshal.Copy(memory, bytes, 0, size); request.Result.TrySetResult(bytes); }
                }
                finally { Marshal.FreeHGlobal(memory); }
            }
            else if (callback.Id == 333 && callback.Size >= 16) { var handler = JoinRequested; if (handler != null) handler(unchecked((ulong)Marshal.ReadInt64(callback.Data))); }
            else if (callback.Id == 331 && callback.Size >= 1) { OverlayActive = Marshal.ReadByte(callback.Data) != 0; var handler = OverlayChanged; if (handler != null) handler(OverlayActive); }
            else if (callback.Id == 1221 && callback.Size >= 708) SocketStatus(callback.Data);
            else if (callback.Id == 3406 && callback.Size >= 20) WorkshopDownloaded(callback.Data);
        }

        bool AllowedPeer(ulong id)
        {
            if (LobbyId == 0 || id == 0) return false;
            if (!Hosting) return id == OwnerId;
            int count = SteamNative.SteamAPI_ISteamMatchmaking_GetNumLobbyMembers(matchmaking, LobbyId);
            for (int i = 0; i < count; i++) if (SteamNative.SteamAPI_ISteamMatchmaking_GetLobbyMemberByIndex(matchmaking, LobbyId, i) == id) return true;
            return false;
        }
        public async Task HostRoom(string code, int port)
        {
            LeaveRoom(); int generation = roomGeneration;
            byte[] result = await Call(() => SteamNative.SteamAPI_ISteamMatchmaking_CreateLobby(matchmaking, 2, 8), 513);
            int status = BitConverter.ToInt32(result, 0); ulong lobby = BitConverter.ToUInt64(result, 8);
            if (status != 1 || lobby == 0) throw new IOException("Steam не создал комнату (код " + status + ").");
            lock (Gate)
            {
                if (generation != roomGeneration) { SteamNative.SteamAPI_ISteamMatchmaking_LeaveLobby(matchmaking, lobby); throw new OperationCanceledException(); }
                LobbyId = lobby; OwnerId = UserId; Code = code; hostPort = port; Hosting = true;
                steamListener = SteamNative.SteamAPI_ISteamNetworkingSockets_CreateListenSocketP2P(networking, 5, 0, IntPtr.Zero);
                if (steamListener == 0) { LeaveRoom(); throw new IOException("Steam не открыл сетевой канал комнаты."); }
                if (!SetData("bp_game", "broken-phone") || !SetData("bp_protocol", Playback.Protocol.ToString()) || !SetData("bp_code", code)) { LeaveRoom(); throw new IOException("Не удалось опубликовать данные комнаты Steam."); }
            }
        }
        bool SetData(string key, string value) { return SteamNative.SteamAPI_ISteamMatchmaking_SetLobbyData(matchmaking, LobbyId, key, value); }
        string GetData(string key) { return SteamNative.Utf8(SteamNative.SteamAPI_ISteamMatchmaking_GetLobbyData(matchmaking, LobbyId, key)); }
        public async Task<int> JoinRoom(ulong lobby)
        {
            LeaveRoom(); int generation = roomGeneration;
            byte[] result = await Call(() => SteamNative.SteamAPI_ISteamMatchmaking_JoinLobby(matchmaking, lobby), 504);
            if (BitConverter.ToUInt32(result, 16) != 1) throw new IOException("Комната Steam недоступна или заполнена.");
            lock (Gate)
            {
                if (generation != roomGeneration) { SteamNative.SteamAPI_ISteamMatchmaking_LeaveLobby(matchmaking, lobby); throw new OperationCanceledException(); }
                LobbyId = lobby;
                if (GetData("bp_game") != "broken-phone" || GetData("bp_protocol") != Playback.Protocol.ToString()) { LeaveRoom(); throw new IOException("Это другая игра или другая версия Broken Phone."); }
                OwnerId = SteamNative.SteamAPI_ISteamMatchmaking_GetLobbyOwner(matchmaking, lobby); Code = GetData("bp_code");
                if (Code.Length != 6 || OwnerId == UserId) { LeaveRoom(); throw new IOException("Нельзя подключиться к собственной или повреждённой комнате."); }
                Hosting = false; return OpenTunnel(OwnerId);
            }
        }
        public void SetJoinable(bool value) { lock (Gate) if (Available && Hosting && LobbyId != 0) SteamNative.SteamAPI_ISteamMatchmaking_SetLobbyJoinable(matchmaking, LobbyId, value); }
        public List<SteamFriend> Friends()
        {
            lock (Gate)
            {
                var result = new List<SteamFriend>(); if (!Available) return result;
                int count = SteamNative.SteamAPI_ISteamFriends_GetFriendCount(friends, 4);
                for (int i = 0; i < Math.Min(count, 500); i++)
                {
                    ulong id = SteamNative.SteamAPI_ISteamFriends_GetFriendByIndex(friends, i, 4);
                    if (SteamNative.SteamAPI_ISteamFriends_GetFriendPersonaState(friends, id) != 0) result.Add(new SteamFriend { Id = id, Name = SteamNative.Utf8(SteamNative.SteamAPI_ISteamFriends_GetFriendPersonaName(friends, id)) });
                }
                return result.OrderBy(f => f.Name).ToList();
            }
        }
        public bool Invite(ulong friend) { lock (Gate) return Available && LobbyId != 0 && SteamNative.SteamAPI_ISteamMatchmaking_InviteUserToLobby(matchmaking, LobbyId, friend); }
        public void LeaveRoom()
        {
            lock (Gate)
            {
                roomGeneration++; CloseTunnels(); if (Available && LobbyId != 0) SteamNative.SteamAPI_ISteamMatchmaking_LeaveLobby(matchmaking, LobbyId);
                LobbyId = OwnerId = 0; Hosting = false; Code = ""; hostPort = 0;
            }
        }
        public void Dispose()
        {
            disposed = true; LeaveRoom(); if (worker != null) worker.Wait(2000);
            lock (Gate) { foreach (var request in pending.Values) request.Result.TrySetException(new IOException("Steam закрыт.")); pending.Clear(); foreach (var request in workshopRequests.Values) request.TrySetException(new IOException("Steam закрыт.")); workshopRequests.Clear(); if (Available) SteamNative.SteamAPI_Shutdown(); Available = false; }
        }
    }
}

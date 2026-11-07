using System;
using System.Runtime.InteropServices;
using System.Text;

namespace BrokenPhone
{
    internal static class SteamNative
    {
        const string Dll = "steam_api64.dll";
        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        internal struct Callback { public int User, Id; public IntPtr Data; public int Size; }
        [StructLayout(LayoutKind.Explicit, Size = 136)]
        internal struct Identity { [FieldOffset(0)] public int Type; [FieldOffset(4)] public int Size; [FieldOffset(8)] public ulong SteamId; }
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int SteamAPI_InitFlat(StringBuilder error);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void SteamAPI_Shutdown();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int SteamAPI_GetHSteamPipe();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void SteamAPI_ManualDispatch_Init();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void SteamAPI_ManualDispatch_RunFrame(int pipe);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool SteamAPI_ManualDispatch_GetNextCallback(int pipe, out Callback callback);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void SteamAPI_ManualDispatch_FreeLastCallback(int pipe);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool SteamAPI_ManualDispatch_GetAPICallResult(int pipe, ulong call, IntPtr result, int size, int expectedCallback, [MarshalAs(UnmanagedType.I1)] out bool failed);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr SteamAPI_SteamUser_v023();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr SteamAPI_SteamFriends_v018();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr SteamAPI_SteamUtils_v010();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr SteamAPI_SteamUGC_v021();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr SteamAPI_SteamApps_v008();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool SteamAPI_ISteamApps_BIsSubscribedApp(IntPtr self, uint app);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool SteamAPI_ISteamUtils_IsOverlayEnabled(IntPtr self);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool SteamAPI_ISteamUtils_BOverlayNeedsPresent(IntPtr self);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void SteamAPI_ISteamFriends_ActivateGameOverlayInviteDialog(IntPtr self, ulong lobby);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)] internal static extern void SteamAPI_ISteamFriends_ActivateGameOverlay(IntPtr self, string dialog);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool SteamAPI_ISteamUGC_DownloadItem(IntPtr self, ulong item, [MarshalAs(UnmanagedType.I1)] bool highPriority);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern uint SteamAPI_ISteamUGC_GetItemState(IntPtr self, ulong item);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool SteamAPI_ISteamUGC_GetItemDownloadInfo(IntPtr self, ulong item, out ulong downloaded, out ulong total);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool SteamAPI_ISteamUGC_GetItemInstallInfo(IntPtr self, ulong item, out ulong size, [Out] byte[] folder, uint folderSize, out uint timestamp);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr SteamAPI_SteamMatchmaking_v009();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr SteamAPI_SteamNetworkingSockets_SteamAPI_v012();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr SteamAPI_SteamNetworkingUtils_SteamAPI_v004();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void SteamAPI_ISteamNetworkingUtils_InitRelayNetworkAccess(IntPtr self);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int SteamAPI_ISteamNetworkingSockets_InitAuthentication(IntPtr self);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern uint SteamAPI_ISteamNetworkingSockets_CreateListenSocketP2P(IntPtr self, int port, int options, IntPtr values);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern uint SteamAPI_ISteamNetworkingSockets_ConnectP2P(IntPtr self, ref Identity identity, int port, int options, IntPtr values);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int SteamAPI_ISteamNetworkingSockets_AcceptConnection(IntPtr self, uint connection);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool SteamAPI_ISteamNetworkingSockets_CloseConnection(IntPtr self, uint connection, int reason, string text, [MarshalAs(UnmanagedType.I1)] bool linger);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool SteamAPI_ISteamNetworkingSockets_CloseListenSocket(IntPtr self, uint listener);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int SteamAPI_ISteamNetworkingSockets_SendMessageToConnection(IntPtr self, uint connection, byte[] data, uint size, int flags, out long number);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int SteamAPI_ISteamNetworkingSockets_ReceiveMessagesOnConnection(IntPtr self, uint connection, [Out] IntPtr[] messages, int maximum);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern ulong SteamAPI_ISteamUser_GetSteamID(IntPtr self);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool SteamAPI_ISteamUser_BLoggedOn(IntPtr self);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern uint SteamAPI_ISteamUtils_GetAppID(IntPtr self);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr SteamAPI_ISteamFriends_GetPersonaName(IntPtr self);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int SteamAPI_ISteamFriends_GetLargeFriendAvatar(IntPtr self, ulong id);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool SteamAPI_ISteamFriends_RequestUserInformation(IntPtr self, ulong id, [MarshalAs(UnmanagedType.I1)] bool nameOnly);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool SteamAPI_ISteamUtils_GetImageSize(IntPtr self, int image, out uint width, out uint height);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool SteamAPI_ISteamUtils_GetImageRGBA(IntPtr self, int image, [Out] byte[] destination, int bytes);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int SteamAPI_ISteamFriends_GetFriendCount(IntPtr self, int flags);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern ulong SteamAPI_ISteamFriends_GetFriendByIndex(IntPtr self, int index, int flags);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr SteamAPI_ISteamFriends_GetFriendPersonaName(IntPtr self, ulong id);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int SteamAPI_ISteamFriends_GetFriendPersonaState(IntPtr self, ulong id);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern ulong SteamAPI_ISteamMatchmaking_CreateLobby(IntPtr self, int type, int maximum);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern ulong SteamAPI_ISteamMatchmaking_JoinLobby(IntPtr self, ulong lobby);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern ulong SteamAPI_ISteamMatchmaking_RequestLobbyList(IntPtr self);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)] internal static extern void SteamAPI_ISteamMatchmaking_AddRequestLobbyListStringFilter(IntPtr self, string key, string value, int comparison);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void SteamAPI_ISteamMatchmaking_AddRequestLobbyListDistanceFilter(IntPtr self, int distance);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void SteamAPI_ISteamMatchmaking_AddRequestLobbyListResultCountFilter(IntPtr self, int maximum);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern ulong SteamAPI_ISteamMatchmaking_GetLobbyByIndex(IntPtr self, int index);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool SteamAPI_ISteamMatchmaking_RequestLobbyData(IntPtr self, ulong lobby);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void SteamAPI_ISteamMatchmaking_LeaveLobby(IntPtr self, ulong lobby);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool SteamAPI_ISteamMatchmaking_SetLobbyData(IntPtr self, ulong lobby, string key, string value);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)] internal static extern IntPtr SteamAPI_ISteamMatchmaking_GetLobbyData(IntPtr self, ulong lobby, string key);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern ulong SteamAPI_ISteamMatchmaking_GetLobbyOwner(IntPtr self, ulong lobby);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int SteamAPI_ISteamMatchmaking_GetNumLobbyMembers(IntPtr self, ulong lobby);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern ulong SteamAPI_ISteamMatchmaking_GetLobbyMemberByIndex(IntPtr self, ulong lobby, int index);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool SteamAPI_ISteamMatchmaking_SetLobbyJoinable(IntPtr self, ulong lobby, [MarshalAs(UnmanagedType.I1)] bool joinable);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool SteamAPI_ISteamMatchmaking_InviteUserToLobby(IntPtr self, ulong lobby, ulong friend);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern ulong SteamAPI_SteamNetworkingIdentity_GetSteamID64(IntPtr identity);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void SteamAPI_SteamNetworkingMessage_t_Release(IntPtr message);
        internal static string Utf8(IntPtr pointer)
        {
            if (pointer == IntPtr.Zero) return ""; int length = 0; while (length < 16384 && Marshal.ReadByte(pointer, length) != 0) length++;
            var bytes = new byte[length]; Marshal.Copy(pointer, bytes, 0, length); return Encoding.UTF8.GetString(bytes);
        }
    }
}

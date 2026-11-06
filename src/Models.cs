using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

namespace BrokenPhone
{
    public static class Json
    {
        public static string Write(object value) { return new JavaScriptSerializer { MaxJsonLength = 24 * 1024 * 1024 }.Serialize(value); }
        public static T Read<T>(string value) { return new JavaScriptSerializer { MaxJsonLength = 24 * 1024 * 1024 }.Deserialize<T>(value); }
    }

    public static class Paths
    {
        public static readonly string Base = AppDomain.CurrentDomain.BaseDirectory;
        public static readonly string Data = Path.Combine(Base, ".runtime");
        public static readonly string Packs = Path.Combine(Base, "Packs");
        public static readonly string Steam = Path.Combine(Data, "SteamCMD");
        public static readonly string Sessions = Path.Combine(Data, "sessions");
        public static readonly string Cache = Path.Combine(Data, "cache");
        public static readonly string Exports = Path.Combine(Base, "exports");
        public static void Prepare() { foreach (var p in new[] { Data, Packs, Sessions, Cache, Exports }) Directory.CreateDirectory(p); }
    }

    public sealed class Line
    {
        public double start { get; set; }
        public double end { get; set; }
        public string text { get; set; }
        public string role { get; set; }
        public string sourceKey { get; set; }
        public bool originalAvailable { get; set; }
    }
    public sealed class SourceCue
    {
        public string key { get; set; }
        public string file { get; set; }
        public double start { get; set; }
        public double end { get; set; }
    }

    public sealed class Pack
    {
        public bool isTestPack { get; set; }
        public bool Selected { get { return lines.Count > 0; } }
        public string id { get; set; }
        public string title { get; set; }
        public string description { get; set; }
        public string language { get; set; }
        public string workshopId { get; set; }
        public string preview { get; set; }
        public string thumbnail { get; set; }
        public int lineCount { get; set; }
        public int roleCount { get; set; }
        public string video { get; set; }
        public string root { get; set; }
        public string background { get; set; }
        public string original { get; set; }
        public string contentKey { get; set; }
        public string videoHash { get; set; }
        public string backgroundHash { get; set; }
        public long videoBytes { get; set; }
        public long backgroundBytes { get; set; }
        public string sourceRoot { get; set; }
        public string sourceHash { get; set; }
        public string sourceVideo { get; set; }
        public string sourceBackground { get; set; }
        public string preparedVideoHash { get; set; }
        public string preparedSourceHash { get; set; }
        public List<PackSourceFile> sourceFiles { get; set; }
        public List<SourceCue> sourceCues { get; set; }
        public bool generatedTiming { get; set; }
        public bool mergedTiming { get; set; }
        public List<Line> lines { get; set; }
        public Pack() { lines = new List<Line>(); sourceCues = new List<SourceCue>(); sourceFiles = new List<PackSourceFile>(); language = "Russian"; }
        public int DisplayLines { get { return lineCount > 0 ? lineCount : lines.Count; } }
        public int DisplayRoles { get { return roleCount > 0 ? roleCount : lines.Select(l => l.role).Where(r => !String.IsNullOrWhiteSpace(r)).Distinct().Count(); } }
        public Pack PublicCopy()
        {
            var copy = Json.Read<Pack>(Json.Write(this));
            copy.video = "video.mp4";
            copy.root = "";
            copy.original = ""; copy.sourceCues = new List<SourceCue>();
            copy.sourceRoot = ""; copy.preparedVideoHash = ""; copy.preparedSourceHash = "";
            copy.background = String.IsNullOrEmpty(background) ? "" : "background.wav";
            return copy;
        }
    }

    public sealed class Player
    {
        public string id { get; set; }
        public ulong steamId { get; set; }
        public string owner { get; set; }
        public string name { get; set; }
        public bool ready { get; set; }
        public bool host { get; set; }
        public int color { get; set; }
        public bool prepared { get; set; }
        public double downloadProgress { get; set; }
        public string loadStatus { get; set; }
    }

    public sealed class ChatLine
    {
        public string name { get; set; }
        public string text { get; set; }
        public string time { get; set; }
    }

    public sealed class RoomState
    {
        public string code { get; set; }
        public string phase { get; set; }
        public string round { get; set; }
        public List<Player> players { get; set; }
        public List<ChatLine> chat { get; set; }
        public Pack pack { get; set; }
        public string roleMode { get; set; }
        public int roleVersion { get; set; }
        public List<RoleAssignment> roles { get; set; }
        public int turn { get; set; }
        public bool heard { get; set; }
        public bool performing { get; set; }
        public string[] order { get; set; }
        public string[] resultReady { get; set; }
        public int playbackVersion { get; set; }
        public bool playing { get; set; }
        public double playbackPosition { get; set; }
        public long playbackAt { get; set; }
        public long serverTime { get; set; }
        public double resultDuration { get; set; }
        public string resultMovieKind { get; set; }
        public RoomState() { phase = "lobby"; players = new List<Player>(); chat = new List<ChatLine>(); order = new string[0]; resultReady = new string[0]; roleMode = "manual"; roles = new List<RoleAssignment>(); }
        public Player Active { get { return turn < order.Length ? players.FirstOrDefault(p => p.id == order[turn]) : null; } }
        public RoomState ForPacket(bool includePack) { var copy = (RoomState)MemberwiseClone(); if (!includePack) copy.pack = null; return copy; }
    }

    public sealed class Message
    {
        internal int wireBytes;
        public string kind { get; set; }
        public ulong steamId { get; set; }
        public string id { get; set; }
        public string text { get; set; }
        public string code { get; set; }
        public string secret { get; set; }
        public string data { get; set; }
        public string asset { get; set; }
        public string round { get; set; }
        public int index { get; set; }
        public bool flag { get; set; }
        public int protocol { get; set; }
        public long clientTime { get; set; }
        public long serverTime { get; set; }
        public double position { get; set; }
        public long bytes { get; set; }
        public long totalBytes { get; set; }
        public Pack pack { get; set; }
        public RoomState state { get; set; }
    }
}

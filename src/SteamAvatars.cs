using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BrokenPhone
{
    public sealed class SteamAvatar
    {
        public int Handle, Width, Height;
        public byte[] Rgba;
    }

    public sealed partial class SteamBridge
    {
        readonly HashSet<ulong> requestedAvatars = new HashSet<ulong>();
        readonly Dictionary<int, SteamAvatar> avatarPixels = new Dictionary<int, SteamAvatar>();

        public SteamAvatar GetAvatar(ulong id)
        {
            lock (Gate)
            {
                if (!Available || disposed || id == 0) return null;
                // Request avatar data as well as the name, including for non-friends in a room.
                if (requestedAvatars.Add(id)) SteamNative.SteamAPI_ISteamFriends_RequestUserInformation(friends, id, false);
                int image = SteamNative.SteamAPI_ISteamFriends_GetLargeFriendAvatar(friends, id);
                if (image <= 0) return null; // -1 means Steam is still fetching the image.
                SteamAvatar cached; if (avatarPixels.TryGetValue(image, out cached)) return cached;
                uint width, height; var utils = SteamNative.SteamAPI_SteamUtils_v010();
                if (!SteamNative.SteamAPI_ISteamUtils_GetImageSize(utils, image, out width, out height) || width == 0 || height == 0 || width > 256 || height > 256) return null;
                var pixels = new byte[checked((int)(width * height * 4))];
                if (!SteamNative.SteamAPI_ISteamUtils_GetImageRGBA(utils, image, pixels, pixels.Length)) return null;
                cached = new SteamAvatar { Handle = image, Width = (int)width, Height = (int)height, Rgba = pixels };
                if (avatarPixels.Count >= 128) avatarPixels.Clear();
                avatarPixels[image] = cached; return cached;
            }
        }
    }

    public sealed partial class GameWindow
    {
        sealed class AvatarBitmap { public SteamAvatar Data; public BitmapSource Image; }
        readonly Dictionary<ulong, AvatarBitmap> steamAvatars = new Dictionary<ulong, AvatarBitmap>();
        DateTime nextAvatarCheck;
        int avatarVersion;

        static BitmapSource AvatarBitmapSource(SteamAvatar avatar)
        {
            var pixels = new byte[avatar.Rgba.Length];
            for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = avatar.Rgba[i + 2]; pixels[i + 1] = avatar.Rgba[i + 1]; pixels[i + 2] = avatar.Rgba[i]; pixels[i + 3] = avatar.Rgba[i + 3]; }
            var bitmap = BitmapSource.Create(avatar.Width, avatar.Height, 96, 96, PixelFormats.Bgra32, null, pixels, avatar.Width * 4); bitmap.Freeze(); return bitmap;
        }
        void RefreshSteamAvatars()
        {
            if (steam == null || !steam.Available || state == null || DateTime.UtcNow < nextAvatarCheck) return;
            nextAvatarCheck = DateTime.UtcNow.AddSeconds(1); bool changed = false;
            var ids = state.players.Where(player => player.steamId != 0).Select(player => player.steamId).Distinct().ToArray();
            foreach (ulong removed in steamAvatars.Keys.Where(id => !ids.Contains(id)).ToArray()) { steamAvatars.Remove(removed); changed = true; }
            foreach (ulong id in ids)
            {
                var avatar = steam.GetAvatar(id); AvatarBitmap existing;
                if (avatar == null || steamAvatars.TryGetValue(id, out existing) && Object.ReferenceEquals(existing.Data, avatar)) continue;
                steamAvatars[id] = new AvatarBitmap { Data = avatar, Image = AvatarBitmapSource(avatar) }; changed = true;
            }
            if (changed) { avatarVersion++; RenderLobby(); }
        }
        Border Avatar(Player player, int size)
        {
            var avatar = Avatar(player.color, size); AvatarBitmap bitmap;
            if (player.steamId != 0 && steamAvatars.TryGetValue(player.steamId, out bitmap)) avatar.Child = new Image { Source = bitmap.Image, Stretch = Stretch.UniformToFill };
            avatar.CornerRadius = new CornerRadius(size / 2.0); avatar.Clip = new EllipseGeometry(new Point(size / 2.0, size / 2.0), size / 2.0, size / 2.0);
            return avatar;
        }
    }
}

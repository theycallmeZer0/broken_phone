using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;

namespace BrokenPhone
{
    public static class PendingWorkshop
    {
        static string Text(string value, int limit, string fallback = "")
        {
            string clean = new string((value ?? "").Where(c => !Char.IsControl(c)).ToArray()).Trim();
            return clean.Length == 0 ? fallback : clean.Substring(0, Math.Min(clean.Length, limit));
        }
        public static Pack Create(Pack metadata)
        {
            if (metadata == null || !Regex.IsMatch(metadata.workshopId ?? "", @"^\d{6,20}$")) throw new InvalidDataException("Некорректный ID выбранного пака Workshop.");
            Uri preview; string image = metadata.preview ?? "";
            if (image.Length > 2048 || !Uri.TryCreate(image, UriKind.Absolute, out preview) || preview.Scheme != "https") image = "";
            return new Pack
            {
                pendingDownload = true, id = "workshop-" + metadata.workshopId, workshopId = metadata.workshopId,
                title = Text(metadata.title, 160, "Пак Workshop " + metadata.workshopId), description = Text(metadata.description, 2000),
                language = Text(metadata.language, 80), preview = image, contentKey = Guid.NewGuid().ToString("N"),
                lineCount = Math.Max(0, Math.Min(100000, metadata.lineCount)), roleCount = Math.Max(0, Math.Min(1000, metadata.roleCount)),
                root = "", video = "", background = ""
            };
        }
    }

    public sealed partial class RoomServer
    {
        bool ProcessWorkshopSelection(Peer peer, Player player, Message message)
        {
            if (message.kind == "workshop_select")
            {
                Host(player); Lobby();
                pack = PendingWorkshop.Create(message.pack); state.pack = pack.PublicCopy(); packRevision++;
                ResetPackSharing(); RoleCasting.NewPack(state);
                foreach (var member in state.players) { member.prepared = false; member.downloadProgress = 0; member.loadStatus = "Загружаем выбранный пак…"; }
                Chat("Студия", "Выбран пак «" + pack.title + "». Загружаем файлы."); Broadcast(); return true;
            }
            if (message.kind != "workshop_offer") return false;
            // A completed download from an older selection must never replace a newer one.
            if (!pack.pendingDownload || message.code != pack.contentKey || state.phase != "lobby") return true;
            var offer = message.pack; var source = message.packPeer;
            if (offer == null || offer.pendingDownload || !offer.Selected || offer.workshopId != pack.workshopId || source == null || source.port <= 0 || source.port > 65535 || !PackTransferFiles.IsHash(source.token))
                throw new InvalidDataException("Некорректное предложение файлов выбранного пака.");
            PackContent.ValidateDescriptor(offer);
            RegisterWorkshopOffer(peer, pack.contentKey, offer.contentKey);
            var host = state.players.FirstOrDefault(member => member.host);
            var receiver = host == null ? null : peers.FirstOrDefault(member => member.Id == host.owner);
            if (receiver != null)
                receiver.Send(new Message
                {
                    kind = "workshop_offer", code = pack.contentKey, pack = offer.PublicCopy(),
                    packPeer = new PackPeerEndpoint { id = peer.Id, address = ((IPEndPoint)peer.Socket.Client.RemoteEndPoint).Address.ToString(), port = source.port, token = source.token, steamId = player.steamId }
                });
            return true;
        }
    }
}

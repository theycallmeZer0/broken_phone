using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace BrokenPhone
{
    public sealed class NetworkAddress
    {
        public string Address { get; set; }
        public string Name { get; set; }
        public bool Radmin { get; set; }
        public bool Steam { get; set; }
        public override string ToString() { return Steam ? Name : Name + " · " + Address; }
    }

    public static class Connection
    {
        public const int DefaultPort = 47770;
        public static List<NetworkAddress> Addresses()
        {
            var result = new List<NetworkAddress>();
            try
            {
                foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    bool radmin = (adapter.Name + " " + adapter.Description).IndexOf("Radmin", StringComparison.OrdinalIgnoreCase) >= 0;
                    foreach (var entry in adapter.GetIPProperties().UnicastAddresses)
                    {
                        var ip = entry.Address; string address = ip.ToString();
                        if (ip.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ip) || address.StartsWith("169.254.")) continue;
                        bool vpn = radmin || address.StartsWith("26.");
                        if (!result.Any(x => x.Address == address)) result.Add(new NetworkAddress { Address = address, Name = vpn ? "Radmin VPN" : adapter.Name, Radmin = vpn });
                    }
                }
            }
            catch (NetworkInformationException) { }
            if (result.Count == 0) result.Add(new NetworkAddress { Address = "127.0.0.1", Name = "Сеть не найдена", Radmin = false });
            return result.OrderByDescending(x => x.Radmin).ThenBy(x => x.Name).ToList();
        }
        public static string Invitation(string address, int port, string code)
        {
            if (port < 1 || port > 65535) throw new ArgumentException("Некорректный порт.");
            return address + ":" + port + "/" + code;
        }
        public static Uri Parse(string invitation)
        {
            Uri uri; var value = (invitation ?? "").Trim();
            if (!value.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase)) value = "tcp://" + value;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri) || uri.Scheme != "tcp" || uri.Port < 1 || uri.Port > 65535 || uri.AbsolutePath.Trim('/').Length != 6 || !String.IsNullOrEmpty(uri.UserInfo) || !String.IsNullOrEmpty(uri.Query) || !String.IsNullOrEmpty(uri.Fragment))
                throw new ArgumentException("Вставьте полное приглашение ведущего: IP:порт/КОД. Одного кода комнаты недостаточно.");
            return uri;
        }
        public const string Help = "Все участники запускают одну версию Broken Phone на своих компьютерах.\n\nSTEAM\n1. Запустите Steam и игру у всех участников.\n2. В «Подключении» выберите «Steam · без VPN». Если его нет, нажмите «Подключить Steam» в настройках.\n3. «Пригласить друзей» открывает список друзей Steam. Друг сначала запускает Broken Phone, затем принимает приглашение. Также можно скопировать ссылку и вставить её в «Войти в комнату».\n4. Для разработки используется Spacewar, App ID 480. В Steam может отображаться Spacewar. Отдельная регистрация в игре не нужна.\n\nLAN / RADMIN\nВ общей локальной сети или сети Radmin выберите адрес ведущего и скопируйте приглашение IP:порт/КОД. Друг вставляет его целиком. В этом режиме Steam не нужен.\n\nВидео, реплики и общий просмотр передаются автоматически. Исходные паки скачивает ведущий; гостям Workshop отдельно проходить не нужно.";
    }

    public sealed class TurnNotice
    {
        public string Title, Detail, Position;
        public static TurnNotice For(RoomState state, string playerId)
        {
            string name = state.Active == null ? "Игрок" : state.Active.name;
            int next = -1;
            for (int i = state.turn + 1; i < state.order.Length; i++) if (state.order[i] == playerId) { next = i; break; }
            return new TurnNotice {
                Title = next < 0 ? (state.order.Contains(playerId) ? "Твои реплики уже записаны" : "Ты смотришь этот раунд") : "Скоро твоя очередь",
                Detail = state.performing ? name + " сейчас записывает реплику." : name + " готовит свою реплику.",
                Position = next < 0 ? "Дождись остальных — затем посмотрим общий дубль." : next == state.turn + 1 ? "Ты следующий. Приготовь микрофон." : "До твоего хода: " + (next - state.turn) + " реплики."
            };
        }
    }
}

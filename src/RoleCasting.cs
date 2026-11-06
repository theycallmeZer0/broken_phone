using System;
using System.Collections.Generic;
using System.Linq;

namespace BrokenPhone
{
    public sealed class RoleAssignment
    {
        public string role { get; set; }
        public int lines { get; set; }
        public string playerId { get; set; }
    }

    public static class RoleCasting
    {
        public static string RoleName(string name) { return String.IsNullOrWhiteSpace(name) ? "Голос" : name.Trim(); }

        public static void NewPack(RoomState state)
        {
            state.roleMode = "manual";
            state.roleVersion = 0;
            state.roles = state.pack.lines.GroupBy(line => RoleName(line.role), StringComparer.Ordinal)
                .Select(group => new RoleAssignment { role = group.Key, lines = group.Count(), playerId = "" }).ToList();
            Refresh(state);
        }

        public static void Refresh(RoomState state)
        {
            if (state.phase == "casting") state.roleVersion++;
            foreach (var role in state.roles) if (!state.players.Any(player => player.id == role.playerId)) role.playerId = "";
            foreach (var player in state.players) player.ready = false;
        }

        public static void Automatic(RoomState state)
        {
            state.roleMode = "auto";
            var load = state.players.ToDictionary(player => player.id, player => 0);
            var count = state.players.ToDictionary(player => player.id, player => 0);
            foreach (var role in state.roles.OrderByDescending(role => role.lines))
            {
                var selected = state.players.OrderBy(player => load[player.id]).ThenBy(player => count[player.id]).FirstOrDefault();
                role.playerId = selected == null ? "" : selected.id;
                if (selected != null) { load[selected.id] += role.lines; count[selected.id]++; }
            }
            Refresh(state);
        }

        public static bool Complete(RoomState state)
        {
            return state.roles != null && state.roles.Count > 0 && state.players.Count > 0
                && state.roles.All(role => state.players.Any(player => player.id == role.playerId))
                && state.players.All(player => HasRole(state, player.id));
        }

        public static bool HasRole(RoomState state, string playerId) { return !String.IsNullOrEmpty(playerId) && state.roles.Any(role => role.playerId == playerId); }

        public static int BlockEnd(RoomState state)
        {
            int end = state.turn;
            while (end + 1 < state.order.Length && state.order[end + 1] == state.order[state.turn]) end++;
            return end;
        }

        public static string[] Order(RoomState state)
        {
            if (!Complete(state)) throw new InvalidOperationException("Сначала распределите все роли.");
            return state.pack.lines.Select(line => state.roles.First(role => role.role == RoleName(line.role)).playerId).ToArray();
        }
    }
}

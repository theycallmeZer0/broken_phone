using System;
using System.Reflection;

namespace BrokenPhone
{
    public static class GameVersion
    {
        static readonly Assembly assembly = typeof(GameVersion).Assembly;
        public static readonly string Version = assembly.GetName().Version.ToString();
        public static readonly string Build = assembly.ManifestModule.ModuleVersionId.ToString("N");

        public static void RequireMatch(int protocol, string version, string build)
        {
            if (protocol != Playback.Protocol || !String.Equals(version, Version, StringComparison.Ordinal))
            {
                System.Version parsed;
                string other = version != null && version.Length <= 32 && System.Version.TryParse(version, out parsed) ? parsed.ToString() : "неизвестна";
                throw new InvalidOperationException("Версии игры различаются. Текущая версия: " + Version + "; у другого участника: " + other + ". Для игры в одном лобби всем нужно установить игру из одного архива.");
            }
            if (!String.Equals(build, Build, StringComparison.Ordinal))
                throw new InvalidOperationException("Сборки Broken Phone " + Version + " различаются. Для игры в одном лобби всем нужно установить игру из одного архива.");
        }
    }
}

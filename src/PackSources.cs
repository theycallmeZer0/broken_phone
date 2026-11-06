using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace BrokenPhone
{
    public sealed class PackSourceFile
    {
        public string path { get; set; }
        public string hash { get; set; }
        public long bytes { get; set; }
    }

    public static class PackSources
    {
        public static bool Native(Pack pack) { return pack.sourceFiles != null && pack.sourceFiles.Count > 0; }
        public static bool Hash(string value) { return Regex.IsMatch(value ?? "", @"^[a-f0-9]{64}$"); }
        public static string Relative(string root, string file) { return Path.GetFullPath(file).Substring(Path.GetFullPath(root).TrimEnd('\\').Length + 1).Replace('\\', '/'); }
        public static string SafeFile(string root, string relative)
        {
            if (String.IsNullOrEmpty(relative) || relative.Length > 240 || relative.Contains('\\') || relative.Split('/').Any(part => part.Length == 0 || part == "." || part == ".." || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)) throw new InvalidDataException("Некорректный путь файла пака.");
            string file = PackLibrary.SafePath(root, relative), current = Path.GetFullPath(root);
            if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Ссылки в паке не поддерживаются.");
            foreach (string part in relative.Split('/'))
            {
                current = Path.Combine(current, part);
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Ссылки в паке не поддерживаются.");
            }
            return file;
        }
        public static string Fingerprint(IEnumerable<PackSourceFile> files)
        {
            string text = Json.Write(files.OrderBy(file => file.path, StringComparer.OrdinalIgnoreCase).Select(file => new { path = file.path.ToLowerInvariant(), file.hash, file.bytes }).ToArray());
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant();
        }
        public static void Describe(Pack pack, CancellationToken token)
        {
            if (String.IsNullOrEmpty(pack.sourceRoot)) return;
            var files = new List<PackSourceFile>(); var pending = new Stack<string>(); pending.Push(pack.sourceRoot);
            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested(); string directory = pending.Pop();
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Ссылки в паке не поддерживаются.");
                foreach (string file in Directory.GetFiles(directory))
                {
                    string name = Path.GetFileName(file);
                    if (name.StartsWith(".") || name.Equals("pack.json", StringComparison.OrdinalIgnoreCase) || name.Equals("_brokenphone_video.mp4", StringComparison.OrdinalIgnoreCase)) continue;
                    string relative = Relative(pack.sourceRoot, file); SafeFile(pack.sourceRoot, relative);
                    files.Add(new PackSourceFile { path = relative, bytes = new FileInfo(file).Length, hash = PackContent.FileHash(file, token) });
                    if (files.Count > 5000) throw new InvalidDataException("В паке больше 5000 файлов.");
                }
                foreach (string child in Directory.GetDirectories(directory)) if (!Path.GetFileName(child).StartsWith(".")) pending.Push(child);
            }
            pack.sourceFiles = files.OrderBy(file => file.path, StringComparer.OrdinalIgnoreCase).ToList();
            pack.sourceHash = Fingerprint(pack.sourceFiles); Validate(pack);
        }
        public static void Validate(Pack pack)
        {
            if (!Native(pack)) return;
            if (pack.sourceFiles.Count > 5000 || !Hash(pack.sourceHash)) throw new InvalidDataException("Некорректный список файлов пака.");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0;
            foreach (var file in pack.sourceFiles)
            {
                if (file == null || !Hash(file.hash) || file.bytes < 0 || file.bytes > 2L * 1024 * 1024 * 1024 || !names.Add(file.path ?? "")) throw new InvalidDataException("Некорректный файл пака.");
                SafeFile(Paths.Cache, file.path); total += file.bytes;
                if (total > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("Пак больше 2 ГБ.");
            }
            if (Fingerprint(pack.sourceFiles) != pack.sourceHash || !names.Contains(pack.sourceVideo) || !names.Contains("_pack_info.ini") || !String.IsNullOrEmpty(pack.sourceBackground) && !names.Contains(pack.sourceBackground)) throw new InvalidDataException("Список файлов пака повреждён.");
        }
        public static bool Matches(string path, PackSourceFile expected, CancellationToken token)
        {
            return File.Exists(path) && new FileInfo(path).Length == expected.bytes && PackContent.FileHash(path, token) == expected.hash;
        }
        public static void Copy(string source, string target, CancellationToken token)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            using (var input = File.OpenRead(source)) using (var output = File.Create(target))
            {
                byte[] buffer = new byte[65536]; int count;
                while ((count = input.Read(buffer, 0, buffer.Length)) > 0) { token.ThrowIfCancellationRequested(); output.Write(buffer, 0, count); }
            }
        }
    }
}

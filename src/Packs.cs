using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace BrokenPhone
{
    public static class PackLibrary
    {
        public static readonly string[] VideoExtensions = { ".mp4", ".webm", ".mkv", ".mov", ".avi", ".ogv" };
        public static string SafePath(string root, string name)
        {
            name = (name ?? "").Replace("res://", "").Replace('/', Path.DirectorySeparatorChar);
            string full = Path.GetFullPath(Path.Combine(root, name));
            string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Файл выходит за пределы папки пака.");
            return full;
        }
        static string Str(IDictionary<string, object> data, params string[] keys)
        {
            foreach (string key in keys) if (data.ContainsKey(key) && data[key] is string) return (string)data[key];
            return "";
        }
        static double Number(IDictionary<string, object> data, double fallback, params string[] keys)
        {
            foreach (string key in keys)
                if (data.ContainsKey(key)) return Convert.ToDouble(data[key], CultureInfo.InvariantCulture) / (key.EndsWith("_ms") ? 1000 : 1);
            return fallback;
        }
        public static double Duration(string path, CancellationToken cancellation = default(CancellationToken))
        {
            string result = Tools.Run("ffprobe", "-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 " + Tools.Quote(path), 30000, cancellation);
            double value;
            if (!Double.TryParse(result.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) || Double.IsNaN(value) || Double.IsInfinity(value) || value <= 0 || value > 3600)
                throw new InvalidDataException("Не удалось прочитать видео (допускаются клипы до 60 минут).");
            return value;
        }
        public static Pack Load(string folder, string workshopId, CancellationToken cancellation = default(CancellationToken))
        {
            cancellation.ThrowIfCancellationRequested();
            if (!File.Exists(Path.Combine(folder, "pack.json")))
            {
                string native = Directory.GetFiles(folder, "_pack_info.ini", SearchOption.AllDirectories).FirstOrDefault();
                if (native != null) return LoadDubTogether(Path.GetDirectoryName(native), workshopId, cancellation);
            }
            var data = new Dictionary<string, object>();
            string root = folder;
            foreach (var file in Directory.GetFiles(folder, "*.json", SearchOption.AllDirectories).OrderBy(p => Path.GetFileName(p) == "pack.json" ? 0 : 1).Take(100))
            {
                if (new FileInfo(file).Length > 8 * 1024 * 1024) continue;
                try
                {
                    var candidate = Json.Read<Dictionary<string, object>>(File.ReadAllText(file));
                    if (candidate != null && new[] { "video", "video_path", "video_file", "lines", "segments", "dialogue" }.Any(candidate.ContainsKey))
                    { data = candidate; root = Path.GetDirectoryName(file); break; }
                }
                catch (ArgumentException) { }
            }
            string name = Str(data, "video", "video_path", "video_file", "videoFile", "source_video");
            string video = String.IsNullOrEmpty(name) ? "" : SafePath(root, name);
            if (!File.Exists(video)) video = Directory.GetFiles(folder, "*", SearchOption.AllDirectories).FirstOrDefault(p => VideoExtensions.Contains(Path.GetExtension(p).ToLowerInvariant()));
            if (video == null) throw new InvalidDataException("В паке не найдено видео. Проверь содержимое папки.");
            double duration = Duration(video, cancellation);
            var pack = new Pack { id = String.IsNullOrEmpty(workshopId) ? Path.GetFileName(folder) : workshopId, workshopId = workshopId, video = video, root = folder,
                title = Str(data, "title", "name"), description = Str(data, "description"), language = Str(data, "language") };
            string originalName = Str(data, "original"); pack.original = String.IsNullOrEmpty(originalName) ? "" : SafePath(root, originalName);
            if (!File.Exists(pack.original)) pack.original = "";
            object sourceCues;
            if (data.TryGetValue("source_cues", out sourceCues) && sourceCues is IEnumerable)
                foreach (var entry in (IEnumerable)sourceCues)
                {
                    var cue = entry as IDictionary<string, object>; if (cue == null) continue; string source = Str(cue, "file");
                    if (!String.IsNullOrEmpty(source)) { string path = SafePath(root, source); if (File.Exists(path)) pack.sourceCues.Add(new SourceCue { key = Str(cue, "key"), file = path, start = Number(cue, 0, "start"), end = Number(cue, 0, "end") }); }
                }
            pack.preview = Str(data, "preview"); pack.thumbnail = Str(data, "thumbnail");
            pack.lineCount = (int)Number(data, 0, "line_count", "lineCount"); pack.roleCount = (int)Number(data, 0, "role_count", "roleCount");
            if (String.IsNullOrWhiteSpace(pack.title)) pack.title = Path.GetFileName(folder);
            if (String.IsNullOrEmpty(pack.description)) pack.description = "Пак сообщества";
            if (pack.description.IndexOf("INI + OGV", StringComparison.OrdinalIgnoreCase) >= 0) pack.description = "Пак сообщества Dub Together";
            if (pack.title.Length > 150) pack.title = pack.title.Substring(0, 150);
            object raw = null;
            foreach (string key in new[] { "lines", "segments", "dialogue" }) if (data.ContainsKey(key)) { raw = data[key]; break; }
            var entries = raw as IEnumerable;
            if (entries != null)
            {
                foreach (var entry in entries)
                {
                    var item = entry as IDictionary<string, object>;
                    if (item == null) continue;
                    double start = Number(item, -1, "start", "start_time", "startTime", "start_ms", "from");
                    if (start < 0) continue;
                    double end = Number(item, start + Number(item, 4, "duration"), "end", "end_time", "endTime", "end_ms", "to");
                    if (Double.IsNaN(start) || Double.IsInfinity(start) || Double.IsNaN(end) || Double.IsInfinity(end) || end <= start || end > duration + .2 || end - start > 60)
                        throw new InvalidDataException("Некорректные таймкоды (одна реплика — максимум 60 секунд).");
                    pack.lines.Add(new Line { start = start, end = Math.Min(end, duration), text = Str(item, "text", "subtitle"), role = RoleCasting.RoleName(Str(item, "role", "character", "speaker")), sourceKey = Str(item, "sourceKey", "source_key") });
                }
            }
            if (pack.lines.Count == 0)
            {
                pack.generatedTiming = true;
                int count = (int)Math.Ceiling(duration / 4);
                for (int i = 0; i < count; i++) pack.lines.Add(new Line { start = i * duration / count, end = (i + 1) * duration / count, text = "Придумай продолжение истории", role = "Голос" });
            }
            pack.generatedTiming = pack.generatedTiming || (data.ContainsKey("generated_timing") && Convert.ToBoolean(data["generated_timing"]));
            pack.mergedTiming = data.ContainsKey("merged_timing") && Convert.ToBoolean(data["merged_timing"]);
            pack.lines = pack.lines.OrderBy(l => l.start).ToList();
            if (pack.lines.Count > 500) throw new InvalidDataException("В паке больше 500 реплик.");
            string background = Str(data, "background", "background_audio", "instrumental", "music");
            pack.background = String.IsNullOrEmpty(background) ? "" : SafePath(root, background);
            if (!File.Exists(pack.background)) pack.background = "";
            string nativeMetadata = pack.mergedTiming ? Directory.GetFiles(folder, "_pack_info.ini", SearchOption.AllDirectories).FirstOrDefault() : Path.Combine(folder, "_pack_info.ini");
            if (pack.mergedTiming && (nativeMetadata == null || !File.Exists(nativeMetadata))) throw new InvalidDataException("Этот пак подготовлен старой версией с объединением реплик. Выбери исходную папку пака и подготовь его заново.");
            if ((pack.mergedTiming || pack.lineCount == 0 || pack.roleCount == 0 || pack.sourceCues.Count == 0) && nativeMetadata != null && File.Exists(nativeMetadata))
            {
                var original = LoadDubTogether(Path.GetDirectoryName(nativeMetadata), workshopId, cancellation); pack.lineCount = original.DisplayLines; pack.roleCount = original.DisplayRoles;
                if (pack.mergedTiming) { pack.lines = original.lines; pack.mergedTiming = false; }
                pack.sourceCues = original.sourceCues; pack.original = original.original;
                if (String.IsNullOrEmpty(pack.thumbnail)) pack.thumbnail = original.thumbnail;
            }
            if (String.IsNullOrEmpty(pack.thumbnail)) pack.thumbnail = MakeThumbnail(video, cancellation);
            if (String.IsNullOrEmpty(pack.original) && !data.ContainsKey("original") && HasAudio(video, cancellation)) pack.original = video;
            Availability(pack); PackContent.Describe(pack, cancellation);
            return pack;
        }
        static bool HasAudio(string file, CancellationToken cancellation)
        {
            return Tools.Run("ffprobe", "-v error -select_streams a:0 -show_entries stream=codec_type -of csv=p=0 " + Tools.Quote(file), 30000, cancellation).IndexOf("audio", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        static void Availability(Pack pack)
        {
            foreach (var line in pack.lines) line.originalAvailable = !String.IsNullOrEmpty(pack.original) || pack.sourceCues.Any(c => c.start < line.end && c.end > line.start && (String.IsNullOrEmpty(line.sourceKey) || c.key == line.sourceKey));
        }
        public static string MakeThumbnail(string video, CancellationToken cancellation = default(CancellationToken))
        {
            string output = Path.Combine(Paths.Cache, "thumbnail-" + Guid.NewGuid().ToString("N") + ".jpg");
            try
            {
                Tools.Run("ffmpeg", "-y -v error -ss 0 -i " + Tools.Quote(video) + " -frames:v 1 -vf scale=640:-2 -q:v 4 " + Tools.Quote(output), 30000, cancellation);
                return Convert.ToBase64String(File.ReadAllBytes(output));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) { System.Diagnostics.Trace.WriteLine(error); return ""; }
            finally { if (File.Exists(output)) File.Delete(output); }
        }
        static Dictionary<string, string> Ini(string path)
        {
            if (new FileInfo(path).Length > 2 * 1024 * 1024) throw new InvalidDataException("Слишком большой INI-файл.");
            var values = new Dictionary<string, string>();
            foreach (Match match in Regex.Matches(File.ReadAllText(path), @"(?m)^\s*([A-Za-z_]+)\s*=\s*([^\r\n]+)")) values[match.Groups[1].Value] = match.Groups[2].Value.Trim();
            return values;
        }
        static string IniText(Dictionary<string, string> data, string key, string fallback)
        {
            if (!data.ContainsKey(key)) return fallback;
            string value = data[key];
            if (value.StartsWith("\"")) try { return Json.Read<string>(value); } catch (ArgumentException) { }
            return value;
        }
        static object[] IniArray(Dictionary<string, string> data, string key)
        {
            if (!data.ContainsKey(key)) return new object[0];
            string value = data[key];
            // Godot may serialize packed arrays as PackedFloat64Array(...).
            if (value.StartsWith("Packed", StringComparison.Ordinal)) { int index = value.IndexOf('('); if (index >= 0 && value.EndsWith(")")) value = "[" + value.Substring(index + 1, value.Length - index - 2) + "]"; }
            return Json.Read<object[]>(value) ?? new object[0];
        }
        public static Pack LoadDubTogether(string folder, string workshopId, CancellationToken cancellation = default(CancellationToken))
        {
            var metadata = Ini(Path.Combine(folder, "_pack_info.ini"));
            string video = Path.Combine(folder, "dub_video.ogv");
            if (!File.Exists(video)) video = Directory.GetFiles(folder).FirstOrDefault(p => VideoExtensions.Contains(Path.GetExtension(p).ToLowerInvariant()));
            if (video == null) throw new InvalidDataException("В паке Dub Together отсутствует dub_video.ogv.");
            double total = Duration(video, cancellation);
            var pack = new Pack { id = String.IsNullOrEmpty(workshopId) ? Path.GetFileName(folder) : workshopId, root = folder, workshopId = workshopId, video = video,
                title = IniText(metadata, "title", Path.GetFileName(folder)), language = IniText(metadata, "language", ""), description = "Пак сообщества Dub Together" };
            string backing = Path.Combine(folder, "_backing_track.ogg"); pack.background = File.Exists(backing) ? backing : "";
            if (HasAudio(video, cancellation)) pack.original = video;
            var lines = new List<Line>();
            foreach (string file in Directory.GetFiles(folder, "*.ini").Where(p => !Path.GetFileName(p).StartsWith("_")).OrderBy(p => p, StringComparer.Ordinal))
            {
                var data = Ini(file); var starts = IniArray(data, "dub_timestamps"); var ends = IniArray(data, "dub_ends"); var characters = IniArray(data, "dub_characters");
                for (int i = 0; i < starts.Length; i++)
                {
                    double start = Convert.ToDouble(starts[i], CultureInfo.InvariantCulture), end = i < ends.Length ? Convert.ToDouble(ends[i], CultureInfo.InvariantCulture) : Math.Min(start + 4, total);
                    if (Double.IsNaN(start) || Double.IsInfinity(start) || Double.IsNaN(end) || Double.IsInfinity(end) || start < 0 || end <= start || end > total + .5) throw new InvalidDataException("В оригинальном паке некорректный таймкод: " + Path.GetFileName(file));
                    string sourceKey = Path.GetFileName(file) + "#" + i;
                    lines.Add(new Line { start = start, end = Math.Min(end, total), text = IniText(data, "caption", "Придумай свою реплику"), role = RoleCasting.RoleName(i < characters.Length ? Convert.ToString(characters[i]) : "Голос"), sourceKey = sourceKey });
                    string audio = Path.ChangeExtension(file, ".mp3"); if (File.Exists(audio)) pack.sourceCues.Add(new SourceCue { key = sourceKey, file = audio, start = start, end = Math.Min(end, total) });
                }
            }
            if (lines.Count == 0) throw new InvalidDataException("В INI-файлах пака не найдены таймкоды реплик.");
            pack.lineCount = lines.Count; pack.roleCount = lines.Select(l => l.role).Where(r => !String.IsNullOrWhiteSpace(r)).Distinct().Count();
            string icon = SafePath(folder, IniText(metadata, "icon", "Icon.png"));
            if (File.Exists(icon) && new FileInfo(icon).Length <= 1024 * 1024) pack.thumbnail = Convert.ToBase64String(File.ReadAllBytes(icon));
            else pack.thumbnail = MakeThumbnail(video, cancellation);
            pack.lines = lines.OrderBy(line => line.start).ToList();
            var sequential = new List<Line>();
            foreach (var line in pack.lines)
            {
                int count = Math.Max(1, (int)Math.Ceiling((line.end - line.start) / 60));
                for (int i = 0; i < count; i++) sequential.Add(new Line { start = line.start + (line.end - line.start) * i / count, end = line.start + (line.end - line.start) * (i + 1) / count, text = line.text, role = line.role, sourceKey = line.sourceKey });
            }
            if (sequential.Count > 500) throw new InvalidDataException("В паке больше 500 последовательных фрагментов.");
            pack.lines = sequential; Availability(pack); PackContent.Describe(pack, cancellation); return pack;
        }
        public static void Extract(string archive, string destination, CancellationToken cancellation = default(CancellationToken))
        {
            using (var zip = ZipFile.OpenRead(archive))
            {
                if (zip.Entries.Count > 5000 || zip.Entries.Sum(e => e.Length) > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("Архив слишком большой (максимум 2 ГБ / 5000 файлов).");
                foreach (var entry in zip.Entries)
                {
                    SafePath(destination, entry.FullName.Replace('\\', '/'));
                    if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000) throw new InvalidDataException("Ссылки в архивах не поддерживаются.");
                }
                foreach (var entry in zip.Entries)
                {
                    cancellation.ThrowIfCancellationRequested(); string path = SafePath(destination, entry.FullName.Replace('\\', '/'));
                    if (String.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(path); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    using (var input = entry.Open()) using (var output = File.Create(path)) Copy(input, output, cancellation);
                }
            }
        }
        public static Pack Import(string source, string workshopId, CancellationToken cancellation = default(CancellationToken), string storageKey = null)
        {
            cancellation.ThrowIfCancellationRequested();
            string key = storageKey ?? (String.IsNullOrEmpty(workshopId) ? Guid.NewGuid().ToString("N") : workshopId);
            if (!Regex.IsMatch(key, @"^[a-zA-Z0-9_-]{1,80}$")) throw new InvalidDataException("Некорректный ID.");
            string destination = Path.Combine(Paths.Packs, key);
            string stage = destination + "-import-" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(stage);
            try
            {
                if (Directory.Exists(source)) CopyDirectory(source, stage, cancellation);
                else if (VideoExtensions.Contains(Path.GetExtension(source).ToLowerInvariant()))
                    using (var input = File.OpenRead(source)) using (var output = File.Create(Path.Combine(stage, Path.GetFileName(source)))) Copy(input, output, cancellation);
                else Extract(source, stage, cancellation);
                cancellation.ThrowIfCancellationRequested();
                Pack loaded = Load(stage, workshopId, cancellation);
                if (Path.GetExtension(loaded.video).ToLowerInvariant() != ".mp4")
                {
                    string converted = Path.Combine(stage, "_brokenphone_video.mp4");
                    Tools.Run("ffmpeg", "-y -v error -i " + Tools.Quote(loaded.video) + " -an -c:v libx264 -preset veryfast -crf 20 -pix_fmt yuv420p -movflags +faststart " + Tools.Quote(converted), 600000, cancellation);
                    loaded.video = converted;
                }
                var canonical = new { id = key, title = loaded.title, description = loaded.description, language = loaded.language,
                    video = loaded.video.Substring(stage.TrimEnd(Path.DirectorySeparatorChar).Length + 1), background = String.IsNullOrEmpty(loaded.background) ? "" : loaded.background.Substring(stage.TrimEnd(Path.DirectorySeparatorChar).Length + 1), lines = loaded.lines,
                    generated_timing = loaded.generatedTiming, merged_timing = loaded.mergedTiming, line_count = loaded.DisplayLines, role_count = loaded.DisplayRoles, preview = loaded.preview, thumbnail = loaded.thumbnail,
                    original = String.IsNullOrEmpty(loaded.original) ? "" : loaded.original.Substring(stage.TrimEnd(Path.DirectorySeparatorChar).Length + 1),
                    source_cues = loaded.sourceCues.Select(c => new { key = c.key, file = c.file.Substring(stage.TrimEnd(Path.DirectorySeparatorChar).Length + 1), start = c.start, end = c.end }).ToArray() };
                File.WriteAllText(Path.Combine(stage, "pack.json"), Json.Write(canonical));
                cancellation.ThrowIfCancellationRequested();
                if (Directory.Exists(destination)) Directory.Delete(destination, true);
                Directory.Move(stage, destination);
                File.WriteAllText(Path.Combine(destination, ".source"), workshopId ?? "");
                return Load(destination, workshopId);
            }
            finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
        }
        static void Copy(Stream input, Stream output, CancellationToken cancellation)
        {
            var buffer = new byte[65536]; int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0) { cancellation.ThrowIfCancellationRequested(); output.Write(buffer, 0, read); }
        }
        static void CopyDirectory(string source, string target, CancellationToken cancellation)
        {
            foreach (string file in Directory.GetFiles(source))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Ссылки в паке не поддерживаются.");
                cancellation.ThrowIfCancellationRequested();
                using (var input = File.OpenRead(file)) using (var output = File.Create(Path.Combine(target, Path.GetFileName(file)))) Copy(input, output, cancellation);
            }
            foreach (string dir in Directory.GetDirectories(source))
            {
                if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Ссылки в паке не поддерживаются.");
                cancellation.ThrowIfCancellationRequested(); string next = Path.Combine(target, Path.GetFileName(dir)); Directory.CreateDirectory(next); CopyDirectory(dir, next, cancellation);
            }
        }
        public static List<Pack> Installed()
        {
            var packs = new List<Pack>();
            string demo = Path.Combine(Paths.Base, "Assets", "Demo");
            if (Directory.Exists(demo)) packs.Add(Load(demo, ""));
            foreach (var folder in Directory.GetDirectories(Paths.Packs).Where(p => !p.Contains("-import-")))
                try { packs.Add(Load(folder, File.Exists(Path.Combine(folder, ".source")) ? File.ReadAllText(Path.Combine(folder, ".source")) : "")); }
                catch (Exception error) { System.Diagnostics.Trace.WriteLine(error); }
            return packs;
        }
    }

    public static class Tools
    {
        public static string Quote(string value) { return "\"" + value.Replace("\"", "") + "\""; }
        public static string Find(string name)
        {
            foreach (string dir in new[] { Paths.Base, Path.Combine(Paths.Base, "tools"), @"C:\ffmpeg\bin" }.Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';')))
            {
                string path = Path.Combine(dir.Trim('"'), name + ".exe");
                if (File.Exists(path)) return path;
            }
            throw new FileNotFoundException("Не найден " + name + ".exe. Добавьте FFmpeg в PATH или папку tools рядом с игрой.");
        }
        public static string Run(string name, string arguments, int timeout, CancellationToken cancellation = default(CancellationToken))
        {
            cancellation.ThrowIfCancellationRequested();
            var info = new ProcessStartInfo(Find(name), arguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            using (var process = Process.Start(info))
            {
                var stderr = process.StandardError.ReadToEndAsync();
                var stdout = process.StandardOutput.ReadToEndAsync();
                var watch = Stopwatch.StartNew();
                while (!process.WaitForExit(100))
                {
                    if (cancellation.IsCancellationRequested || watch.ElapsedMilliseconds > timeout)
                    { process.Kill(); process.WaitForExit(5000); cancellation.ThrowIfCancellationRequested(); throw new TimeoutException(name + ": превышено время ожидания."); }
                }
                cancellation.ThrowIfCancellationRequested();
                if (process.ExitCode != 0) throw new InvalidDataException(name + ": " + stderr.Result.Substring(Math.Max(0, stderr.Result.Length - 800)));
                return stdout.Result;
            }
        }
    }
}

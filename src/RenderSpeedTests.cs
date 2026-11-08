using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;

namespace BrokenPhone
{
    public static partial class SelfTests
    {
        static string VideoPackets(string file)
        {
            return Tools.Run("ffprobe", "-v error -select_streams v:0 -show_packets -show_data_hash sha256 -show_entries packet=data_hash -of csv=p=0 " + Tools.Quote(file), 30000).Trim();
        }
        static byte[] RenderAudioPcm(string source, string directory, string name)
        {
            string decoded = Path.Combine(directory, name + ".wav");
            Tools.Run("ffmpeg", "-y -v error -i " + Tools.Quote(source) + " -vn -ar 44100 -ac 1 -c:a pcm_s16le " + Tools.Quote(decoded), 30000);
            return Audio.ReadPcm(File.ReadAllBytes(decoded));
        }
        public static int RenderSpeedRegression()
        {
            var report = new List<string>(); string root = Path.Combine(Paths.Cache, "render-speed-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            try
            {
                string video = Path.Combine(root, "input.mp4"), background = Path.Combine(root, "background.wav");
                Tools.Run("ffmpeg", "-y -v error -f lavfi -i testsrc2=size=1280x720:rate=24 -t 24 -an -c:v libx264 -preset ultrafast -crf 24 -pix_fmt yuv420p " + Tools.Quote(video), 120000);
                File.WriteAllBytes(background, ToneWave(24, 110, 2500));
                string stereoBackground = Path.Combine(root, "background-stereo.wav");
                Tools.Run("ffmpeg", "-y -v error -i " + Tools.Quote(background) + " -ac 2 -ar 48000 " + Tools.Quote(stereoBackground), 30000); background = stereoBackground;
                var pack = new Pack { video = video, root = root, background = background, title = "Render speed fixture" };
                var takes = new List<string>();
                for (int i = 0; i < 8; i++)
                {
                    double start = i < 2 ? i * 2 : i * 2.75;
                    pack.lines.Add(new Line { start = start, end = start + 3, role = "Role " + i });
                    string take = Path.Combine(root, "take-" + i + ".wav");
                    File.WriteAllBytes(take, ToneWave(3, i == 0 ? 330 : i == 1 ? 770 : 990 + i * 110, 5000)); takes.Add(take);
                }
                string copied = Path.Combine(root, "copy"); Directory.CreateDirectory(copied); string output = Path.Combine(copied, "result.mp4");
                var timer = Stopwatch.StartNew(); Audio.Render(pack, takes.ToArray(), output); timer.Stop();
                string packets = VideoPackets(video);
                Check(packets.Length > 0 && packets == VideoPackets(output), "Готовое H264-видео перекодировано или потеряло кадры вместо копирования исходных пакетов.");
                Check(Math.Abs(PackLibrary.Duration(output) - 24) < .12, "Длительность итогового видео изменилась при копировании.");
                byte[] mix = RenderAudioPcm(output, copied, "mix"), voices = RenderAudioPcm(Path.Combine(copied, "result-voices.m4a"), copied, "voices"), movie = RenderAudioPcm(Path.Combine(copied, "result-movie.m4a"), copied, "movie");
                Check(FrequencyLevel(mix, 2.15, 2.85, 330) > 900 && FrequencyLevel(mix, 2.15, 2.85, 770) > 900 && FrequencyLevel(mix, 6.8, 7.3, 110) > 900, "В финальном MP4 потеряны пересекающиеся голоса или фон.");
                Check(FrequencyLevel(voices, 2.15, 2.85, 330) > 900 && FrequencyLevel(voices, 2.15, 2.85, 770) > 900 && FrequencyLevel(voices, 6.8, 7.3, 110) < 50, "Отдельная дорожка голосов содержит фон или потеряла реплики.");
                Check(FrequencyLevel(movie, 2.15, 2.85, 110) > 900 && FrequencyLevel(movie, 2.15, 2.85, 330) < 50, "Отдельная дорожка фона содержит записанные голоса.");
                Check(Math.Abs(PackLibrary.Duration(Path.Combine(copied, "result-voices.m4a")) - 24) < .12 && Math.Abs(PackLibrary.Duration(Path.Combine(copied, "result-movie.m4a")) - 24) < .12, "Дорожки общего просмотра имеют другую длительность.");
                string movieChannels = Tools.Run("ffprobe", "-v error -select_streams a:0 -show_entries stream=channels -of default=noprint_wrappers=1:nokey=1 " + Tools.Quote(Path.Combine(copied, "result-movie.m4a")), 30000).Trim();
                Check(movieChannels == "2", "Стереофон ролика потерял каналы при формировании отдельных дорожек.");
                report.Add("PASS: a 24-second 1280x720 H264 fixture retains every compressed video packet unchanged; overlapping voices, background, separate playback tracks and duration are verified after decoding; render=" + timer.Elapsed.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture) + " s");

                string original = Path.Combine(root, "original.wav"); File.WriteAllBytes(original, ToneWave(24, 220, 2500));
                pack.background = ""; pack.original = original;
                string originalResult = Path.Combine(root, "original"); Directory.CreateDirectory(originalResult);
                Audio.Render(pack, takes.ToArray(), Path.Combine(originalResult, "result.mp4"));
                mix = RenderAudioPcm(Path.Combine(originalResult, "result.mp4"), originalResult, "mix"); movie = RenderAudioPcm(Path.Combine(originalResult, "result-movie.m4a"), originalResult, "movie");
                Check(Audio.MovieKind(pack) == "original" && FrequencyLevel(movie, .2, .8, 220) > 900 && FrequencyLevel(mix, .2, .8, 220) < 50 && FrequencyLevel(mix, .2, .8, 330) > 900, "Исходный звук ошибочно добавлен в экспорт или потерян для отдельного воспроизведения.");
                pack.original = "";
                string silentResult = Path.Combine(root, "silent"); Directory.CreateDirectory(silentResult);
                Audio.Render(pack, takes.ToArray(), Path.Combine(silentResult, "result.mp4"));
                movie = RenderAudioPcm(Path.Combine(silentResult, "result-movie.m4a"), silentResult, "movie");
                Check(Audio.MovieKind(pack) == "none" && movie.All(value => value == 0), "Отсутствующий звук ролика не заменён корректной дорожкой тишины.");
                report.Add("PASS: original movie sound stays separate from exported voices; a silent source still produces a full-duration silent playback track");

                string legacy = Path.Combine(root, "legacy.avi");
                Tools.Run("ffmpeg", "-y -v error -i " + Tools.Quote(video) + " -t 6 -an -c:v mpeg4 -q:v 5 " + Tools.Quote(legacy), 60000);
                pack.video = legacy; pack.lines = pack.lines.Take(2).ToList();
                string encodedResult = Path.Combine(root, "encoded"); Directory.CreateDirectory(encodedResult); output = Path.Combine(encodedResult, "result.mp4");
                Audio.Render(pack, takes.Take(2).ToArray(), output);
                string codec = Tools.Run("ffprobe", "-v error -select_streams v:0 -show_entries stream=codec_name -of default=noprint_wrappers=1:nokey=1 " + Tools.Quote(output), 30000).Trim();
                mix = RenderAudioPcm(output, encodedResult, "mix");
                Check(codec == "h264" && Math.Abs(PackLibrary.Duration(output) - 6) < .12 && FrequencyLevel(mix, 2.15, 2.85, 330) > 900 && FrequencyLevel(mix, 2.15, 2.85, 770) > 900, "Резервное кодирование неподдерживаемого исходного видео не сохранило изображение, длительность или голоса.");
                report.Add("PASS: a non-H264 source uses the encoder fallback and produces a playable H264 MP4 with both overlapping voices intact");
                return 0;
            }
            catch (Exception error) { report.Add("FAIL: " + error); return 1; }
            finally
            {
                File.WriteAllLines(Path.Combine(Paths.Base, "render-speed-test.txt"), report);
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }
    }
}

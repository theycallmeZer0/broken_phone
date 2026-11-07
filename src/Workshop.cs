using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace BrokenPhone
{
    public sealed class WorkshopItem
    {
        public string Id, Title, Author, Preview, Description, FileUrl;
        public int Lines, Roles;
        public string Url { get { return "https://steamcommunity.com/sharedfiles/filedetails/?id=" + Id; } }
    }
    public sealed class WorkshopService
    {
        public const string AppId = "5020310";
        public Func<string, Action<PackProgress>, CancellationToken, Task<string>> ClientDownload;
        sealed class Client : WebClient
        {
            protected override WebRequest GetWebRequest(Uri uri) { var request = base.GetWebRequest(uri); request.Timeout = 25000; return request; }
        }
        static string Get(string url)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            using (var client = new Client()) { client.Encoding = Encoding.UTF8; client.Headers[HttpRequestHeader.UserAgent] = "Mozilla/5.0 BrokenPhone/0.7"; return client.DownloadString(url); }
        }
        static string Text(string value) { return WebUtility.HtmlDecode(Regex.Replace(value, "<[^>]+>", "")).Trim(); }
        static WorkshopItem Counts(WorkshopItem item)
        {
            int value; var lines = Regex.Match(item.Description ?? "", @"Lines:\s*(\d+)", RegexOptions.IgnoreCase); var roles = Regex.Match(item.Description ?? "", @"Roles:\s*(\d+)", RegexOptions.IgnoreCase);
            if (Int32.TryParse(lines.Groups[1].Value, out value)) item.Lines = value;
            if (Int32.TryParse(roles.Groups[1].Value, out value)) item.Roles = value;
            return item;
        }
        public static List<WorkshopItem> Parse(string html)
        {
            var result = new List<WorkshopItem>();
            // Steam's current React/SSR catalog keeps full item records in this JSON block.
            var ssr = Regex.Match(html, "<script[^>]+id=[\"']valve-ssr-data[\"'][^>]*>(.*?)</script>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (ssr.Success)
            {
                try { Visit(Json.Read<object>(ssr.Groups[1].Value), result, 0); }
                catch (ArgumentException) { }
                if (result.Count > 0) return result;
            }
            // Match by item title rather than page-global links to exclude navigation/collections.
            var titles = Regex.Matches(html, "<[^>]+class=[\"'][^\"']*workshopItemTitle[^\"']*[\"'][^>]*>(.*?)</[^>]+>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            foreach (Match title in titles)
            {
                string before = html.Substring(Math.Max(0, title.Index - 5000), Math.Min(5000, title.Index));
                var ids = Regex.Matches(before, @"(?:filedetails/\?id=|data-publishedfileid=[""'])(\d+)");
                if (ids.Count == 0) continue;
                string id = ids[ids.Count - 1].Groups[1].Value;
                if (result.Any(x => x.Id == id)) continue;
                var images = Regex.Matches(before, "(?:src|data-src)=[\"'](https://[^\"']+)[\"']", RegexOptions.IgnoreCase);
                string preview = images.Count == 0 ? "" : WebUtility.HtmlDecode(images[images.Count - 1].Groups[1].Value);
                result.Add(Counts(new WorkshopItem { Id = id, Title = Text(title.Groups[1].Value), Preview = preview, Author = "Steam Workshop", Description = "Пак сообщества Dub Together" }));
            }
            return result;
        }
        static void Visit(object node, List<WorkshopItem> items, int depth)
        {
            if (node == null || depth > 24) return;
            var map = node as Dictionary<string, object>;
            if (map != null)
            {
                if (map.ContainsKey("publishedfileid") && map.ContainsKey("title"))
                {
                    string id = Convert.ToString(map["publishedfileid"]);
                    if (Regex.IsMatch(id, @"^\d+$") && !items.Any(x => x.Id == id) && (!map.ContainsKey("consumer_appid") || Convert.ToString(map["consumer_appid"]) == AppId))
                        items.Add(Counts(new WorkshopItem { Id = id, Title = Convert.ToString(map["title"]), Preview = map.ContainsKey("preview_url") ? Convert.ToString(map["preview_url"]) : "", Description = map.ContainsKey("short_description") ? Text(Convert.ToString(map["short_description"])) : "Пак сообщества", Author = "Steam Workshop" }));
                }
                foreach (var value in map.Values) Visit(value, items, depth + 1);
                return;
            }
            var text = node as string;
            if (text != null)
            {
                if (text.StartsWith("{") || text.StartsWith("[")) try { Visit(Json.Read<object>(text), items, depth + 1); } catch (ArgumentException) { }
                return;
            }
            var list = node as System.Collections.IEnumerable;
            if (list != null) foreach (var item in list) Visit(item, items, depth + 1);
        }
        public List<WorkshopItem> Browse(string query, int page, int sort, int language)
        {
            string direct = Regex.Match(query ?? "", @"(?:^|[?&]id=)(\d{6,})(?:$|&)").Groups[1].Value;
            if (!String.IsNullOrEmpty(direct)) return new List<WorkshopItem> { Details(direct) };
            string url = "https://steamcommunity.com/workshop/browse/?appid=" + AppId + "&section=readytouseitems&browsesort=" + (sort == 1 ? "mostrecent" : "trend") + "&days=" + (sort == 2 ? "-1" : "90") + "&num_per_page=30&p=" + page + "&searchtext=" + Uri.EscapeDataString(query ?? "");
            if (language > 0) url += "&requiredtags%5B%5D=" + (language == 1 ? "Russian" : "English");
            string html = Get(url); var items = Parse(html);
            if (items.Count == 0 && !Regex.IsMatch(html, "0 (?:entries|items)|No items matching|workshopBrowseItems", RegexOptions.IgnoreCase))
                throw new InvalidDataException("Steam вернул страницу без распознаваемого каталога. Попробуйте ID или ссылку на пак.");
            return items;
        }
        public WorkshopItem Details(string id)
        {
            return DetailsAsync(id, CancellationToken.None).GetAwaiter().GetResult();
        }
        async Task<WorkshopItem> DetailsAsync(string id, CancellationToken cancellation)
        {
            if (!Regex.IsMatch(id, @"^\d{6,20}$")) throw new InvalidDataException("Некорректный ID Workshop.");
            {
                string json = await Transfers.Post("https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/", "itemcount=1&publishedfileids%5B0%5D=" + id, cancellation).ConfigureAwait(false);
                var data = Json.Read<Dictionary<string, object>>(json); var response = (Dictionary<string, object>)data["response"];
                var list = (System.Collections.IEnumerable)response["publishedfiledetails"];
                foreach (Dictionary<string, object> item in list)
                {
                    if (Convert.ToInt32(item["result"]) != 1 || Convert.ToString(item["consumer_app_id"]) != AppId) throw new InvalidDataException("Этот пак недоступен или относится к другой игре.");
                    return Counts(new WorkshopItem { Id = id, Title = Convert.ToString(item["title"]), Preview = item.ContainsKey("preview_url") ? Convert.ToString(item["preview_url"]) : "", FileUrl = item.ContainsKey("file_url") ? Convert.ToString(item["file_url"]) : "", Author = "Steam Workshop", Description = Text(Convert.ToString(item["description"])) });
                }
            }
            throw new InvalidDataException("Steam не вернул данные пака.");
        }
        public static PackProgress ParseProgress(string line)
        {
            var ratio = Regex.Match(line, @"\((\d+)\s*/\s*(\d+)\)"); long received, total;
            if (ratio.Success && Int64.TryParse(ratio.Groups[1].Value, out received) && Int64.TryParse(ratio.Groups[2].Value, out total)) return PackProgress.Bytes("Steam загружает файлы", received, total);
            var percent = Regex.Match(line, @"(?:progress:\s*|\[)(\d+(?:[.,]\d+)?)(?:%|\s)", RegexOptions.IgnoreCase); double value;
            if (percent.Success && Double.TryParse(percent.Groups[1].Value.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value)) return new PackProgress { Stage = "Steam загружает файлы", Fraction = Math.Max(0, Math.Min(1, value / 100)) };
            if (line.IndexOf("Downloading item", StringComparison.OrdinalIgnoreCase) >= 0) return PackProgress.Step("Steam загружает файлы…");
            return null;
        }
        public List<Pack> InstalledPacks() { return PackLibrary.Installed(); }
        public Pack FindOwned(string id, CancellationToken cancellation = default(CancellationToken))
        {
            if (!Regex.IsMatch(id ?? "", @"^\d{6,20}$")) return null;
            string path = Path.Combine(Paths.Packs, id);
            return File.Exists(Path.Combine(path, "pack.json")) ? PackLibrary.Load(path, id, cancellation) : null;
        }
        public Task<Pack> Download(WorkshopItem item, Action<string> status, CancellationToken cancellation = default(CancellationToken)) { return DownloadProgress(item, p => status(p.Label), cancellation); }
        public async Task<Pack> DownloadProgress(WorkshopItem item, Action<PackProgress> progress, CancellationToken cancellation = default(CancellationToken))
        {
            Action<string> status = text => progress(PackProgress.Step(text));
            cancellation.ThrowIfCancellationRequested();
            var owned = await Task.Run(() => FindOwned(item.Id, cancellation), cancellation).ConfigureAwait(false);
            if (owned != null) { status("Пак уже загружен."); return owned; }
            status("Получаем данные пака из Steam…"); var details = await DetailsAsync(item.Id, cancellation).ConfigureAwait(false);
            Pack loaded;
            if (!String.IsNullOrEmpty(details.FileUrl))
            {
                var uri = new Uri(details.FileUrl); if (uri.Scheme != "https") throw new InvalidDataException("Steam вернул незащищённую ссылку на файл.");
                status("Скачиваем пак с серверов Steam…"); string archive = Path.Combine(Paths.Cache, item.Id + ".zip");
                await Transfers.Download(uri.AbsoluteUri, archive, cancellation, (received, total) => progress(PackProgress.Bytes("Загрузка из Steam", received, total))).ConfigureAwait(false);
                status("Распаковываем и подготавливаем видео…");
                loaded = await Task.Run(() => PackLibrary.Import(archive, item.Id, cancellation), cancellation).ConfigureAwait(false);
            }
            else
            {
                if (ClientDownload == null) throw new IOException("Для загрузки этого пака подключи открытый клиент Steam в настройках игры. Отдельный вход в загрузчик не требуется.");
                string downloaded = await ClientDownload(item.Id, progress, cancellation).ConfigureAwait(false);
                status("Подготавливаем видео и реплики…"); loaded = await Task.Run(() => PackLibrary.Import(downloaded, item.Id, cancellation), cancellation).ConfigureAwait(false);
            }
            cancellation.ThrowIfCancellationRequested();
            loaded.preview = details.Preview;
            string metadata = Path.Combine(loaded.root, "pack.json"); var data = Json.Read<Dictionary<string, object>>(File.ReadAllText(metadata)); data["preview"] = loaded.preview;
            File.WriteAllText(metadata, Json.Write(data)); progress(new PackProgress { Stage = "Пак готов", Fraction = 1 }); return loaded;
        }
    }
}

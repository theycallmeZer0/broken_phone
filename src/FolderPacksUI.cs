using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace BrokenPhone
{
    public sealed partial class GameWindow
    {
        CancellationTokenSource folderScan;
        string PacksFolder { get { return FolderPacks.Normalize(settings.PacksFolder); } }

        void InitializeFolderPacks()
        {
            B("ChoosePacksFolderButton").Click += async delegate
            {
                string folder = PickPacksFolder(PacksFolder);
                if (folder != null) await Guard(() => ApplyPackSource(true, folder));
            };
            B("BrowsePacksFolderButton").Click += delegate
            {
                string folder = PickPacksFolder(TBox("PacksFolderInput").Text);
                if (folder != null) { TBox("PacksFolderInput").Text = folder; C<ComboBox>("PackSourceCombo").SelectedIndex = 0; }
            };
            C<ComboBox>("PackSourceCombo").SelectionChanged += delegate { RenderPackSourceSettings(); };
            RenderPackSource();
        }

        sealed class DialogOwner : System.Windows.Forms.IWin32Window
        {
            public IntPtr Handle { get; private set; }
            public DialogOwner(Window owner) { Handle = new WindowInteropHelper(owner).Handle; }
        }

        string PickPacksFolder(string initial)
        {
            using (var picker = new System.Windows.Forms.FolderBrowserDialog())
            {
                picker.Description = "Выбери папку с паками или один распакованный пак Dub Together";
                picker.ShowNewFolderButton = false;
                try { string path = FolderPacks.Normalize(initial); if (Directory.Exists(path)) picker.SelectedPath = path; } catch (ArgumentException) { }
                return picker.ShowDialog(new DialogOwner(window)) == System.Windows.Forms.DialogResult.OK ? picker.SelectedPath : null;
            }
        }

        void RenderPackSourceSettings()
        {
            bool local = C<ComboBox>("PackSourceCombo").SelectedIndex != 1;
            C<StackPanel>("LocalPackSettings").Visibility = local ? Visibility.Visible : Visibility.Collapsed;
            C<StackPanel>("SteamPackSettings").Visibility = local ? Visibility.Collapsed : Visibility.Visible;
        }

        void RenderPackSource()
        {
            bool local = settings.LocalPacks;
            Text("WorkshopTitle", local ? "Паки из папки" : "Мастерская");
            Text("WorkshopSubtitle", local ? "Локальная коллекция · вход в Steam для паков не требуется" : "Steam Workshop · Dub Together · 5020310");
            Text("PackSourcePath", PacksFolder); C<TextBlock>("PackSourcePath").ToolTip = PacksFolder;
            C<StackPanel>("LocalPacksNotice").Visibility = local ? Visibility.Visible : Visibility.Collapsed;
            C<StackPanel>("PackDownloadToolbar").Visibility = local ? Visibility.Collapsed : Visibility.Visible;
            C<ComboBox>("SortCombo").Visibility = local ? Visibility.Collapsed : Visibility.Visible;
            C<ColumnDefinition>("SortColumn").Width = new GridLength(local ? 0 : 200);
            B("PreviousPage").Visibility = B("NextPage").Visibility = local ? Visibility.Collapsed : Visibility.Visible;
            B("SearchButton").Content = local ? "Обновить" : "Найти";
            B("MyPacksFolderButton").Content = local ? "Открыть папку ↗" : "Мои паки ↗";
            TBox("SearchInput").Tag = local ? "Название пака" : "Название пака или ссылка Steam";
            TBox("SearchInput").ToolTip = local ? "Поиск по названиям паков в выбранной папке" : "Название пака, ссылка Steam или ID";
            if (local) C<ComboBox>("LanguageCombo").SelectedIndex = 0;
            Text("PageLabel", local ? "Локальная коллекция" : "Страница " + workshopPage);
            Text("WorkshopStatus", local ? "Выбери папку с распакованными паками Dub Together или pack.json." : "Каталог загружается из Steam.");
        }

        async Task ApplyPackSource(bool local, string folder)
        {
            string path = FolderPacks.Normalize(folder);
            if (local && !Directory.Exists(path)) throw new DirectoryNotFoundException("Папка с паками не найдена. Проверь путь или выбери её кнопкой «Обзор…».");
            settings.LocalPacks = local; settings.PacksFolder = path; Persist();
            workshopPage = 1; RenderPackSource();
            if (folderScan != null) folderScan.Cancel();
            ++searchGeneration; packControls.Clear(); C<WrapPanel>("PackCards").Children.Clear();
            if (currentPage == "Workshop") await Search();
        }

        async Task SearchFolder(string query, int language, int generation)
        {
            if (folderScan != null) folderScan.Cancel();
            var cancellation = new CancellationTokenSource(); folderScan = cancellation;
            string folder = PacksFolder;
            Text("WorkshopStatus", "Читаем паки из выбранной папки…");
            try
            {
                FolderPackCatalog catalog = await Task.Run(() => FolderPacks.Scan(folder, cancellation.Token), cancellation.Token);
                if (disposed || generation != searchGeneration) return;
                if (FolderPacks.SameFolder(folder, Paths.Packs))
                {
                    var demo = installed.FirstOrDefault(p => FolderPacks.SameFolder(p.root, System.IO.Path.Combine(Paths.Base, "Assets", "Demo")));
                    if (demo != null) catalog.Packs.Insert(0, demo);
                }
                var matches = catalog.Packs.Where(p => p.title.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 && LocalLanguage(p.language, language)).ToArray();
                foreach (var pack in matches) LocalCard(pack);
                string status = "Паков в папке: " + catalog.Packs.Count + ". Показано: " + matches.Length + ".";
                if (catalog.Packs.Count == 0 && catalog.Problems.Count == 0) status = "В папке нет паков. Нужны распакованные папки с _pack_info.ini или pack.json.";
                if (catalog.Problems.Count > 0) status += " Пропущено: " + catalog.Problems.Count + ". " + catalog.Problems[0];
                if (catalog.Limited) status += " Выбери папку ближе к пакам, чтобы увидеть всю коллекцию.";
                Text("WorkshopStatus", status); C<TextBlock>("WorkshopStatus").ToolTip = String.Join(Environment.NewLine, catalog.Problems.Take(10));
                Text("PageLabel", "Локальная коллекция");
            }
            catch (OperationCanceledException) { }
            finally { if (folderScan == cancellation) folderScan = null; cancellation.Dispose(); }
        }

        static bool LocalLanguage(string value, int language)
        {
            value = (value ?? "").Trim().ToLowerInvariant();
            return language <= 0 || (language == 1 ? value == "ru" || value.StartsWith("rus") || value.StartsWith("рус") : value == "en" || value.StartsWith("eng") || value.StartsWith("анг"));
        }

        void OpenPacksFolder()
        {
            string path = settings.LocalPacks ? PacksFolder : Paths.Packs;
            if (!Directory.Exists(path)) { Toast("Папка недоступна. Выбери другую папку."); return; }
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
    }
}

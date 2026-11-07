using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace BrokenPhone
{
    public sealed partial class GameWindow
    {
        sealed class PackCardControls
        {
            public Button Download, Cancel;
            public CheckBox Select;
            public ProgressBar Progress;
            public TextBlock Status, Metadata;
        }
        PackDownloadQueue packDownloads;
        readonly Dictionary<string, PackCardControls> packControls = new Dictionary<string, PackCardControls>();
        readonly Dictionary<string, WorkshopItem> selectedDownloads = new Dictionary<string, WorkshopItem>();
        void InitializeWorkshopDownloads()
        {
            packDownloads = new PackDownloadQueue((item, progress, token) => workshop.DownloadProgress(item, progress, token));
            AttachDownloadQueue();
            B("DownloadSelectedButton").Click += delegate { foreach (var item in selectedDownloads.Values.ToArray()) QueuePack(item); };
            B("CancelAllPacksButton").Click += delegate { packDownloads.CancelAll(); };
        }
        void AttachDownloadQueue() { packDownloads.Changed += delegate { if (!disposed) window.Dispatcher.BeginInvoke(new Action(() => { if (!disposed) RenderDownloadJobs(); })); }; }
        void QueuePack(WorkshopItem item)
        {
            selectedDownloads.Remove(item.Id); packDownloads.Add(item); RenderDownloadJobs();
        }
        Pack InstalledItem(string id) { return installed.FirstOrDefault(p => p.workshopId == id); }
        void WorkshopCard(WorkshopItem item)
        {
            var body = CardBase(item.Title, "STEAM WORKSHOP · " + item.Id, "Реплик: " + (item.Lines > 0 ? item.Lines.ToString() : "—") + " · Ролей: " + (item.Roles > 0 ? item.Roles.ToString() : "—"), item.Preview);
            var controls = new PackCardControls { Metadata = body.Children[3] as TextBlock };
            controls.Select = new CheckBox { Content = "В пакетную загрузку", Foreground = Brush("#BDB2CA"), FontSize = 11, Margin = new Thickness(0, 0, 0, 9), IsChecked = selectedDownloads.ContainsKey(item.Id) };
            controls.Select.Checked += delegate { selectedDownloads[item.Id] = item; RenderDownloadToolbar(); };
            controls.Select.Unchecked += delegate { selectedDownloads.Remove(item.Id); RenderDownloadToolbar(); };
            body.Children.Add(controls.Select);
            controls.Progress = new ProgressBar { Minimum = 0, Maximum = 1, Height = 6, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 5) }; body.Children.Add(controls.Progress);
            controls.Status = new TextBlock { FontSize = 10, Height = 17, Foreground = Brush("#CFB3D9"), TextTrimming = TextTrimming.CharacterEllipsis }; body.Children.Add(controls.Status);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            controls.Download = SmallButton("↓ Загрузить", delegate { var pack = InstalledItem(item.Id); if (pack == null) QueuePack(item); else SelectPack(pack); }); controls.Download.Background = Brush("#C5B4D2"); controls.Download.Foreground = Brush("#2A1D2B"); row.Children.Add(controls.Download);
            controls.Cancel = SmallButton("Отмена", delegate { packDownloads.Cancel(item.Id); }); controls.Cancel.Visibility = Visibility.Collapsed; controls.Cancel.Margin = new Thickness(7, 0, 0, 0); row.Children.Add(controls.Cancel);
            var page = SmallButton("Страница ↗", delegate { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(item.Url) { UseShellExecute = true }); }); page.Margin = new Thickness(7, 0, 0, 0); row.Children.Add(page);
            body.Children.Add(row); packControls[item.Id] = controls; ((Border)body.Parent).Tag = "download-card"; UpdateWorkshopLayout(); RenderDownloadJobs();
        }
        void RenderDownloadToolbar()
        {
            var jobs = packDownloads.Snapshot(); int busy = jobs.Count(j => j.Busy);
            B("DownloadSelectedButton").Content = "↓ Скачать выбранные (" + selectedDownloads.Count + ")"; B("DownloadSelectedButton").IsEnabled = selectedDownloads.Count > 0;
            B("CancelAllPacksButton").IsEnabled = busy > 0; Text("PackQueueStatus", busy > 0 ? "Загружается или в очереди: " + busy : "Отметь несколько паков для загрузки");
        }
        void RenderDownloadJobs()
        {
            var jobs = packDownloads.Snapshot();
            foreach (var job in jobs.Where(j => j.State == "ready" && j.Pack != null)) if (InstalledItem(job.Item.Id) == null) installed.Add(job.Pack);
            foreach (var entry in packControls)
            {
                var c = entry.Value; var job = jobs.FirstOrDefault(j => j.Item.Id == entry.Key); var local = InstalledItem(entry.Key); bool busy = job != null && job.Busy;
                c.Download.IsEnabled = !busy && (local == null || IsHost && state != null && state.phase == "lobby");
                c.Download.Content = busy ? job.State == "queued" ? "В очереди…" : "Загружается…" : local != null ? IsHost ? "Выбрать в лобби" : "✓ Загружен" : job != null && job.State == "error" ? "Повторить" : "↓ Загрузить";
                c.Select.IsEnabled = !busy && local == null; if (!c.Select.IsEnabled) c.Select.IsChecked = false;
                c.Cancel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
                c.Progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
                c.Progress.IsIndeterminate = busy && job.Progress.Fraction < 0; c.Progress.Value = busy ? Math.Max(0, job.Progress.Fraction) : 0;
                if (local != null) c.Metadata.Text = "Реплик: " + local.DisplayLines + " · Ролей: " + local.DisplayRoles;
                c.Status.Text = job == null ? local != null ? "✓ В моей коллекции" : "" : job.Progress.Label; c.Status.ToolTip = c.Status.Text;
            }
            RenderDownloadToolbar();
            if (downloadCancellation != null) return;
            var active = jobs.FirstOrDefault(j => j.State == "loading") ?? jobs.FirstOrDefault(j => j.Busy);
            C<Border>("DownloadBar").Visibility = active == null ? Visibility.Collapsed : Visibility.Visible;
            if (active != null)
            {
                Text("DownloadStatus", active.Item.Title + " · " + active.Progress.Label); B("CancelDownloadButton").IsEnabled = true; B("CancelDownloadButton").Content = "Отменить";
                C<ProgressBar>("DownloadProgress").IsIndeterminate = active.Progress.Fraction < 0; C<ProgressBar>("DownloadProgress").Value = Math.Max(0, active.Progress.Fraction);
            }
        }
    }
}

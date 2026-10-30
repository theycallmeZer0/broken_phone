using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BrokenPhone
{
    public sealed class PackProgress
    {
        public string Stage = "";
        public long Received, Total;
        public double Fraction = -1;
        public static PackProgress Step(string text) { return new PackProgress { Stage = text }; }
        public static PackProgress Bytes(string text, long received, long total) { return new PackProgress { Stage = text, Received = received, Total = total, Fraction = total > 0 ? Math.Min(1, (double)received / total) : -1 }; }
        public string Label
        {
            get { return (Fraction >= 0 ? (Fraction * 100).ToString("0") + "% · " : "") + Stage + (Received > 0 ? " · " + (Received / 1048576.0).ToString("0.0") + (Total > 0 ? " / " + (Total / 1048576.0).ToString("0.0") : "") + " МБ" : ""); }
        }
    }
    public sealed class PackJob
    {
        public WorkshopItem Item;
        public string State = "queued", Error;
        public PackProgress Progress = PackProgress.Step("В очереди");
        public Pack Pack;
        internal CancellationTokenSource CancelSource;
        internal TaskCompletionSource<Pack> Done;
        internal long LastReport;
        public Task<Pack> Completion { get { return Done.Task; } }
        public bool Busy { get { return State == "queued" || State == "loading"; } }
    }
    public sealed class PackDownloadQueue : IDisposable
    {
        readonly object gate = new object();
        readonly Dictionary<string, PackJob> jobs = new Dictionary<string, PackJob>();
        readonly SemaphoreSlim slots = new SemaphoreSlim(2, 2);
        readonly Func<WorkshopItem, Action<PackProgress>, CancellationToken, Task<Pack>> load;
        bool disposed;
        public event Action Changed;
        public PackDownloadQueue(Func<WorkshopItem, Action<PackProgress>, CancellationToken, Task<Pack>> loader) { load = loader; }
        public PackJob[] Snapshot()
        {
            lock (gate) return jobs.Values.Select(j => new PackJob { Item = j.Item, State = j.State, Error = j.Error, Progress = j.Progress, Pack = j.Pack }).ToArray();
        }
        public PackJob Add(WorkshopItem item)
        {
            PackJob job;
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException("PackDownloadQueue");
                if (jobs.TryGetValue(item.Id, out job) && (job.Busy || job.State == "ready")) return job;
                job = new PackJob { Item = item, CancelSource = new CancellationTokenSource(), Done = new TaskCompletionSource<Pack>(TaskCreationOptions.RunContinuationsAsynchronously) }; jobs[item.Id] = job;
            }
            Notify(); var ignored = Run(job); return job;
        }
        async Task Run(PackJob job)
        {
            bool entered = false;
            try
            {
                await slots.WaitAsync(job.CancelSource.Token).ConfigureAwait(false); entered = true;
                lock (gate) { job.State = "loading"; job.Progress = PackProgress.Step("Подключаемся к Steam…"); } Notify();
                Pack pack = await load(job.Item, p => Report(job, p), job.CancelSource.Token).ConfigureAwait(false);
                job.CancelSource.Token.ThrowIfCancellationRequested();
                lock (gate) { job.Pack = pack; job.State = "ready"; job.Progress = new PackProgress { Stage = "Готово", Fraction = 1 }; }
                job.Done.TrySetResult(pack);
            }
            catch (OperationCanceledException) { lock (gate) { job.State = "cancelled"; job.Progress = PackProgress.Step("Загрузка отменена"); } job.Done.TrySetCanceled(); }
            catch (Exception e) { lock (gate) { job.State = "error"; job.Error = e.GetBaseException().Message; job.Progress = PackProgress.Step(job.Error); } job.Done.TrySetException(e); }
            finally { if (entered) slots.Release(); Notify(); }
        }
        void Report(PackJob job, PackProgress progress)
        {
            bool notify;
            lock (gate)
            {
                if (!job.Busy || job.CancelSource.IsCancellationRequested) return;
                notify = progress.Stage != job.Progress.Stage || Environment.TickCount - job.LastReport > 120 || progress.Fraction >= 1;
                job.Progress = progress; if (notify) job.LastReport = Environment.TickCount;
            }
            if (notify) Notify();
        }
        void Notify() { var changed = Changed; if (changed != null && !disposed) changed(); }
        public void Cancel(string id) { lock (gate) { PackJob job; if (jobs.TryGetValue(id, out job) && job.Busy) job.CancelSource.Cancel(); } }
        public void CancelAll() { lock (gate) foreach (var job in jobs.Values.Where(j => j.Busy)) job.CancelSource.Cancel(); }
        public bool Busy { get { lock (gate) return jobs.Values.Any(j => j.Busy); } }
        public void Dispose() { disposed = true; CancelAll(); }
    }
}

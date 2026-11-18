using System.IO;
using System.Text.Json;

namespace LineProductionWPF
{
    /// <summary>When a job became current / finished. Kept on disk so a restart of the app does not lose the count.</summary>
    public class JobTiming
    {
        public DateTime? Start { get; set; }
        public DateTime? End { get; set; }
        public bool Unobserved { get; set; }   // job was already complete when the app first saw it: its start is unknown
        public double PausedSeconds { get; set; }   // time the counter was paused while this job was running (not counted)
    }

    /// <summary>The numbers of the four kanban boards (big = current job, small = whole day).</summary>
    public class KanbanNumbers
    {
        public KanbanJob? Current { get; set; }
        public bool AllDone { get; set; }

        public int TargetJob, TargetDay;     // board 1: planned quantity
        public int ActualJob, ActualDay;     // board 2: scanned
        public int CountJob, CountDay;       // board 3: expected at the job's pcs/hour since it became current
        public int SelisihJob, SelisihDay;   // board 4: actual - count
    }

    /// <summary>
    /// Decides which job is current and turns the schedule + scan numbers into the board numbers.
    ///  - Current job = first job whose scanned count is below its Jumlah (all done: the last job).
    ///  - The first job starts at 08:00; each later job starts when the previous job completes.
    ///  - Count = elapsed seconds x pcs per hour / 3600, never above Jumlah, frozen when the job completes
    ///    and while the counter is paused.
    ///  - Day numbers add up every job; the job numbers restart when the next job becomes current.
    /// </summary>
    public class KanbanTracker
    {
        private readonly Dictionary<int, JobTiming> _timings = new Dictionary<int, JobTiming>();   // by urutan
        private string _key = "";
        private DateTime _firstJobStart;
        private DateTime? _pausedAt;               // set while the counter is paused ("Jeda Counter")
        private const int PauseKey = -1;           // _timings entry used to keep the pause on disk (no job has urutan -1)

        public bool IsPaused => _pausedAt != null;

        /// <summary>Pause the count (pcs/hour x elapsed time). Scans are still saved; only the expected count stops.</summary>
        public void Pause(DateTime now)
        {
            if (_pausedAt != null) return;
            _pausedAt = now;
            Save();
        }

        /// <summary>Continue the count: the paused time is taken out of every job it overlapped.</summary>
        public void Resume(DateTime now)
        {
            if (_pausedAt == null) return;
            DateTime from = _pausedAt.Value;
            foreach (var t in _timings.Values)
            {
                if (t.Start == null) continue;
                DateTime a = t.Start.Value > from ? t.Start.Value : from;
                DateTime end = t.End ?? now;
                DateTime b = end < now ? end : now;
                if (b > a) t.PausedSeconds += (b - a).TotalSeconds;
            }
            _pausedAt = null;
            Save();
        }

        private static string StatePath
        {
            get
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LineProductionWPF");
                return Path.Combine(dir, "kanban_state.json");
            }
        }

        /// <summary>Switch to another date + line: loads the saved timings of that day (if any).</summary>
        public void Open(DateTime date, int lineId)
        {
            _key = date.ToString("yyyy-MM-dd") + "|" + lineId;
            _firstJobStart = date.Date.AddHours(8);
            _timings.Clear();
            _pausedAt = null;
            try
            {
                if (!File.Exists(StatePath)) return;
                var all = JsonSerializer.Deserialize<Dictionary<string, Dictionary<int, JobTiming>>>(File.ReadAllText(StatePath));
                if (all != null && all.TryGetValue(_key, out var saved))
                    foreach (var kv in saved) _timings[kv.Key] = kv.Value;
                if (_timings.Remove(PauseKey, out var pause)) _pausedAt = pause.Start;
            }
            catch { /* unreadable state file: start the day's timing again */ }
        }

        private void Save()
        {
            try
            {
                Dictionary<string, Dictionary<int, JobTiming>> all = new Dictionary<string, Dictionary<int, JobTiming>>();
                if (File.Exists(StatePath))
                {
                    try { all = JsonSerializer.Deserialize<Dictionary<string, Dictionary<int, JobTiming>>>(File.ReadAllText(StatePath)) ?? all; }
                    catch { }
                }

                // keep only the last few days
                foreach (string old in all.Keys.Where(k => string.CompareOrdinal(k.Substring(0, 10), DateTime.Today.AddDays(-7).ToString("yyyy-MM-dd")) < 0).ToList())
                    all.Remove(old);

                var toSave = new Dictionary<int, JobTiming>(_timings);
                if (_pausedAt != null) toSave[PauseKey] = new JobTiming { Start = _pausedAt };
                all[_key] = toSave;
                Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
                File.WriteAllText(StatePath, JsonSerializer.Serialize(all));
            }
            catch { /* the board still works without the file */ }
        }

        /// <summary>New scan numbers arrived: note which jobs became current / completed, in schedule order.</summary>
        public void Update(IList<KanbanJob> jobs, DateTime now)
        {
            bool changed = false;
            bool previousComplete = true;

            for (int i = 0; i < jobs.Count; i++)
            {
                var job = jobs[i];
                if (!_timings.TryGetValue(job.Urutan, out var t))
                {
                    if (!previousComplete) break;   // not reached yet
                    t = new JobTiming();
                    _timings[job.Urutan] = t;
                    changed = true;
                }

                if (i == 0)
                {
                    if (t.Start != _firstJobStart || t.Unobserved)
                    {
                        t.Start = _firstJobStart;
                        t.Unobserved = false;
                        changed = true;
                    }
                }
                else if (t.Start == null && previousComplete)
                {
                    t.Start = now;
                    if (job.IsComplete) t.Unobserved = true;   // it was done before we looked
                    changed = true;
                }

                if (job.IsComplete && t.End == null && t.Start != null)
                {
                    t.End = now;
                    changed = true;
                }

                previousComplete = job.IsComplete;
                if (!previousComplete) break;   // jobs after the current one have not started
            }

            if (changed) Save();
        }

        private int CountOf(KanbanJob job, DateTime now)
        {
            if (!_timings.TryGetValue(job.Urutan, out var t) || t.Start == null) return 0;
            if (t.Unobserved) return job.Jumlah;

            DateTime end = t.End ?? now;
            if (_pausedAt != null && _pausedAt.Value < end) end = _pausedAt.Value;   // paused: the count stands still
            double seconds = Math.Max(0, (end - t.Start.Value).TotalSeconds - t.PausedSeconds);
            int count = (int)(seconds * job.Target / 3600.0);
            return Math.Min(count, job.Jumlah);
        }

        public KanbanNumbers Calculate(IList<KanbanJob> jobs, DateTime now)
        {
            var n = new KanbanNumbers();
            if (jobs.Count == 0) return n;

            var current = jobs.FirstOrDefault(j => !j.IsComplete);
            n.AllDone = current == null;
            n.Current = current ?? jobs[jobs.Count - 1];

            foreach (var job in jobs)
            {
                job.Count = CountOf(job, now);
                job.Status = job.IsComplete ? "Selesai" : ReferenceEquals(job, n.Current) ? "Berjalan" : "Menunggu";

                n.TargetDay += job.Jumlah;
                n.ActualDay += job.Discan;
                n.CountDay += job.Count;
            }

            n.TargetJob = n.Current.Jumlah;
            n.ActualJob = n.Current.Discan;
            n.CountJob = n.Current.Count;
            n.SelisihJob = n.ActualJob - n.CountJob;
            n.SelisihDay = n.ActualDay - n.CountDay;
            return n;
        }
    }
}

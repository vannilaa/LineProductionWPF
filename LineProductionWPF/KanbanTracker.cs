using System.IO;
using System.Text.Json;
using ProductionTime.Shared;

namespace LineProductionWPF
{
    /// <summary>A stretch of time during which the counter was paused ("Jeda Counter"). To is null while it is still paused.</summary>
    public class PauseSpan
    {
        public DateTime From { get; set; }
        public DateTime? To { get; set; }
    }

    /// <summary>The numbers of the four kanban boards (big = current job, small = whole day).</summary>
    public class KanbanNumbers
    {
        public KanbanJob? Current { get; set; }
        public bool AllDone { get; set; }

        public int TargetJob, TargetDay;     // board 1: planned quantity
        public int ActualJob, ActualDay;     // board 2: scanned
        public int CountJob, CountDay;       // board 3: expected at the job's pcs/hour since it started
        public int SelisihJob, SelisihDay;   // board 4: actual - count
    }

    /// <summary>
    /// Uses shared cycle-time results and adds the board numbers, local counter pauses, and job status.
    ///  - Current job = first job whose scanned count is below its Jumlah (all done: the last job).
    ///  - Time is WORKING time (see WorkCalendar): breaks and time outside the shift do not count.
    ///  - The first job starts at the first working moment of the day, or when the schedule was created if that was later.
    ///    Each later job starts when the previous job really finished (the time of its last scan in the database). A job
    ///    added to today's schedule starts no earlier than its added_at time, matching DailyScheduler.
    ///  - Count = working seconds since the job started x pcs per hour / 3600, never above Jumlah, frozen when the job
    ///    completes. "Jeda Counter" takes the paused time out of the count (the Count board only, not the job times).
    ///  - Job times: finished = real start / end. Running = expected end from the pieces still to scan at the planned rate.
    ///    Waiting = starts when the previous job is expected to end. Cycle-time calculations are shared with DailyScheduler.
    ///  - Day numbers add up every job; the job numbers restart when the next job becomes current.
    /// Nothing about the jobs is kept on this PC: they all come from the database, so every station shows the same times.
    /// Only the pauses are kept (kanban_pauses.json), because a pause is only a way to pace the workers on this station.
    /// </summary>
    public class KanbanTracker
    {
        private readonly List<PauseSpan> _pauses = new List<PauseSpan>();
        private string _key = "";
        private DateTime _date = DateTime.Today;

        private static string StatePath
        {
            get
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LineProductionWPF");
                return Path.Combine(dir, "kanban_pauses.json");
            }
        }

        public bool IsPaused => _pauses.Count > 0 && _pauses[_pauses.Count - 1].To == null;

        /// <summary>Switch to another date + line: loads the pauses saved for that day (if any).</summary>
        public void Open(DateTime date, int lineId)
        {
            _date = date.Date;
            _key = date.ToString("yyyy-MM-dd") + "|" + lineId;
            _pauses.Clear();
            try
            {
                if (!File.Exists(StatePath)) return;
                var all = JsonSerializer.Deserialize<Dictionary<string, List<PauseSpan>>>(File.ReadAllText(StatePath));
                if (all != null && all.TryGetValue(_key, out var saved)) _pauses.AddRange(saved);
            }
            catch { /* unreadable state file: start the day without pauses */ }
        }

        /// <summary>Pause the count (pcs/hour x working time). Scans are still saved; only the expected count stops.</summary>
        public void Pause(DateTime now)
        {
            if (IsPaused) return;
            _pauses.Add(new PauseSpan { From = now });
            Save();
        }

        /// <summary>Continue the count: the paused time stays out of it.</summary>
        public void Resume(DateTime now)
        {
            if (!IsPaused) return;
            _pauses[_pauses.Count - 1].To = now;
            Save();
        }

        private void Save()
        {
            try
            {
                var all = new Dictionary<string, List<PauseSpan>>();
                if (File.Exists(StatePath))
                {
                    try { all = JsonSerializer.Deserialize<Dictionary<string, List<PauseSpan>>>(File.ReadAllText(StatePath)) ?? all; }
                    catch { }
                }

                // keep only the last few days
                foreach (string old in all.Keys.Where(k => k.Length >= 10 &&
                             string.CompareOrdinal(k.Substring(0, 10), DateTime.Today.AddDays(-7).ToString("yyyy-MM-dd")) < 0).ToList())
                    all.Remove(old);

                all[_key] = new List<PauseSpan>(_pauses);
                Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
                File.WriteAllText(StatePath, JsonSerializer.Serialize(all));
            }
            catch { /* the board still works without the file */ }
        }

        /// <summary>Working seconds between from and to during which the counter was paused.</summary>
        private double PausedWorkingSeconds(DateTime from, DateTime to, DateTime now)
        {
            double total = 0;
            foreach (PauseSpan p in _pauses)
            {
                DateTime a = p.From > from ? p.From : from;
                DateTime end = p.To ?? now;
                DateTime b = end < to ? end : to;
                if (b > a) total += WorkCalendar.WorkingSeconds(a, b);
            }
            return total;
        }

        public KanbanNumbers Calculate(IList<KanbanJob> jobs, DateTime now)
        {
            var n = new KanbanNumbers();
            if (jobs.Count == 0) return n;

            var current = jobs.FirstOrDefault(j => !j.IsComplete);
            n.AllDone = current == null;
            n.Current = current ?? jobs[jobs.Count - 1];

            var inputs = jobs.Select(job => new ScheduleJobTimeInput(
                job.Jumlah, job.Target, job.Discan, job.LastScanAt, job.AddedAt)).ToList();
            IReadOnlyList<ScheduleJobTimeResult> timings = ScheduleTimeCalculator.Calculate(
                inputs, _date, jobs[0].ScheduleCreatedAt, now);

            for (int i = 0; i < jobs.Count; i++)
            {
                KanbanJob job = jobs[i];
                ScheduleJobTimeResult timing = timings[i];
                if (!timing.IsUsable)
                {
                    job.Count = 0;
                    job.Status = job.IsComplete ? "Selesai" : ReferenceEquals(job, n.Current) ? "Berjalan" : "Menunggu";
                    job.Mulai = null;
                    job.Selesai = null;
                    job.PlannedEnd = null;
                    job.IsProjected = false;
                    job.MulaiNote = "";
                    job.SelesaiNote = "";
                    job.DiffNote = "";
                    job.DiffMinutes = null;
                }
                else
                {
                    DateTime start = timing.Start!.Value;
                    DateTime end = timing.End!.Value;
                    DateTime countUntil = timing.IsComplete ? end : timing.IsCurrent ? now : start;
                    double elapsed = WorkCalendar.WorkingSeconds(start, countUntil) - PausedWorkingSeconds(start, countUntil, now);
                    job.Count = (int)Math.Min(Math.Max(0, elapsed) * job.Target / 3600.0, job.Jumlah);
                    job.Status = job.IsComplete ? "Selesai" : ReferenceEquals(job, n.Current) ? "Berjalan" : "Menunggu";
                    job.Mulai = timing.Start;
                    job.Selesai = timing.End;
                    job.PlannedEnd = timing.PlannedEnd;
                    job.IsProjected = !timing.IsComplete;
                    job.DiffMinutes = timing.DifferenceMinutes;
                    job.MulaiNote = timing.StartNote.Length == 0 ? "" : "(" + timing.StartNote + ")";
                    job.SelesaiNote = timing.EndNote.Length == 0 ? "" : "(" + timing.EndNote + ")";
                    job.DiffNote = timing.DifferenceNote.Length == 0 ? "" : "(" + timing.DifferenceNote + ")";
                }

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

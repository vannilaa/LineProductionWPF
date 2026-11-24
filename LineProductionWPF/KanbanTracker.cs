using System.IO;
using System.Text.Json;

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
    /// Decides which job is current and turns the schedule + scan numbers into the board numbers and the job times.
    ///  - Current job = first job whose scanned count is below its Jumlah (all done: the last job).
    ///  - Time is WORKING time (see WorkCalendar): breaks and time outside the shift do not count.
    ///  - The first job starts at the first working moment of the day, or when the schedule was created if that was later.
    ///    Each later job starts when the previous job really finished (the time of its last scan in the database).
    ///  - Count = working seconds since the job started x pcs per hour / 3600, never above Jumlah, frozen when the job
    ///    completes. "Jeda Counter" takes the paused time out of the count (the Count board only, not the job times).
    ///  - Job times: finished = real start / end. Running = expected end from the pieces still to scan at the planned rate.
    ///    Waiting = starts when the previous job is expected to end. Selisih Waktu = planned end - expected end in
    ///    WORKING minutes (the break is not lateness; negative = late), where the plan has every job on time, back to back.
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

        /// <summary>When the first job starts: the first working moment of the day, or the creation of the schedule if that was later.</summary>
        private DateTime AnchorOf(KanbanJob first)
        {
            DateTime anchor = WorkCalendar.DayStart(_date);
            DateTime? created = first.ScheduleCreatedAt;
            if (created != null && created.Value.Date == _date && created.Value > anchor) anchor = created.Value;
            return WorkCalendar.NextWorking(anchor);
        }

        /// <summary>End of the shift of that day as text: "17:00" (Friday "17:15").</summary>
        private static string ShiftEndText(DateTime t) =>
            WorkCalendar.DayEnd(t).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>The job started right after a break because the previous job ended before or at the start of it.</summary>
        private static bool StartedAfterBreak(DateTime raw, DateTime start)
        {
            if (start <= raw) return false;
            var (breakStart, breakEnd) = WorkCalendar.BreakOf(raw);
            return raw >= breakStart && raw < breakEnd && start >= breakEnd;
        }

        public KanbanNumbers Calculate(IList<KanbanJob> jobs, DateTime now)
        {
            var n = new KanbanNumbers();
            if (jobs.Count == 0) return n;

            var current = jobs.FirstOrDefault(j => !j.IsComplete);
            n.AllDone = current == null;
            n.Current = current ?? jobs[jobs.Count - 1];

            DateTime anchor = AnchorOf(jobs[0]);
            DateTime plannedCursor = anchor;   // end of the previous job in the plan
            DateTime actualCursor = anchor;    // end of the previous job in reality / expectation

            foreach (var job in jobs)
            {
                double needSeconds = job.Target > 0 ? job.Jumlah * 3600.0 / job.Target : 0;   // working time the whole job needs

                // the plan: every job on time, back to back
                DateTime plannedStart = WorkCalendar.NextWorking(plannedCursor);
                DateTime plannedEnd = WorkCalendar.AddWorkingSeconds(plannedStart, needSeconds);
                plannedCursor = plannedEnd;

                // what really happened (finished job) or is expected (running / waiting job)
                DateTime start = WorkCalendar.NextWorking(actualCursor);
                DateTime end;
                bool isCurrent = current != null && ReferenceEquals(job, current);
                if (job.IsComplete)
                {
                    end = job.LastScanAt ?? start;
                    if (end < start) end = start;
                }
                else if (isCurrent)
                {
                    double remaining = job.Target > 0 ? Math.Max(0, job.Jumlah - job.Discan) * 3600.0 / job.Target : 0;
                    end = WorkCalendar.AddWorkingSeconds(now > start ? now : start, remaining);
                }
                else
                {
                    end = WorkCalendar.AddWorkingSeconds(start, needSeconds);
                }

                // the count (expected pieces): working time since the start, minus the pauses ("Jeda Counter")
                DateTime countUntil = job.IsComplete ? end : isCurrent ? now : start;
                double elapsed = WorkCalendar.WorkingSeconds(start, countUntil) - PausedWorkingSeconds(start, countUntil, now);
                job.Count = (int)Math.Min(Math.Max(0, elapsed) * job.Target / 3600.0, job.Jumlah);
                job.Status = job.IsComplete ? "Selesai" : ReferenceEquals(job, n.Current) ? "Berjalan" : "Menunggu";

                // the times shown in the job table
                job.Mulai = start;
                job.Selesai = end;
                job.PlannedEnd = plannedEnd;
                job.IsProjected = !job.IsComplete;
                // Selisih Waktu is counted in WORKING minutes: the break between the two ends is not lateness.
                double diffSeconds = end >= plannedEnd
                    ? WorkCalendar.WorkSecondsWithOvertime(plannedEnd, end)
                    : -WorkCalendar.WorkSecondsWithOvertime(end, plannedEnd);
                job.DiffMinutes = (int)Math.Truncate(diffSeconds / 60.0);   // under a minute counts as 0

                // Say so when a time contains the break (the times are clock times; Selisih Waktu leaves the break out).
                string breakText = WorkCalendar.BreakText(start);
                job.MulaiNote = StartedAfterBreak(actualCursor, start) ? "(setelah istirahat " + breakText + ")" : "";

                var endNotes = new List<string>();
                if (WorkCalendar.SpansBreak(start, end)) endNotes.Add("+istirahat " + breakText);
                // Only the clock time is stated ("lewat 17:00"): the word "lembur" would sound like paid overtime.
                if (WorkCalendar.IsAfterShift(end)) endNotes.Add("lewat " + ShiftEndText(end));
                job.SelesaiNote = endNotes.Count == 0 ? "" : "(" + string.Join(", ", endNotes) + ")";

                DateTime early = plannedEnd < end ? plannedEnd : end;
                DateTime late = plannedEnd < end ? end : plannedEnd;
                var diffNotes = new List<string>();
                if (job.DiffMinutes != 0 && WorkCalendar.SpansBreak(early, late)) diffNotes.Add("tidak termasuk istirahat " + breakText);
                if (job.DiffMinutes != 0 && WorkCalendar.IsAfterShift(late)) diffNotes.Add("lewat " + ShiftEndText(late));
                job.DiffNote = diffNotes.Count == 0 ? "" : "(" + string.Join(", ", diffNotes) + ")";

                actualCursor = end;

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

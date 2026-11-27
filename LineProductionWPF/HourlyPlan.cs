using System.Globalization;
using ProductionTime.Shared;

namespace LineProductionWPF
{
    /// <summary>One row of the "Jadwal Produksi (per Jam)" table: one part number inside one clock hour.</summary>
    public class HourSlotRow
    {
        public int Hour { get; set; }                 // clock hour, 8 = 08:00-09:00
        public string Barcode { get; set; } = "";
        public int Urutan { get; set; }               // schedule row the target comes from (0 = not planned in this hour)

        public string JamText { get; set; } = "";     // "1", "2", ... only on the first row of the hour
        public string CycleText { get; set; } = "";   // "08:00-09:00", only on the first row of the hour
        public string PartNumber { get; set; } = "";
        public string Model { get; set; } = "";
        public string PosisiPart { get; set; } = "";
        public int Actual { get; set; }
        public int Target { get; set; }
        public string Note { get; set; } = "";        // "di luar rencana" for scans in an hour the plan did not have them

        public bool IsFirstOfHour { get; set; }
        public bool IsCurrentHour { get; set; }
        public bool IsCurrentJob { get; set; }        // current hour + the job that is running now (same Urutan, not just same part number)
        /// <summary>"" = hour not over yet (or nothing planned), "Ok" = target reached, "Short" = below target.</summary>
        public string ActualKind { get; set; } = "";

        public string Key => Hour + "|" + Barcode + "|" + Urutan;

        public void CopyValuesFrom(HourSlotRow o)
        {
            JamText = o.JamText; CycleText = o.CycleText; PartNumber = o.PartNumber; Model = o.Model;
            PosisiPart = o.PosisiPart; Actual = o.Actual; Target = o.Target; Note = o.Note;
            IsFirstOfHour = o.IsFirstOfHour; IsCurrentHour = o.IsCurrentHour; IsCurrentJob = o.IsCurrentJob;
            ActualKind = o.ActualKind;
        }
    }

    /// <summary>
    /// Splits the FIXED plan (planned start / end of every job, see KanbanTracker) into clock hours (:00 marks).
    ///  - Target of a part in an hour = its Jumlah spread over the planned working seconds (rounded so the hours add up
    ///    to Jumlah). An hour that is only partly working time (start 07:30, overtime from 18:30, ...) gets its share,
    ///    e.g. 30 minutes = half the target per jam. Break time counts for nothing; an hour that is all break is not shown.
    ///  - Actual = real scans of that part number during that clock hour. Scans in an hour the plan did not have that
    ///    part get their own row with target 0 ("di luar rencana"), so no scan is missing from the table.
    /// LineProductionWPF only: DailyScheduler does not use this table.
    /// </summary>
    public static class HourlyPlanBuilder
    {
        public static List<HourSlotRow> Build(IList<KanbanJob> jobs, IReadOnlyDictionary<(string Barcode, int Hour), int> scans,
                                              KanbanJob? current, DateTime date, DateTime now)
        {
            var planned = new List<HourSlotRow>();
            foreach (KanbanJob job in jobs)
            {
                if (job.Jumlah <= 0 || job.Target <= 0 || !job.PlannedStart.HasValue || !job.PlannedEnd.HasValue) continue;
                DateTime start = job.PlannedStart.Value, end = job.PlannedEnd.Value;

                // working seconds of the job per clock hour
                var slices = new List<(DateTime HourStart, double Seconds)>();
                for (DateTime t = start; t < end;)
                {
                    DateTime hourStart = new DateTime(t.Year, t.Month, t.Day, t.Hour, 0, 0);
                    DateTime next = hourStart.AddHours(1);
                    DateTime sliceEnd = next < end ? next : end;
                    double seconds = WorkCalendar.WorkSecondsWithOvertime(t, sliceEnd);
                    if (seconds > 0) slices.Add((hourStart, seconds));
                    t = sliceEnd;
                }
                if (slices.Count == 0) slices.Add((new DateTime(start.Year, start.Month, start.Day, start.Hour, 0, 0), 1));

                double total = slices.Sum(s => s.Seconds), cumulative = 0;
                int given = 0;
                foreach (var slice in slices)
                {
                    cumulative += slice.Seconds;
                    int upTo = (int)Math.Round(job.Jumlah * cumulative / total, MidpointRounding.AwayFromZero);
                    planned.Add(NewRow(job, slice.HourStart.Hour, upTo - given));
                    given = upTo;
                }
            }

            // actual scans: the first planned row of that part in that hour takes them; otherwise a row of its own
            var rows = new List<HourSlotRow>(planned);
            var taken = new HashSet<(string, int)>();
            foreach (HourSlotRow row in planned)
            {
                if (!taken.Add((row.Barcode, row.Hour))) continue;
                row.Actual = scans.TryGetValue((row.Barcode, row.Hour), out int n) ? n : 0;
            }
            foreach (var scan in scans.Where(s => s.Value > 0 && !taken.Contains((s.Key.Barcode, s.Key.Hour))))
            {
                KanbanJob? job = jobs.FirstOrDefault(j => string.Equals(j.Barcode, scan.Key.Barcode, StringComparison.OrdinalIgnoreCase));
                HourSlotRow extra = job != null ? NewRow(job, scan.Key.Hour, 0) : new HourSlotRow { Hour = scan.Key.Hour, Barcode = scan.Key.Barcode, PartNumber = scan.Key.Barcode };
                extra.Urutan = 0;
                extra.Actual = scan.Value;
                extra.Note = "di luar rencana";
                rows.Add(extra);
            }

            // hour order; inside an hour: planned rows in schedule order, unplanned scans last
            rows = rows.Select((r, i) => (r, i))
                       .OrderBy(x => x.r.Hour).ThenBy(x => x.r.Urutan == 0 ? 1 : 0).ThenBy(x => x.i)
                       .Select(x => x.r).ToList();

            bool today = date.Date == now.Date;
            int jam = 0, lastHour = -1;
            foreach (HourSlotRow row in rows)
            {
                if (row.Hour != lastHour)
                {
                    jam++;
                    lastHour = row.Hour;
                    row.IsFirstOfHour = true;
                    row.JamText = jam.ToString(CultureInfo.InvariantCulture);
                    row.CycleText = row.Hour.ToString("00") + ":00-" + ((row.Hour + 1) % 24).ToString("00") + ":00";
                }
                row.IsCurrentHour = today && row.Hour == now.Hour;
                // The running job is matched by its schedule row (Urutan), like the per-job tab: the same part number
                // can be in the schedule twice, and only the row of the job that runs now may light up.
                // An unplanned row (Urutan 0, "di luar rencana") of that part this hour = scans of the running job
                // when it runs late, so it lights up too.
                row.IsCurrentJob = row.IsCurrentHour && current != null && !current.IsComplete &&
                                   string.Equals(row.Barcode, current.Barcode, StringComparison.OrdinalIgnoreCase) &&
                                   (row.Urutan == current.Urutan || row.Urutan == 0);
                bool hourOver = date.Date < now.Date || (today && row.Hour < now.Hour);
                row.ActualKind = !hourOver || row.Target <= 0 ? "" : row.Actual >= row.Target ? "Ok" : "Short";
            }
            return rows;
        }

        private static HourSlotRow NewRow(KanbanJob job, int hour, int target) => new HourSlotRow
        {
            Hour = hour,
            Barcode = job.Barcode,
            Urutan = job.Urutan,
            PartNumber = job.PartNumber,
            Model = job.Model,
            PosisiPart = job.PosisiPart,
            Target = target
        };
    }
}

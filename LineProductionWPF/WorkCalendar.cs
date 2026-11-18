using System.Globalization;

namespace LineProductionWPF
{
    /// <summary>A stretch of working time inside one day, e.g. 08:00 - 12:00.</summary>
    public readonly record struct WorkBlock(DateTime Start, DateTime End);

    /// <summary>
    /// The working hours of the factory, used for the count (pcs/hour) and for the start / end times of the jobs.
    ///   Monday - Thursday, Saturday (and Sunday, if a schedule exists):  08:00-12:00 and 13:00-17:00  (break 12:00-13:00)
    ///   Friday:                                                          08:00-11:30 and 12:45-17:15  (break 11:30-12:45)
    /// A Saturday is a working day only when a schedule exists for it, so there is nothing to switch on here.
    /// NOT handled yet: Ramadan hours and public holidays. When the hours change, only the numbers in BlocksOf change.
    ///
    /// Outside these blocks the clock does not count. Work that runs past the end of the last block (overtime) is
    /// shown as plain clock time after it, because the next day has its own schedule.
    /// </summary>
    public static class WorkCalendar
    {
        public static IReadOnlyList<WorkBlock> BlocksOf(DateTime day)
        {
            DateTime d = day.Date;
            if (d.DayOfWeek == DayOfWeek.Friday)
                return new[]
                {
                    new WorkBlock(d.AddHours(8), d.AddHours(11).AddMinutes(30)),
                    new WorkBlock(d.AddHours(12).AddMinutes(45), d.AddHours(17).AddMinutes(15))
                };

            return new[]
            {
                new WorkBlock(d.AddHours(8), d.AddHours(12)),
                new WorkBlock(d.AddHours(13), d.AddHours(17))
            };
        }

        /// <summary>First moment of work on this day (08:00).</summary>
        public static DateTime DayStart(DateTime day) => BlocksOf(day)[0].Start;

        /// <summary>End of the last block of this day (17:00, Friday 17:15).</summary>
        public static DateTime DayEnd(DateTime day)
        {
            var blocks = BlocksOf(day);
            return blocks[blocks.Count - 1].End;
        }

        /// <summary>The break between the two blocks of this day (Start = end of the first block, End = start of the second).</summary>
        public static (DateTime Start, DateTime End) BreakOf(DateTime day)
        {
            var blocks = BlocksOf(day);
            return (blocks[0].End, blocks[1].Start);
        }

        /// <summary>The break as short text for the screen: "12-13" (Friday "11:30-12:45").</summary>
        public static string BreakText(DateTime day)
        {
            var (start, end) = BreakOf(day);
            return Short(start) + "-" + Short(end);
        }

        private static string Short(DateTime t) =>
            t.Minute == 0
                ? t.Hour.ToString(CultureInfo.InvariantCulture)
                : t.ToString("H:mm", CultureInfo.InvariantCulture);

        /// <summary>True when the stretch from..to runs right across the break, so its length includes the break.</summary>
        public static bool SpansBreak(DateTime from, DateTime to)
        {
            if (to <= from) return false;
            var (breakStart, breakEnd) = BreakOf(from);
            return from < breakStart && to > breakEnd;
        }

        /// <summary>True when t is after the end of that day's last block (overtime).</summary>
        public static bool IsAfterShift(DateTime t) => t > DayEnd(t);

        /// <summary>Working seconds between from and to (breaks and time outside the blocks do not count).</summary>
        public static double WorkingSeconds(DateTime from, DateTime to)
        {
            if (to <= from) return 0;

            double total = 0;
            for (DateTime day = from.Date; day <= to.Date; day = day.AddDays(1))
            {
                foreach (WorkBlock b in BlocksOf(day))
                {
                    DateTime s = b.Start > from ? b.Start : from;
                    DateTime e = b.End < to ? b.End : to;
                    if (e > s) total += (e - s).TotalSeconds;
                }
            }
            return total;
        }

        /// <summary>
        /// The moment when <paramref name="seconds"/> of working time have passed, counted from <paramref name="from"/>.
        /// A start inside the break (or before the shift) first moves to the next working moment. With 0 seconds this
        /// simply returns that next working moment. Past the end of the last block the rest is added as plain clock time (overtime).
        /// </summary>
        public static DateTime AddWorkingSeconds(DateTime from, double seconds)
        {
            double left = Math.Max(0, seconds);
            DateTime t = from;

            var blocks = BlocksOf(from);
            foreach (WorkBlock b in blocks)
            {
                if (t >= b.End) continue;

                DateTime s = t > b.Start ? t : b.Start;
                double available = (b.End - s).TotalSeconds;
                if (left <= available) return s.AddSeconds(left);

                left -= available;
                t = b.End;
            }

            DateTime lastEnd = blocks[blocks.Count - 1].End;
            DateTime overtimeStart = t > lastEnd ? t : lastEnd;
            return overtimeStart.AddSeconds(left);
        }

        /// <summary>The next moment at which work can happen (t itself when it is inside a block).</summary>
        public static DateTime NextWorking(DateTime t) => AddWorkingSeconds(t, 0);
    }
}

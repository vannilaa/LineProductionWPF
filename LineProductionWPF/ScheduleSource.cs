using System;
using System.Collections.Generic;
using System.Text;
using static System.Net.Mime.MediaTypeNames;

namespace LineProductionWPF
{
    public interface IScheduleSource
    {
        List<ScheduleRow> GetSchedule();
    }

    // Test data. The hours follow the clock, so it works whenever you run it.
    public class FakeScheduleSource : IScheduleSource
    {
        public List<ScheduleRow> GetSchedule()
        {
            // line, part number, job number, pcs per hour, scans per box, boxes per pallet
            var lines = new (string Name, string Part, string Job, int Target, int ScanPerBox, int BoxesPerPallet)[]
            {
            ("WEB BKL", "81230-BZ110", "WB-1021", 3600,  4, 10),
            ("PLT BKL", "81250-BZ320", "PB-2207", 3600,  3, 12),
            ("STC",     "89100-BZ050", "ST-0815", 3600,  2, 10),
            ("RC0",     "73350-BZ230", "AA-3736", 3600, 3, 10),
            };

            var rows = new List<ScheduleRow>();
            foreach (var l in lines)
            {
                for (int hour = 0; hour < 24; hour++)
                {
                    rows.Add(new ScheduleRow
                    {
                        Jadwal = hour + 1,
                        Jam = hour,
                        NomorLine = l.Name,
                        PartNumber = l.Part,
                        JobNo = l.Job,
                        Target = l.Target,
                        ScanPerBox = l.ScanPerBox,
                        BoxesPerPallet = l.BoxesPerPallet
                    });
                }
            }
            return rows;
        }
    }
}
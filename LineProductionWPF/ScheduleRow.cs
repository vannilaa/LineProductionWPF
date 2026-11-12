using System;
using System.Collections.Generic;
using System.Text;

namespace LineProductionWPF
{
    public class ScheduleRow
    {
        public string NomorLine { get; set; } = "";
        public int Jadwal { get; set; }
        public int Jam { get; set; }
        public string PartNumber { get; set; }
        public string JobNo { get; set; }
        public int Target { get; set; }           // PPIC rate: pcs per hour
        public int JumlahTarget { get; set; }
        public int Actual { get; set; }
        public int JumlahActual { get; set; }
        public int Selisih { get; set; }
        public int ScanPerBox { get; set; }       // scans that make one box
        public int BoxesPerPallet { get; set; }   // boxes that make one pallet
    }
}

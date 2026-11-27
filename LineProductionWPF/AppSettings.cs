using System;
using System.IO;
using System.Text.Json;

namespace LineProductionWPF
{
    /// <summary>One line's Checker and the day it was entered (yyyy-MM-dd).</summary>
    public sealed class CheckerEntry
    {
        public string Name { get; set; } = "";
        public string Date { get; set; } = "";
    }

    /// <summary>
    /// Settings of this PC, changed in the Pengaturan (gear) window and saved to
    /// %LocalAppData%\LineProductionWPF\settings.json (next to kanban_pauses.json).
    /// Not in db.ini, because db.ini is shared with DailyScheduler.
    /// </summary>
    public sealed class AppSettings
    {
        // ---- Line of this PC ----
        /// <summary>line_production.id opened last on this PC (0 = none yet: the line picker opens on the first start).</summary>
        public int LastLineId { get; set; }

        // ---- Checker (printed on the box label, saved per print in box_print_job) ----
        /// <summary>Checker per line, key = line_production.id. Local to this PC, so another PC never changes it.</summary>
        public System.Collections.Generic.Dictionary<string, CheckerEntry> Checkers { get; set; } = new();

        // ---- Box label printer ----
        /// <summary>Printer for box labels. Empty = the Windows default printer.</summary>
        public string PrinterName { get; set; } = "";

        // ---- ESP (serial / COM) ----
        public string EspPort { get; set; } = "";
        public int EspBaud { get; set; } = 9600;
        /// <summary>0 = none, 1 = Newline, 2 = Carriage return, 3 = Both NL &amp; CR (same order as the serial monitor).</summary>
        public int EspLineEnding { get; set; } = 0;
        /// <summary>Connect to <see cref="EspPort"/> when the app starts.</summary>
        public bool EspAutoConnect { get; set; } = false;

        /// <summary>The Checker entered today for this line, or "" when none (a new day asks again).</summary>
        public string CheckerFor(int lineId) =>
            Checkers.TryGetValue(lineId.ToString(), out var c) && c.Date == Today ? (c.Name ?? "").Trim() : "";

        /// <summary>Sets today's Checker of this line (call <see cref="Save"/> after).</summary>
        public void SetChecker(int lineId, string name) =>
            Checkers[lineId.ToString()] = new CheckerEntry { Name = (name ?? "").Trim(), Date = Today };

        private static string Today => DateTime.Today.ToString("yyyy-MM-dd");

        // ---------------- load / save ----------------
        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LineProductionWPF", "settings.json");

        private static AppSettings? _current;

        /// <summary>The settings of this PC (loaded once; defaults when the file is missing or broken).</summary>
        public static AppSettings Current => _current ??= Load();

        private static AppSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
            }
            catch { /* broken file: start with defaults */ }
            return new AppSettings();
        }

        /// <summary>Saves <see cref="Current"/>. Returns an error message, or null when saved.</summary>
        public static string? Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true }));
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }
    }
}

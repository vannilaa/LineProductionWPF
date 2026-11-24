using System.IO;
using System.Media;

namespace LineProductionWPF
{
    /// <summary>
    /// Sounds that tell the operator a scan was SAVED (Sounds\scan-ok.wav) and that it completed a box (Sounds\box-full.wav).
    /// The files are copied next to the .exe (like Logos). They are short and soft on purpose: the stations stand close together.
    /// A missing or unreadable file falls back to a Windows system sound, so a scan is never silent because of a missing file.
    /// A rejected scan keeps its own sounds (SystemSounds.Beep / Hand) and a schedule revision has its own (Exclamation).
    /// </summary>
    public static class ScanSounds
    {
        private static readonly SoundPlayer? Saved = Load("scan-ok.wav");
        private static readonly SoundPlayer? BoxFull = Load("box-full.wav");

        private static SoundPlayer? Load(string file)
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Sounds", file);
                if (!File.Exists(path)) return null;
                var player = new SoundPlayer(path);
                player.Load();   // read it once now, so the first scan does not wait for the disk
                return player;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>A scan was saved.</summary>
        public static void PlaySaved() => Play(Saved, SystemSounds.Asterisk);

        /// <summary>A scan was saved and it completed a box.</summary>
        public static void PlayBoxFull() => Play(BoxFull, SystemSounds.Asterisk);

        /// <summary>A schedule was revised in DailyScheduler.</summary>
        public static void PlayScheduleRevision()
        {
            try
            {
                SystemSounds.Exclamation.Play();
            }
            catch
            {
                // A sound problem must not interfere with schedule refresh.
            }
        }

        private static void Play(SoundPlayer? player, SystemSound fallback)
        {
            try
            {
                if (player != null) player.Play();   // plays in the background; does not block scanning
                else fallback.Play();
            }
            catch
            {
                // a sound problem must never disturb the scan itself
            }
        }
    }
}

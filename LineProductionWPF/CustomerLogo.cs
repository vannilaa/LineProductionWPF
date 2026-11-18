using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;

namespace LineProductionWPF
{
    /// <summary>
    /// Finds the logo of a customer in the "Logos" folder next to the .exe.
    /// The file name is made from the customer name in the product table, see <see cref="KeyFor"/>:
    ///   PT. ASTRA DAIHATSU MOTOR  ->  Logos\astra-daihatsu-motor.ico   (or .png)
    /// A missing file is not an error: the dashboard just shows the customer name without a logo.
    /// </summary>
    public static class CustomerLogo
    {
        public static string Folder => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logos");

        private static readonly string[] Extensions = { ".ico", ".png" };

        /// <summary>
        /// "PT. HINO MOTOR MANUFACTURING IND." -> "hino-motor-manufacturing-ind".
        /// Upper-case, leading "PT."/"PT" and trailing "TBK." removed, everything that is not a letter or digit becomes "-".
        /// </summary>
        public static string KeyFor(string? customer)
        {
            string s = (customer ?? "").Trim().ToUpperInvariant();
            s = Regex.Replace(s, @"^PT\.?\s+", "");
            s = Regex.Replace(s, @"\s+TBK\.?$", "");
            s = Regex.Replace(s, @"[^A-Z0-9]+", "-").Trim('-');
            return s.ToLowerInvariant();
        }

        /// <summary>The logo, or null when the customer is empty or no (readable) file exists.</summary>
        public static BitmapSource? Load(string? customer)
        {
            foreach (string key in Keys(customer))
            {
                foreach (string ext in Extensions)
                {
                    string path = Path.Combine(Folder, key + ext);
                    if (!File.Exists(path)) continue;
                    BitmapSource? logo = Decode(path);
                    if (logo != null) return logo;
                }
            }
            return null;
        }

        // "PT. FUJISEAT INDONESIA / TBINA" tries fujiseat-indonesia-tbina first, then fujiseat-indonesia.
        private static IEnumerable<string> Keys(string? customer)
        {
            if (string.IsNullOrWhiteSpace(customer)) yield break;

            string full = KeyFor(customer);
            if (full.Length > 0) yield return full;

            int slash = customer.IndexOf('/');
            if (slash > 0)
            {
                string first = KeyFor(customer.Substring(0, slash));
                if (first.Length > 0 && first != full) yield return first;
            }
        }

        // An .ico holds several sizes; take the biggest so the logo stays sharp.
        private static BitmapSource? Decode(string path)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                BitmapDecoder decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                BitmapFrame frame = decoder.Frames.OrderByDescending(f => f.PixelWidth * f.PixelHeight).First();
                if (frame.CanFreeze) frame.Freeze();
                return frame;
            }
            catch
            {
                return null;   // damaged or unsupported file: show no logo
            }
        }
    }
}

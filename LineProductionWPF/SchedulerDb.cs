using System.IO;
using System.Text;
using Microsoft.Data.SqlClient;

namespace LineProductionWPF
{
    /// <summary>One line production (table line_production of the DailyScheduler database).</summary>
    public class KanbanLine
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }

    /// <summary>One job of a day's schedule, with what has been scanned so far.</summary>
    public class KanbanJob
    {
        public int Urutan { get; set; }
        public string Barcode { get; set; } = "";
        public string PartNumber { get; set; } = "";
        public string JobNo { get; set; } = "";
        public string Posisi { get; set; } = "";
        public string Part { get; set; } = "";

        /// <summary>Position + part, e.g. "F - COVER". Whatever is missing is left out.</summary>
        public string PosisiPart => string.Join(" - ", new[] { Posisi, Part }.Where(x => !string.IsNullOrWhiteSpace(x) && x != "-"));
        public int Jumlah { get; set; }          // planned quantity of this job
        public int Target { get; set; }          // rate: pcs per hour (product.target_per_jam)
        public int Discan { get; set; }          // scanned on the line so far
        public int QtyPerBox { get; set; }       // pieces that make one box (product.qty_per_box), 0 = unknown

        public bool IsComplete => Jumlah > 0 && Discan >= Jumlah;

        // shown in the job table
        public int Count { get; set; }
        public string Status { get; set; } = "";
    }

    /// <summary>
    /// Read-only access to the DailyScheduler database: the schedule that was made there and its scan numbers.
    /// Uses the same db.ini as DailyScheduler (put it next to the .exe).
    /// </summary>
    public static class SchedulerDb
    {
        private static string? _connectionString;

        public static string SettingsPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "db.ini");

        private static string ConnectionString
        {
            get
            {
                if (_connectionString != null) return _connectionString;

                string path = SettingsPath;
                if (!File.Exists(path))
                    throw new InvalidOperationException("File db.ini tidak ditemukan di:\n" + path +
                        "\n\nSalin db.ini dari DailyScheduler (atau db.ini.example) ke folder aplikasi ini.");

                var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';')) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    values[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }

                values.TryGetValue("Server", out string? server);
                values.TryGetValue("Database", out string? database);
                values.TryGetValue("User", out string? user);
                values.TryGetValue("Password", out string? password);

                if (string.IsNullOrEmpty(server))
                    throw new InvalidOperationException("Isi \"Server=\" di db.ini:\n" + path);

                var builder = new SqlConnectionStringBuilder
                {
                    DataSource = server,
                    InitialCatalog = string.IsNullOrEmpty(database) ? "DailyScheduler" : database,
                    ConnectTimeout = 8,
                    TrustServerCertificate = true,
                    Encrypt = false,
                    ApplicationName = "LineProductionWPF"
                };
                if (string.IsNullOrEmpty(user))
                {
                    builder.IntegratedSecurity = true;
                }
                else
                {
                    builder.UserID = user;
                    builder.Password = password ?? "";
                }

                _connectionString = builder.ConnectionString;
                return _connectionString;
            }
        }

        public static List<KanbanLine> LoadLines()
        {
            var list = new List<KanbanLine>();
            using var con = new SqlConnection(ConnectionString);
            con.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT id, name FROM line_production ORDER BY name";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new KanbanLine { Id = r.GetInt32(0), Name = r.GetString(1) });
            return list;
        }

        /// <summary>Only the lines that have a saved schedule on this date (what the line picker offers).</summary>
        public static List<KanbanLine> LoadScheduledLines(DateTime tanggal)
        {
            var list = new List<KanbanLine>();
            using var con = new SqlConnection(ConnectionString);
            con.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText =
                "SELECT DISTINCT lp.id, lp.name FROM schedule s " +
                "JOIN line_production lp ON lp.id = s.line_production_id " +
                "WHERE s.tanggal = @tanggal ORDER BY lp.name";
            cmd.Parameters.Add("@tanggal", System.Data.SqlDbType.Date).Value = tanggal.Date;
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new KanbanLine { Id = r.GetInt32(0), Name = r.GetString(1) });
            return list;
        }

        /// <summary>The jobs of this date + line in schedule order, each with its scanned count. Empty = no schedule saved.</summary>
        public static List<KanbanJob> LoadJobs(DateTime tanggal, int lineProductionId)
        {
            var list = new List<KanbanJob>();
            using var con = new SqlConnection(ConnectionString);
            con.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText =
                "SELECT i.urutan, i.barcode, i.jumlah, " +
                "       COALESCE(p.part_number_code, p.part_number), p.job_no, p.position, p.target_per_jam, " +
                "       ISNULL(v.discan, 0), ISNULL(p.qty_per_box, 0), p.part " +
                "FROM schedule s " +
                "JOIN schedule_item i ON i.schedule_id = s.id " +
                "LEFT JOIN product p ON p.barcode = i.barcode " +
                "LEFT JOIN v_job_scan_summary v ON v.tanggal = s.tanggal AND v.line_production_id = s.line_production_id AND v.barcode = i.barcode " +
                "WHERE s.tanggal = @tanggal AND s.line_production_id = @lp " +
                "ORDER BY i.urutan";
            cmd.Parameters.Add("@tanggal", System.Data.SqlDbType.Date).Value = tanggal.Date;
            cmd.Parameters.AddWithValue("@lp", lineProductionId);
            cmd.CommandTimeout = 30;

            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new KanbanJob
                {
                    Urutan = r.GetInt32(0),
                    Barcode = r.GetString(1),
                    Jumlah = r.GetInt32(2),
                    PartNumber = r.IsDBNull(3) ? "-" : r.GetString(3),
                    JobNo = r.IsDBNull(4) ? "-" : r.GetString(4),
                    Posisi = r.IsDBNull(5) ? "-" : r.GetString(5),
                    Target = r.IsDBNull(6) ? 0 : r.GetInt32(6),
                    Discan = r.GetInt32(7),
                    QtyPerBox = r.GetInt32(8),
                    Part = r.IsDBNull(9) ? "" : r.GetString(9)
                });
            }
            return list;
        }
    }
}

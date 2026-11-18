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
        public int BoxesRequired { get; set; }   // boxes needed for this job's planned quantity

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
                "       ISNULL(v.discan, 0), ISNULL(p.qty_per_box, 0), p.part, " +
                "       CASE WHEN i.jumlah > 0 THEN ((i.jumlah - 1) / CASE WHEN p.qty_per_box > 0 THEN p.qty_per_box ELSE 1 END) + 1 ELSE 0 END " +
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
                    Part = r.IsDBNull(9) ? "" : r.GetString(9),
                    BoxesRequired = r.GetInt32(10)
                });
            }
            return list;
        }

        /// <summary>
        /// Records a scan only when the exact serial belongs to a sticker printed for this date,
        /// production line and scheduled product. A sticker can be scanned only once.
        /// box_id is intentionally NULL; box assignment is not implemented in this dashboard yet.
        /// </summary>
        public static bool RecordStickerScan(DateTime tanggal, int lineProductionId, string barcode, string serial, out string message)
        {
            message = "";
            if (string.IsNullOrWhiteSpace(serial))
            {
                message = "Kode scan kosong.";
                return false;
            }

            using var con = new SqlConnection(ConnectionString);
            con.Open();
            using var tx = con.BeginTransaction(System.Data.IsolationLevel.Serializable);
            try
            {
                long printStickerId;
                using (var cmd = con.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText =
                        "SELECT id FROM print_sticker WITH (UPDLOCK, HOLDLOCK) " +
                        "WHERE tanggal = @tanggal AND line_production_id = @lp AND barcode = @barcode AND serial = @serial";
                    cmd.Parameters.Add("@tanggal", System.Data.SqlDbType.Date).Value = tanggal.Date;
                    cmd.Parameters.AddWithValue("@lp", lineProductionId);
                    cmd.Parameters.AddWithValue("@barcode", barcode);
                    cmd.Parameters.AddWithValue("@serial", serial.Trim());
                    object? value = cmd.ExecuteScalar();
                    if (value == null || value == DBNull.Value)
                    {
                        tx.Rollback();
                        message = "Stiker tidak ditemukan. Pastikan stiker sudah dicetak untuk job ini.";
                        return false;
                    }
                    printStickerId = Convert.ToInt64(value);
                }

                using (var cmd = con.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText =
                        "SELECT " +
                        "CASE WHEN EXISTS (SELECT 1 FROM sticker_scan WHERE print_sticker_id = @id) THEN 1 " +
                        "     WHEN EXISTS (SELECT 1 FROM sticker_writeoff WHERE print_sticker_id = @id) THEN 2 " +
                        "     ELSE 0 END";
                    cmd.Parameters.AddWithValue("@id", printStickerId);
                    int state = Convert.ToInt32(cmd.ExecuteScalar());
                    if (state == 1)
                    {
                        tx.Rollback();
                        message = "Stiker ini sudah pernah discan.";
                        return false;
                    }
                    if (state == 2)
                    {
                        tx.Rollback();
                        message = "Stiker ini sudah ditandai hilang/rusak dan tidak dapat discan.";
                        return false;
                    }
                }

                using (var cmd = con.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText =
                        "INSERT INTO sticker_scan (print_sticker_id, box_id, windows_user, machine_name) " +
                        "VALUES (@id, NULL, @user, @machine)";
                    cmd.Parameters.AddWithValue("@id", printStickerId);
                    cmd.Parameters.AddWithValue("@user", Environment.UserName);
                    cmd.Parameters.AddWithValue("@machine", Environment.MachineName);
                    cmd.ExecuteNonQuery();
                }

                tx.Commit();
                message = "Scan berhasil disimpan ke database.";
                return true;
            }
            catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 2601 || ex.Number == 2627)
            {
                try { tx.Rollback(); } catch { }
                message = "Stiker ini sudah pernah discan.";
                return false;
            }
            catch
            {
                try { tx.Rollback(); } catch { }
                throw;
            }
        }

    }
}

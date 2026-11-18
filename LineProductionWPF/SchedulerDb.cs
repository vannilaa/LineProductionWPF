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
    /// Access to the DailyScheduler database: reads schedules and records production sticker scans.
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
        public static bool RecordStickerScan(DateTime tanggal, int lineProductionId, string barcode, string serial,
            int qtyPerBox, int plannedQuantity, out int completedBoxes, out int scansInOpenBox, out string message)
        {
            completedBoxes = 0;
            scansInOpenBox = 0;
            message = "";
            if (string.IsNullOrWhiteSpace(serial))
            {
                message = "Kode scan kosong.";
                return false;
            }
            if (qtyPerBox <= 0 || plannedQuantity <= 0)
            {
                message = "Qty per box atau jumlah job belum valid.";
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
                        // If this serial exists on another scheduled product for the same line/date,
                        // tell the operator that it belongs to a different job.
                        cmd.Parameters.Clear();
                        cmd.CommandText =
                            "SELECT TOP (1) barcode FROM print_sticker " +
                            "WHERE tanggal = @tanggal AND line_production_id = @lp AND serial = @serial";
                        cmd.Parameters.Add("@tanggal", System.Data.SqlDbType.Date).Value = tanggal.Date;
                        cmd.Parameters.AddWithValue("@lp", lineProductionId);
                        cmd.Parameters.AddWithValue("@serial", serial.Trim());
                        object? otherBarcode = cmd.ExecuteScalar();

                        tx.Rollback();
                        message = otherBarcode != null && otherBarcode != DBNull.Value
                            ? "PERINGATAN: Item ini bukan bagian dari job yang sedang aktif."
                            : "PERINGATAN: Serial tidak ditemukan untuk line/tanggal ini. Periksa barcode atau cetak stikernya terlebih dahulu.";
                        return false;
                    }
                    printStickerId = Convert.ToInt64(value);
                }

                using (var cmd = con.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText =
                        "SELECT CASE " +
                        "WHEN EXISTS (SELECT 1 FROM sticker_scan WHERE print_sticker_id = @id) THEN 1 " +
                        "WHEN EXISTS (SELECT 1 FROM sticker_writeoff WHERE print_sticker_id = @id) THEN 2 ELSE 0 END";
                    cmd.Parameters.AddWithValue("@id", printStickerId);
                    int state = Convert.ToInt32(cmd.ExecuteScalar());
                    if (state != 0)
                    {
                        tx.Rollback();
                        message = state == 1 ? "Stiker ini sudah pernah discan." :
                            "Stiker ini sudah ditandai hilang/rusak dan tidak dapat discan.";
                        return false;
                    }
                }

                int totalBefore;
                using (var cmd = con.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText =
                        "SELECT COUNT(*) FROM sticker_scan c JOIN print_sticker s ON s.id = c.print_sticker_id " +
                        "WHERE s.tanggal = @tanggal AND s.line_production_id = @lp AND s.barcode = @barcode";
                    cmd.Parameters.Add("@tanggal", System.Data.SqlDbType.Date).Value = tanggal.Date;
                    cmd.Parameters.AddWithValue("@lp", lineProductionId);
                    cmd.Parameters.AddWithValue("@barcode", barcode);
                    totalBefore = Convert.ToInt32(cmd.ExecuteScalar());
                }
                if (totalBefore >= plannedQuantity)
                {
                    tx.Rollback();
                    message = "Jumlah scan untuk job ini sudah mencapai jumlah rencana.";
                    return false;
                }

                int boxId;
                using (var cmd = con.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText =
                        "SELECT TOP (1) id FROM dbo.box WITH (UPDLOCK, HOLDLOCK) " +
                        "WHERE tanggal = @tanggal AND line_production_id = @lp AND barcode = @barcode AND closed_at IS NULL " +
                        "ORDER BY box_no DESC";
                    cmd.Parameters.Add("@tanggal", System.Data.SqlDbType.Date).Value = tanggal.Date;
                    cmd.Parameters.AddWithValue("@lp", lineProductionId);
                    cmd.Parameters.AddWithValue("@barcode", barcode);
                    object? value = cmd.ExecuteScalar();
                    if (value == null || value == DBNull.Value)
                    {
                        cmd.Parameters.Clear();
                        cmd.CommandText =
                            "DECLARE @next INT = ISNULL((SELECT MAX(box_no) FROM dbo.box WITH (UPDLOCK, HOLDLOCK) " +
                            "WHERE tanggal = @tanggal AND line_production_id = @lp AND barcode = @barcode), 0) + 1; " +
                            "INSERT INTO dbo.box (tanggal, line_production_id, barcode, box_no, qty_per_box, windows_user, machine_name) " +
                            "OUTPUT INSERTED.id VALUES (@tanggal, @lp, @barcode, @next, @qty, @user, @machine)";
                        cmd.Parameters.Add("@tanggal", System.Data.SqlDbType.Date).Value = tanggal.Date;
                        cmd.Parameters.AddWithValue("@lp", lineProductionId);
                        cmd.Parameters.AddWithValue("@barcode", barcode);
                        cmd.Parameters.AddWithValue("@qty", qtyPerBox);
                        cmd.Parameters.AddWithValue("@user", Environment.UserName);
                        cmd.Parameters.AddWithValue("@machine", Environment.MachineName);
                        boxId = Convert.ToInt32(cmd.ExecuteScalar());
                    }
                    else boxId = Convert.ToInt32(value);
                }

                using (var cmd = con.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText =
                        "INSERT INTO sticker_scan (print_sticker_id, box_id, windows_user, machine_name) " +
                        "VALUES (@id, @box, @user, @machine)";
                    cmd.Parameters.AddWithValue("@id", printStickerId);
                    cmd.Parameters.AddWithValue("@box", boxId);
                    cmd.Parameters.AddWithValue("@user", Environment.UserName);
                    cmd.Parameters.AddWithValue("@machine", Environment.MachineName);
                    cmd.ExecuteNonQuery();
                }

                int scansInBox;
                using (var cmd = con.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "SELECT COUNT(*) FROM sticker_scan WHERE box_id = @box";
                    cmd.Parameters.AddWithValue("@box", boxId);
                    scansInBox = Convert.ToInt32(cmd.ExecuteScalar());
                }

                int totalAfter = totalBefore + 1;
                bool jobComplete = totalAfter >= plannedQuantity;
                if (scansInBox >= qtyPerBox || jobComplete)
                {
                    using (var cmd = con.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText =
                            "UPDATE dbo.box SET closed_at = SYSDATETIME(), is_partial = @partial WHERE id = @box";
                        cmd.Parameters.AddWithValue("@partial", scansInBox < qtyPerBox);
                        cmd.Parameters.AddWithValue("@box", boxId);
                        cmd.ExecuteNonQuery();
                    }
                    scansInOpenBox = 0;
                }
                else scansInOpenBox = scansInBox;

                using (var cmd = con.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText =
                        "SELECT COUNT(*) FROM dbo.box WHERE tanggal = @tanggal AND line_production_id = @lp " +
                        "AND barcode = @barcode AND closed_at IS NOT NULL";
                    cmd.Parameters.Add("@tanggal", System.Data.SqlDbType.Date).Value = tanggal.Date;
                    cmd.Parameters.AddWithValue("@lp", lineProductionId);
                    cmd.Parameters.AddWithValue("@barcode", barcode);
                    completedBoxes = Convert.ToInt32(cmd.ExecuteScalar());
                }

                tx.Commit();
                message = "Scan tersimpan. Box " + (completedBoxes + (scansInOpenBox > 0 ? 1 : 0)) +
                    ": " + (scansInOpenBox > 0 ? scansInOpenBox : scansInBox) + "/" + qtyPerBox + ".";
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

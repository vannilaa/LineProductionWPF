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

        /// <summary>Previous name of the line (column old_name); the website still filters on it. Empty = not set.</summary>
        public string OldName { get; set; } = "";
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
        public string Model { get; set; } = "";

        /// <summary>Position + part, e.g. "F - COVER". Whatever is missing is left out.</summary>
        public string PosisiPart => string.Join(" - ", new[] { Posisi, Part }.Where(x => !string.IsNullOrWhiteSpace(x) && x != "-"));
        public int Jumlah { get; set; }          // planned quantity of this job
        public int Target { get; set; }          // rate: pcs per hour (product.target_per_jam)
        public int Discan { get; set; }          // scanned on the line so far
        public int QtyPerBox { get; set; }       // pieces that make one box (product.qty_per_box), 0 = unknown
        public int BoxesRequired { get; set; }   // boxes needed for this job's planned quantity
        public int CompletedBoxes { get; set; }  // boxes already closed in the database
        public int OpenBoxScans { get; set; }    // scans in the box that is still open
        public string Customer { get; set; } = "";   // product.customer, e.g. "PT. ASTRA DAIHATSU MOTOR"

        public bool IsComplete => Jumlah > 0 && Discan >= Jumlah;

        // SAME BARCODE IN MORE THAN ONE ROW of the schedule: the sticker numbers of THIS row (see LoadJobs).
        public int FirstNo { get; set; } = 1;
        public int LastNo { get; set; } = int.MaxValue;     // int.MaxValue = the last row of the barcode (takes everything above)
        public int Occurrence { get; set; } = 1;            // 1 = first row of this barcode in the schedule
        public int SameBarcodeRows { get; set; } = 1;       // how many rows of the schedule use this barcode
        public int BarcodeTotalJumlah { get; set; }         // planned quantity of ALL rows with this barcode

        public DateTime? ScheduleCreatedAt { get; set; }    // schedule.created_at (null for schedules made before that column existed)
        public int ScheduleVersion { get; set; }            // schedule.version: 1 when created, +1 with every edit in DailyScheduler (same for every job of a schedule)
        public DateTime? LastScanAt { get; set; }           // scanned_at of the latest scan of this row
        public DateTime? AddedAt { get; set; }              // schedule_item.added_at: when this job was added to the schedule
        public string OpenBoxSerial { get; set; } = "";     // name of the box that is being filled now ("" = no open box / no name)

        // shown in the job table
        public int Count { get; set; }
        public string Status { get; set; } = "";
        public bool IsCurrent { get; set; }

        // Times (set by KanbanTracker.Calculate). Finished jobs show what really happened, the others an estimate.
        public DateTime Mulai { get; set; }                 // start
        public DateTime Selesai { get; set; }               // end (finished) or expected end
        public DateTime PlannedEnd { get; set; }            // end according to the plan (every job on time, back to back)
        public bool IsProjected { get; set; }               // true = estimate (job is running or waiting)
        public string MulaiNote { get; set; } = "";         // e.g. "(setelah istirahat 12-13)"
        public string SelesaiNote { get; set; } = "";       // e.g. "(+istirahat 12-13)" or "(lewat 17:00)"
        public string DiffNote { get; set; } = "";
        public int DiffMinutes { get; set; }                // Selesai - PlannedEnd in WORKING minutes (no break): negative = ahead of the plan, positive = adds time (late)

        public string MulaiText => Mulai.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        public string SelesaiText => Selesai.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        public string DiffKind => DiffMinutes < 0 ? "Early" : DiffMinutes > 0 ? "Late" : "OnTime";
        public string DiffText
        {
            get
            {
                if (DiffMinutes == 0) return "0 mnt";
                string sign = DiffMinutes > 0 ? "+" : "-";
                int abs = Math.Abs(DiffMinutes);
                return abs < 60 ? sign + abs + " mnt" : sign + (abs / 60) + "j " + (abs % 60).ToString("00") + "m";
            }
        }
    }

    public enum ScanOutcome
    {
        Saved,
        InvalidInput,     // empty code, qty per box or planned quantity missing
        NotFound,         // serial is not a printed sticker for this line/date
        WrongJob,         // serial belongs to another product of this line/date
        AlreadyScanned,
        WrittenOff,
        JobFull,
        Busy,             // lock timeout / deadlock: the operator should scan again
        Error             // unexpected failure (set by the caller)
    }

    /// <summary>Result of <see cref="SchedulerDb.RecordStickerScan"/>.</summary>
    public sealed class ScanResult
    {
        public ScanOutcome Outcome { get; init; }
        public string Message { get; init; } = "";
        public int CompletedBoxes { get; init; }    // closed boxes of the job after this scan
        public int ScansInOpenBox { get; init; }    // scans in the still-open box (0 right after a box closed)
        public string BoxSerial { get; init; } = "";   // name of the box THIS scan went into (also when it just closed), "" if it has none
        public bool Saved => Outcome == ScanOutcome.Saved;
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

        /// <summary>Address of the production website (page without the filter). Optional "WebUrl=" in db.ini overrides it.</summary>
        private const string DefaultWebUrl = "http://192.168.1.194:8080/swi/pages/bpbj/production-all-lines.php";

        /// <summary>
        /// Website address showing this line, e.g. old name "RC0 # 2" gives ...production-all-lines.php?filter_line=RC0+%23+2
        /// The website knows the line by its OLD name; a line without old_name falls back to its current name.
        /// </summary>
        public static string BuildWebUrl(KanbanLine line)
        {
            string baseUrl = DefaultWebUrl;
            try
            {
                if (File.Exists(SettingsPath))
                    foreach (string raw in File.ReadAllLines(SettingsPath))
                    {
                        string t = raw.Trim();
                        int eq = t.IndexOf('=');
                        if (eq > 0 && t.Substring(0, eq).Trim().Equals("WebUrl", StringComparison.OrdinalIgnoreCase) && t.Length > eq + 1)
                            baseUrl = t.Substring(eq + 1).Trim();
                    }
            }
            catch (IOException) { /* keep the default address */ }

            string filter = string.IsNullOrWhiteSpace(line.OldName) ? line.Name : line.OldName;
            return baseUrl + "?filter_line=" + System.Net.WebUtility.UrlEncode(filter);
        }

        public static List<KanbanLine> LoadLines()
        {
            var list = new List<KanbanLine>();
            using var con = new SqlConnection(ConnectionString);
            con.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT id, name, old_name FROM line_production ORDER BY name";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new KanbanLine { Id = r.GetInt32(0), Name = r.GetString(1), OldName = r.IsDBNull(2) ? "" : r.GetString(2).Trim() });
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
                "SELECT DISTINCT lp.id, lp.name, lp.old_name FROM schedule s " +
                "JOIN line_production lp ON lp.id = s.line_production_id " +
                "WHERE s.tanggal = @tanggal ORDER BY lp.name";
            cmd.Parameters.Add("@tanggal", System.Data.SqlDbType.Date).Value = tanggal.Date;
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new KanbanLine { Id = r.GetInt32(0), Name = r.GetString(1), OldName = r.IsDBNull(2) ? "" : r.GetString(2).Trim() });
            return list;
        }

        /// <summary>The jobs of this date + line in schedule order, each with its scanned count. Empty = no schedule saved.</summary>
        public static List<KanbanJob> LoadJobs(DateTime tanggal, int lineProductionId)
        {
            var list = new List<KanbanJob>();
            using var con = new SqlConnection(ConnectionString);
            con.Open();
            using var cmd = con.CreateCommand();
            // SAME BARCODE IN MORE THAN ONE ROW of the schedule: the stickers of a barcode are ONE pool numbered in schedule
            // order (row 1 = 1..J1, row 2 = J1+1..J1+J2, ... as DailyScheduler prints them). Each row counts only the scans
            // of its own range; the LAST row of a barcode also takes everything above, so a barcode used once counts all
            // its stickers, as before. Boxes stay per barcode (ASSUMPTION, see RecordStickerScan).
            cmd.CommandText =
                "WITH items AS ( " +
                "  SELECT i.urutan, i.barcode, i.jumlah, s.tanggal, s.line_production_id, s.created_at, s.version, i.added_at, " +
                "         SUM(i.jumlah) OVER (PARTITION BY i.barcode ORDER BY i.urutan ROWS UNBOUNDED PRECEDING) - i.jumlah AS first_off, " +
                "         CAST(ROW_NUMBER() OVER (PARTITION BY i.barcode ORDER BY i.urutan) AS INT) AS occurrence, " +
                "         COUNT(*) OVER (PARTITION BY i.barcode) AS same_rows, " +
                "         SUM(i.jumlah) OVER (PARTITION BY i.barcode) AS barcode_total " +
                "  FROM schedule s " +
                "  JOIN schedule_item i ON i.schedule_id = s.id " +
                "  WHERE s.tanggal = @tanggal AND s.line_production_id = @lp) " +
                "SELECT it.urutan, it.barcode, it.jumlah, " +
                "       COALESCE(p.part_number_code, p.part_number), p.job_no, p.position, p.target_per_jam, " +
                "       ISNULL(sc.cnt, 0), ISNULL(p.qty_per_box, 0), p.part, " +
                "       CASE WHEN it.jumlah > 0 THEN ((it.jumlah - 1) / CASE WHEN p.qty_per_box > 0 THEN p.qty_per_box ELSE 1 END) + 1 ELSE 0 END, " +
                "       (SELECT COUNT(*) FROM dbo.box b WHERE b.tanggal = it.tanggal AND b.line_production_id = it.line_production_id " +
                "          AND b.barcode = it.barcode AND b.closed_at IS NOT NULL), " +
                "       (SELECT COUNT(*) FROM sticker_scan c JOIN dbo.box b ON b.id = c.box_id WHERE b.tanggal = it.tanggal " +
                "          AND b.line_production_id = it.line_production_id AND b.barcode = it.barcode AND b.closed_at IS NULL), " +
                "       p.customer, " +
                "       it.first_off, it.occurrence, it.same_rows, it.barcode_total, it.created_at, sc.last_at, " +
                "       (SELECT TOP (1) b.serial FROM dbo.box b WHERE b.tanggal = it.tanggal AND b.line_production_id = it.line_production_id " +
                "          AND b.barcode = it.barcode AND b.closed_at IS NULL ORDER BY b.box_no DESC), " +
                "       it.version, p.model, it.added_at " +
                "FROM items it " +
                "LEFT JOIN product p ON p.barcode = it.barcode " +
                "OUTER APPLY (SELECT COUNT(c.id) AS cnt, MAX(c.scanned_at) AS last_at " +
                "             FROM print_sticker ps JOIN sticker_scan c ON c.print_sticker_id = ps.id " +
                "             WHERE ps.tanggal = it.tanggal AND ps.line_production_id = it.line_production_id AND ps.barcode = it.barcode " +
                "               AND ps.sticker_no > it.first_off " +
                "               AND (ps.sticker_no <= it.first_off + it.jumlah OR it.occurrence = it.same_rows)) sc " +
                "ORDER BY it.urutan";
            cmd.Parameters.Add("@tanggal", System.Data.SqlDbType.Date).Value = tanggal.Date;
            cmd.Parameters.AddWithValue("@lp", lineProductionId);
            cmd.CommandTimeout = 30;

            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                int firstOffset = r.GetInt32(14);
                int occurrence = r.GetInt32(15);
                int sameRows = r.GetInt32(16);
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
                    BoxesRequired = r.GetInt32(10),
                    CompletedBoxes = r.GetInt32(11),
                    OpenBoxScans = r.GetInt32(12),
                    Customer = r.IsDBNull(13) ? "" : r.GetString(13).Trim(),
                    FirstNo = firstOffset + 1,
                    LastNo = occurrence == sameRows ? int.MaxValue : firstOffset + r.GetInt32(2),
                    Occurrence = occurrence,
                    SameBarcodeRows = sameRows,
                    BarcodeTotalJumlah = r.GetInt32(17),
                    ScheduleCreatedAt = r.IsDBNull(18) ? null : r.GetDateTime(18),
                    LastScanAt = r.IsDBNull(19) ? null : r.GetDateTime(19),
                    OpenBoxSerial = r.IsDBNull(20) ? "" : r.GetString(20),
                    ScheduleVersion = r.GetInt32(21),
                    Model = r.IsDBNull(22) ? "" : r.GetString(22).Trim(),
                    AddedAt = r.IsDBNull(23) ? null : r.GetDateTime(23)
                });
            }
            return list;
        }

        /// <summary>The current schedule version and, when available, details of its latest edit.</summary>
        public sealed record ScheduleRevision(int VersionNo, DateTime? EditedAt, string Reason, string Machine);

        /// <summary>The current schedule version and latest edit details of this date + line, or null if no schedule exists.</summary>
        public static ScheduleRevision? LoadLatestRevision(DateTime tanggal, int lineProductionId)
        {
            using var con = new SqlConnection(ConnectionString);
            con.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText =
                "SELECT s.version, e.edited_at, e.reason, e.machine_name " +
                "FROM schedule s " +
                "OUTER APPLY (SELECT TOP (1) edited_at, reason, machine_name " +
                "            FROM schedule_edit WHERE schedule_id = s.id ORDER BY version_no DESC) e " +
                "WHERE s.tanggal = @tanggal AND s.line_production_id = @lp";
            cmd.Parameters.Add("@tanggal", System.Data.SqlDbType.Date).Value = tanggal.Date;
            cmd.Parameters.AddWithValue("@lp", lineProductionId);
            cmd.CommandTimeout = 10;
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            return new ScheduleRevision(r.GetInt32(0), r.IsDBNull(1) ? null : r.GetDateTime(1),
                r.IsDBNull(2) ? "" : r.GetString(2).Trim(),
                r.IsDBNull(3) ? "" : r.GetString(3).Trim());
        }

        private static SqlCommand Command(SqlConnection con, SqlTransaction tx, string sql)
        {
            var cmd = con.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            return cmd;
        }

        private static void AddJobParameters(SqlCommand cmd, DateTime tanggal, int lineProductionId, string barcode)
        {
            cmd.Parameters.Add("@tanggal", System.Data.SqlDbType.Date).Value = tanggal.Date;
            cmd.Parameters.AddWithValue("@lp", lineProductionId);
            cmd.Parameters.AddWithValue("@barcode", barcode);
        }

        private static ScanResult Reject(ScanOutcome outcome, string message) =>
            new ScanResult { Outcome = outcome, Message = message };

        /// <summary>
        /// Records a scan only when the exact serial belongs to a sticker printed for this date,
        /// production line and scheduled product. A sticker can be scanned only once.
        /// The scan is assigned to the open box of the job (a new box is opened when needed); a box is closed
        /// when it holds qtyPerBox scans, or when the job reaches its planned quantity (then it is marked partial).
        /// All scans of one job are serialised with an application lock, so several scan stations can work on the
        /// same job without deadlocks or two stations opening the same box number.
        ///
        /// SAME BARCODE IN MORE THAN ONE ROW of the schedule: <paramref name="plannedQuantity"/> is the planned quantity of
        /// ALL rows with this barcode, and the sticker must be inside this row's number range (rowFirstNo..rowLastNo,
        /// rowQuantity = this row's Jumlah). ASSUMPTION - REVISIT LATER: boxes belong to the barcode, not to the row, so
        /// a partly filled box at the end of a row is NOT closed; the next row's scans keep filling it. Only the last
        /// piece of the whole barcode closes a partial box. (Same assumption as DailyScheduler, MainWindow.StickerNumberOffset.)
        /// </summary>
        public static ScanResult RecordStickerScan(DateTime tanggal, int lineProductionId, string barcode, string serial,
            int qtyPerBox, int plannedQuantity, int rowFirstNo, int rowLastNo, int rowQuantity)
        {
            serial = (serial ?? "").Trim();
            barcode = (barcode ?? "").Trim();
            if (serial.Length == 0)
                return Reject(ScanOutcome.InvalidInput, "Kode scan kosong.");
            if (qtyPerBox <= 0)
                return Reject(ScanOutcome.InvalidInput, "Qty per box produk ini belum diatur. Scan tidak disimpan.");
            if (plannedQuantity <= 0 || rowQuantity <= 0)
                return Reject(ScanOutcome.InvalidInput, "Jumlah job belum valid. Scan tidak disimpan.");

            using var con = new SqlConnection(ConnectionString);
            con.Open();
            // No explicit Rollback needed on early returns: disposing an uncommitted transaction rolls it back.
            using var tx = con.BeginTransaction();
            try
            {
                // 1. One scan at a time per job (date + line + product).
                using (var cmd = Command(con, tx,
                    "DECLARE @r INT; " +
                    "EXEC @r = sp_getapplock @Resource = @res, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 5000; " +
                    "SELECT @r;"))
                {
                    cmd.Parameters.AddWithValue("@res", "scan:" + tanggal.ToString("yyyyMMdd") + ":" + lineProductionId + ":" + barcode);
                    if (Convert.ToInt32(cmd.ExecuteScalar()) < 0)
                        return Reject(ScanOutcome.Busy, "Sistem sedang sibuk. Scan ulang item ini.");
                }

                // 2. The serial must belong to a sticker printed for this exact job.
                long printStickerId;
                int stickerNo = 0;
                using (var cmd = Command(con, tx,
                    "SELECT id, sticker_no FROM print_sticker " +
                    "WHERE tanggal = @tanggal AND line_production_id = @lp AND barcode = @barcode AND serial = @serial"))
                {
                    AddJobParameters(cmd, tanggal, lineProductionId, barcode);
                    cmd.Parameters.AddWithValue("@serial", serial);
                    object? value = null;
                    using (var reader = cmd.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            value = Convert.ToInt64(reader.GetValue(0));
                            stickerNo = Convert.ToInt32(reader.GetValue(1));
                        }
                    }
                    if (value == null || value == DBNull.Value)
                    {
                        // Same serial on another product of this line/date: the operator picked the wrong job.
                        cmd.Parameters.Clear();
                        cmd.CommandText =
                            "SELECT TOP (1) 1 FROM print_sticker " +
                            "WHERE tanggal = @tanggal AND line_production_id = @lp AND serial = @serial";
                        cmd.Parameters.Add("@tanggal", System.Data.SqlDbType.Date).Value = tanggal.Date;
                        cmd.Parameters.AddWithValue("@lp", lineProductionId);
                        cmd.Parameters.AddWithValue("@serial", serial);
                        object? other = cmd.ExecuteScalar();
                        return other != null && other != DBNull.Value
                            ? Reject(ScanOutcome.WrongJob, "PERINGATAN: Item ini bukan bagian dari job yang sedang aktif.")
                            : Reject(ScanOutcome.NotFound, "PERINGATAN: Serial tidak ditemukan untuk line/tanggal ini. Periksa barcode atau cetak stikernya terlebih dahulu.");
                    }
                    printStickerId = Convert.ToInt64(value);
                }

                // 3. Not scanned before, and not written off.
                using (var cmd = Command(con, tx,
                    "SELECT CASE " +
                    "WHEN EXISTS (SELECT 1 FROM sticker_scan WHERE print_sticker_id = @id) THEN 1 " +
                    "WHEN EXISTS (SELECT 1 FROM sticker_writeoff WHERE print_sticker_id = @id) THEN 2 ELSE 0 END"))
                {
                    cmd.Parameters.AddWithValue("@id", printStickerId);
                    int state = Convert.ToInt32(cmd.ExecuteScalar());
                    if (state == 1) return Reject(ScanOutcome.AlreadyScanned, "Stiker ini sudah pernah discan.");
                    if (state == 2) return Reject(ScanOutcome.WrittenOff, "Stiker ini sudah ditandai hilang/rusak dan tidak dapat discan.");
                }

                // 4. The sticker must belong to this row, and neither the row nor the whole barcode may already be full.
                if (stickerNo < rowFirstNo || stickerNo > rowLastNo)
                    return Reject(ScanOutcome.WrongJob,
                        "PERINGATAN: Stiker no. " + stickerNo + " bukan milik baris job yang sedang aktif (nomor " + rowFirstNo +
                        " - " + (rowLastNo == int.MaxValue ? "akhir" : rowLastNo.ToString()) + ").");

                int totalBefore, rowBefore;
                using (var cmd = Command(con, tx,
                    "SELECT COUNT(*), COUNT(CASE WHEN s.sticker_no >= @firstNo AND s.sticker_no <= @lastNo THEN 1 END) " +
                    "FROM sticker_scan c JOIN print_sticker s ON s.id = c.print_sticker_id " +
                    "WHERE s.tanggal = @tanggal AND s.line_production_id = @lp AND s.barcode = @barcode"))
                {
                    AddJobParameters(cmd, tanggal, lineProductionId, barcode);
                    cmd.Parameters.AddWithValue("@firstNo", rowFirstNo);
                    cmd.Parameters.AddWithValue("@lastNo", rowLastNo);
                    using var reader = cmd.ExecuteReader();
                    reader.Read();
                    totalBefore = reader.GetInt32(0);
                    rowBefore = reader.GetInt32(1);
                }
                if (totalBefore >= plannedQuantity || rowBefore >= rowQuantity)
                    return Reject(ScanOutcome.JobFull, "Jumlah scan untuk job ini sudah mencapai jumlah rencana.");

                // 5. The open box of this job, or a new one.
                int boxId;
                using (var cmd = Command(con, tx,
                    "SELECT TOP (1) id FROM dbo.box " +
                    "WHERE tanggal = @tanggal AND line_production_id = @lp AND barcode = @barcode AND closed_at IS NULL " +
                    "ORDER BY box_no DESC"))
                {
                    AddJobParameters(cmd, tanggal, lineProductionId, barcode);
                    object? value = cmd.ExecuteScalar();
                    if (value == null || value == DBNull.Value)
                    {
                        cmd.Parameters.Clear();
                        // Box name (stored in box.serial, the content of the box label QR):
                        //   B + yyMMdd + "." + line map code + "." + barcode + "." + box number with 3 digits
                        //   e.g. B261119.13.9CE.001  (a box number above 999 simply gets more digits).
                        // The map code is the same one DailyScheduler puts in the sticker serials. When the line has no map code
                        // the name stays empty (NULL) instead of getting a wrong name. box_no is unique per job and the whole
                        // insert runs inside the job's application lock, so the name is unique too (UX_box_serial).
                        cmd.CommandText =
                            "INSERT INTO dbo.box (tanggal, line_production_id, barcode, box_no, serial, qty_per_box, windows_user, machine_name) " +
                            "OUTPUT INSERTED.id " +
                            "SELECT @tanggal, @lp, @barcode, n.box_no, " +
                            "       CASE WHEN m.map_code IS NULL THEN NULL " +
                            "            ELSE N'B' + CONVERT(NVARCHAR(6), @tanggal, 12) + N'.' + m.map_code + N'.' + @barcode + N'.' + " +
                            "                 CASE WHEN n.box_no < 1000 THEN RIGHT(N'00' + CAST(n.box_no AS NVARCHAR(10)), 3) " +
                            "                      ELSE CAST(n.box_no AS NVARCHAR(10)) END " +
                            "       END, " +
                            "       @qty, @user, @machine " +
                            "FROM (SELECT ISNULL(MAX(box_no), 0) + 1 AS box_no FROM dbo.box " +
                            "      WHERE tanggal = @tanggal AND line_production_id = @lp AND barcode = @barcode) n " +
                            "OUTER APPLY (SELECT NULLIF(LTRIM(RTRIM(map_code)), N'') AS map_code " +
                            "            FROM dbo.line_production WHERE id = @lp) m";
                        AddJobParameters(cmd, tanggal, lineProductionId, barcode);
                        cmd.Parameters.AddWithValue("@qty", qtyPerBox);
                        cmd.Parameters.AddWithValue("@user", Environment.UserName);
                        cmd.Parameters.AddWithValue("@machine", Environment.MachineName);
                        boxId = Convert.ToInt32(cmd.ExecuteScalar());
                    }
                    else boxId = Convert.ToInt32(value);
                }

                // 6. Save the scan.
                using (var cmd = Command(con, tx,
                    "INSERT INTO sticker_scan (print_sticker_id, box_id, windows_user, machine_name) " +
                    "VALUES (@id, @box, @user, @machine)"))
                {
                    cmd.Parameters.AddWithValue("@id", printStickerId);
                    cmd.Parameters.AddWithValue("@box", boxId);
                    cmd.Parameters.AddWithValue("@user", Environment.UserName);
                    cmd.Parameters.AddWithValue("@machine", Environment.MachineName);
                    cmd.ExecuteNonQuery();
                }

                // 7. Close the box when it is full, or when this was the last piece of the job (partial box).
                int scansInBox;
                using (var cmd = Command(con, tx, "SELECT COUNT(*) FROM sticker_scan WHERE box_id = @box"))
                {
                    cmd.Parameters.AddWithValue("@box", boxId);
                    scansInBox = Convert.ToInt32(cmd.ExecuteScalar());
                }

                bool jobComplete = totalBefore + 1 >= plannedQuantity;
                bool closeBox = scansInBox >= qtyPerBox || jobComplete;
                if (closeBox)
                {
                    using var cmd = Command(con, tx,
                        "UPDATE dbo.box SET closed_at = SYSDATETIME(), is_partial = @partial WHERE id = @box");
                    cmd.Parameters.AddWithValue("@partial", scansInBox < qtyPerBox);
                    cmd.Parameters.AddWithValue("@box", boxId);
                    cmd.ExecuteNonQuery();
                }

                int completedBoxes;
                using (var cmd = Command(con, tx,
                    "SELECT COUNT(*) FROM dbo.box WHERE tanggal = @tanggal AND line_production_id = @lp " +
                    "AND barcode = @barcode AND closed_at IS NOT NULL"))
                {
                    AddJobParameters(cmd, tanggal, lineProductionId, barcode);
                    completedBoxes = Convert.ToInt32(cmd.ExecuteScalar());
                }

                string boxSerial = "";
                using (var cmd = Command(con, tx, "SELECT serial FROM dbo.box WHERE id = @box"))
                {
                    cmd.Parameters.AddWithValue("@box", boxId);
                    object? v = cmd.ExecuteScalar();
                    if (v != null && v != DBNull.Value) boxSerial = Convert.ToString(v) ?? "";
                }

                tx.Commit();

                int scansInOpenBox = closeBox ? 0 : scansInBox;
                int boxNumber = closeBox ? completedBoxes : completedBoxes + 1;
                return new ScanResult
                {
                    Outcome = ScanOutcome.Saved,
                    Message = "Scan tersimpan. Box " + boxNumber + ": " + scansInBox + "/" + qtyPerBox + ".",
                    CompletedBoxes = completedBoxes,
                    ScansInOpenBox = scansInOpenBox,
                    BoxSerial = boxSerial
                };
            }
            catch (SqlException ex) when (ex.Number == 2601 || ex.Number == 2627)
            {
                // unique index on sticker_scan(print_sticker_id): another station scanned it first
                return Reject(ScanOutcome.AlreadyScanned, "Stiker ini sudah pernah discan.");
            }
            catch (SqlException ex) when (ex.Number == 1205)
            {
                return Reject(ScanOutcome.Busy, "Sistem sedang sibuk. Scan ulang item ini.");
            }
        }
    }
}

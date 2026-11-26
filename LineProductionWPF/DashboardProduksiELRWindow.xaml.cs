using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Controls.Primitives;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace LineProductionWPF
{
    /// <summary>
    /// Line dashboard. The boards show today's schedule made in DailyScheduler (read from its database):
    /// target (planned), actual (scanned), count (pcs per hour) and selisih, each as "current job / whole day".
    /// </summary>
    public partial class DashboardProduksiELRWindow : Window
    {
        private static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(5);   // how often the database is read
        private const int DefaultBoxesPerPallet = 10;   // not in the database yet

        // ---- schedule (from the DailyScheduler database) ----
        private readonly KanbanTracker _tracker = new KanbanTracker();
        private KanbanLine _line;                                     // null = no line picked / no schedule today
        private DateTime _date = DateTime.Today;
        private List<KanbanJob> _jobs = new List<KanbanJob>();
        private KanbanJob _current;                                   // the job on the boards
        private DateTime _lastRead = DateTime.MinValue;
        private bool _reading;
        private int? _lastScheduleRevisionVersion;
        private DateTime _scheduleNoticeUntil = DateTime.MinValue;
        private string _scheduleNoticeText = "";

        // ---- scan panel (box / pallet of the current job) ----
        private readonly ObservableCollection<string> _scans = new ObservableCollection<string>();
        private readonly HashSet<string> _allScanned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // quick duplicate check (the database is the real one)
        private readonly List<string> _scanHistory = new List<string>();
        private int _scanHistoryPosition;
        private string _scanHistoryDraft = "";
        private bool _pickerOpen;

        // Scans are saved one at a time, in the order they were scanned, each for the job that was active when scanned.
        private sealed record PendingScan(KanbanLine Line, KanbanJob Job, DateTime Date, string Code);
        private readonly Queue<PendingScan> _scanQueue = new Queue<PendingScan>();
        private bool _scanBusy;
        private int _scanVersion;          // +1 per saved scan: tells whether a database read is older than a scan
        private bool _syncBoxProgress;     // next Render() takes the box progress from the (fresh) database read
        private readonly DispatcherTimer _popupTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        private readonly DispatcherTimer _clockTimer = new DispatcherTimer();
        private readonly DispatcherTimer _holdTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)   // how long 10 / 10 stays on screen
        };
        // After a box completes the scan list stays visible for a moment (so the last scan is not gone at once), then clears.
        private readonly DispatcherTimer _clearListTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        private int ScanPerBox => _current != null && _current.QtyPerBox > 0 ? _current.QtyPerBox : 1;
        private int BoxesPerPallet => DefaultBoxesPerPallet;
        private int BoxesCompleted => _palletCount * BoxesPerPallet + _boxCount;
        private int _scansInBox, _boxCount, _palletCount;
        private string _boxName = "";      // name of the box being filled (or the one that was just completed), e.g. B261119.13.9CE.001
        private bool _boxFull;             // true right after a scan completed the box (shown as "penuh")

        public DashboardProduksiELRWindow()
        {
            InitializeComponent();
            webHost.LayoutUpdated += (s, e) => UpdateWebBounds();
            webHost.IsVisibleChanged += (s, e) => UpdateWebBounds();
            lstScans.ItemsSource = _scans;

            _popupTimer.Tick += (s, e) => CloseScanPopup();

            _holdTimer.Tick += (s, e) =>
            {
                _holdTimer.Stop();
                txtScanned.Foreground = new SolidColorBrush(Color.FromRgb(0x2F, 0x6F, 0xE4)); // back to blue
                UpdateScanCounters();                                                          // show the real 0
            };

            _clearListTimer.Tick += (s, e) =>
            {
                _clearListTimer.Stop();
                _scans.Clear();
            };

            _clockTimer.Interval = TimeSpan.FromSeconds(1);
            _clockTimer.Tick += (s, e) => Tick();
            _clockTimer.Start();

            UpdateClock();
            Render();
        }

        // ---------- data in ----------
        private async Task PickFirstLine()
        {
            try
            {
                var lines = await Task.Run(() => SchedulerDb.LoadScheduledLines(_date));
                if (lines.Count > 0) SelectLine(lines[0]);
                else ShowNoSchedule();
            }
            catch (Exception ex)
            {
                txtStatus.Text = "Gagal membaca database: " + ex.Message;
            }
        }

        private void ShowNoSchedule()
        {
            _line = null;
            NomorLine.Text = "TIDAK ADA JADWAL";
            txtStatus.Text = "Belum ada jadwal untuk tanggal " + _date.ToString("dd MMM yyyy", CultureInfo.InvariantCulture) + ".";
        }

        private void SelectLine(KanbanLine line)
        {
            _line = line;
            _date = DateTime.Today;
            NomorLine.Text = line.Name;
            if (ReferenceEquals(tabMain.SelectedItem, tabWeb)) _ = ShowWebAsync();   // web tab is open: show the new line
            _jobs = new List<KanbanJob>();
            _lastScheduleRevisionVersion = null;
            _scheduleNoticeUntil = DateTime.MinValue;
            _scheduleNoticeText = "";
            _tracker.Open(_date, line.Id);
            UpdatePauseButton();
            ResetScanPanel();
            _lastRead = DateTime.MinValue;   // read right away
            Tick();
        }

        // every second: clock, count, and (every few seconds) a fresh read of the scan numbers
        private async void Tick()
        {
            UpdateClock();

            if (_line != null && _date != DateTime.Today)   // midnight: a new day, a new schedule
            {
                _date = DateTime.Today;
                _jobs = new List<KanbanJob>();
                _lastScheduleRevisionVersion = null;
                _scheduleNoticeUntil = DateTime.MinValue;
                _scheduleNoticeText = "";
                _tracker.Open(_date, _line.Id);
                UpdatePauseButton();
                _lastRead = DateTime.MinValue;
            }

            UpdateScheduleNoticeCountdown();

            if (_line != null && !_reading && DateTime.Now - _lastRead >= RefreshEvery)
            {
                _reading = true;
                _lastRead = DateTime.Now;
                var line = _line;
                var date = _date;
                int versionBefore = _scanVersion;
                try
                {
                    var jobs = await Task.Run(() => SchedulerDb.LoadJobs(date, line.Id));
                    SchedulerDb.ScheduleRevision? revision = null;
                    string? revisionReadError = null;
                    try
                    {
                        revision = await Task.Run(() => SchedulerDb.LoadLatestRevision(date, line.Id));
                    }
                    catch (Exception ex)
                    {
                        revisionReadError = ex.Message;
                    }

                    if (ReferenceEquals(line, _line) && date == _date)
                    {
                        _jobs = jobs;
                        // Take box progress from this read only if no scan was saved or is waiting meanwhile,
                        // otherwise the read is older than the panel.
                        _syncBoxProgress = versionBefore == _scanVersion && !_scanBusy && _scanQueue.Count == 0;
                        bool newRevision = revisionReadError == null && UpdateScheduleRevisionNotice(revision);
                        if (!newRevision && DateTime.Now >= _scheduleNoticeUntil)
                        {
                            txtStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80));
                            txtStatus.FontWeight = FontWeights.Normal;
                            txtStatus.Text = revisionReadError != null
                                ? "Gagal memeriksa revisi jadwal: " + revisionReadError
                                : jobs.Count == 0
                                    ? "Belum ada jadwal untuk " + line.Name + " tanggal " + date.ToString("dd MMM yyyy", CultureInfo.InvariantCulture) + "."
                                    : "Data diperbarui " + DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
                        }
                    }
                }
                catch (Exception ex)
                {
                    txtStatus.Text = "Gagal membaca database (angka terakhir tetap ditampilkan): " + ex.Message;
                }
                finally
                {
                    _reading = false;
                }
            }

            Render();
        }

        private bool UpdateScheduleRevisionNotice(SchedulerDb.ScheduleRevision? revision)
        {
            if (revision == null)
            {
                _lastScheduleRevisionVersion = null;
                return false;
            }

            bool isNewRevision = _lastScheduleRevisionVersion.HasValue &&
                                 revision.VersionNo > _lastScheduleRevisionVersion.Value;
            _lastScheduleRevisionVersion = revision.VersionNo;
            if (!isNewRevision) return false;

            string editedAt = revision.EditedAt.HasValue
                ? " " + revision.EditedAt.Value.ToString("HH:mm", CultureInfo.InvariantCulture)
                : "";
            string editor = string.IsNullOrWhiteSpace(revision.Editor) ? "" : " oleh " + revision.Editor;
            if (!string.IsNullOrWhiteSpace(revision.Machine))
                editor += string.IsNullOrWhiteSpace(revision.Editor) ? " oleh " + revision.Machine : " @ " + revision.Machine;
            string reason = string.IsNullOrWhiteSpace(revision.Reason) ? "" : ": " + revision.Reason;
            string changes = revision.Changes.Count == 0
                ? "(tidak ada perbedaan)"
                : string.Join(Environment.NewLine, revision.Changes);
            _scheduleNoticeText = "JADWAL DIREVISI (v" + revision.VersionNo + ")" + editedAt + editor + reason +
                                  Environment.NewLine + "Perubahan jadwal:" + Environment.NewLine + changes;
            _scheduleNoticeUntil = DateTime.Now.AddMinutes(1);
            UpdateScheduleNoticeCountdown();
            ScanSounds.PlayScheduleRevision();
            return true;
        }

        private void UpdateScheduleNoticeCountdown()
        {
            if (_scheduleNoticeText.Length == 0) return;

            TimeSpan remaining = _scheduleNoticeUntil - DateTime.Now;
            if (remaining <= TimeSpan.Zero)
            {
                _scheduleNoticeText = "";
                txtStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80));
                txtStatus.FontWeight = FontWeights.Normal;
                txtStatus.Text = _jobs.Count == 0
                    ? "Belum ada jadwal untuk tanggal " + _date.ToString("dd MMM yyyy", CultureInfo.InvariantCulture) + "."
                    : "Data diperbarui " + DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
                return;
            }

            int seconds = (int)Math.Ceiling(remaining.TotalSeconds);
            txtStatus.Text = _scheduleNoticeText + Environment.NewLine + "(hilang dalam " +
                             (seconds / 60).ToString("00", CultureInfo.InvariantCulture) + ":" +
                             (seconds % 60).ToString("00", CultureInfo.InvariantCulture) + ")";
            txtStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xB9, 0x1C, 0x1C));
            txtStatus.FontWeight = FontWeights.Bold;
        }

        // "Jeda Counter" pauses the expected count (target_per_jam x time); the same button then reads "Lanjut Counter".
        private void btnPause_Click(object sender, RoutedEventArgs e)
        {
            if (_line == null) return;

            if (_tracker.IsPaused) _tracker.Resume(DateTime.Now);
            else _tracker.Pause(DateTime.Now);

            UpdatePauseButton();
            Render();
            txtScan.Focus();   // keep the scanner ready
        }

        private void UpdatePauseButton()
        {
            if (_tracker.IsPaused)
            {
                btnPause.Content = "Lanjut Counter";
                btnPause.Background = new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A));   // green: press to continue
                txtStatus.Text = "Counter dijeda.";
            }
            else
            {
                btnPause.Content = "Jeda Counter";
                btnPause.ClearValue(Button.BackgroundProperty);                               // back to the red style
            }
        }

        // ---------- display ----------
        private void Render()
        {
            KanbanNumbers n = _tracker.Calculate(_jobs, DateTime.Now);

            // the next job became current: the scan panel starts fresh
            if (!ReferenceEquals(n.Current, _current) && (n.Current == null || _current == null || n.Current.Urutan != _current.Urutan))
                ResetScanPanel();
            _current = n.Current;
            foreach (KanbanJob job in _jobs)
            {
                job.IsCurrent = _current != null &&
                                job.Urutan == _current.Urutan &&
                                job.Barcode == _current.Barcode;
            }

            // After a restart or a job change the panel shows what the database holds (also scans from other stations).
            if (_syncBoxProgress && _current != null)
            {
                _scansInBox = _current.OpenBoxScans;
                _boxName = _current.OpenBoxSerial;
                _boxFull = false;
                _boxCount = _current.CompletedBoxes % BoxesPerPallet;
                _palletCount = _current.CompletedBoxes / BoxesPerPallet;
            }
            _syncBoxProgress = false;

            UpdateCustomer(_current?.Customer);
            PartNomor.Text = _current?.PartNumber ?? "-";
            Posisi.Text = _current == null || string.IsNullOrWhiteSpace(_current.Posisi) ? "-" : _current.Posisi;
            PartHeader.Text = _current == null || string.IsNullOrWhiteSpace(_current.Part) ? "-" : _current.Part;
            Model.Text = _current == null || string.IsNullOrWhiteSpace(_current.Model) ? "-" : _current.Model;
            bool hasJobNo = _current != null && !string.IsNullOrWhiteSpace(_current.JobNo) && _current.JobNo != "-";
            JobNo.Text = hasJobNo ? _current!.JobNo : "";
            JobNo.Visibility = hasJobNo ? Visibility.Visible : Visibility.Collapsed;
            HeaderJobSeparator.Visibility = hasJobNo ? Visibility.Visible : Visibility.Collapsed;

            txtTarget.Text = n.TargetJob.ToString();
            txtTargetJam.Text = n.TargetDay.ToString();
            txtActual.Text = n.ActualJob.ToString();
            txtActualJam.Text = n.ActualDay.ToString();
            txtCount.Text = n.CountJob.ToString();
            txtCountJam.Text = n.CountDay.ToString();

            UpdateSelisih(n.SelisihJob, n.SelisihDay);
            UpdateScanCounters();

            // the table: re-bind when the jobs list changed, otherwise only refresh the numbers
            if (!ReferenceEquals(dgvSchedule.ItemsSource, _jobs)) dgvSchedule.ItemsSource = _jobs;
            else dgvSchedule.Items.Refresh();
        }

        // Customer name + logo of the current job. Render() runs every second, so only reload the logo file when the customer changes.
        private string? _customerShown;

        private void UpdateCustomer(string? customer)
        {
            customer = (customer ?? "").Trim();
            if (customer == _customerShown) return;
            _customerShown = customer;

            txtCustomer.Text = customer.Length == 0 ? "-" : customer;
            var logo = CustomerLogo.Load(customer);
            imgCustomerLogo.Source = logo;
            imgCustomerLogo.Visibility = logo == null ? Visibility.Collapsed : Visibility.Visible;
        }

        private void ResetScanPanel()
        {
            _holdTimer.Stop();
            _clearListTimer.Stop();
            txtScanned.Foreground = new SolidColorBrush(Color.FromRgb(0x2F, 0x6F, 0xE4));
            _scans.Clear();
            _allScanned.Clear();
            _scanHistory.Clear();
            _scanHistoryPosition = 0;
            _scanHistoryDraft = "";
            _scansInBox = _boxCount = _palletCount = 0;
            _boxName = "";
            _boxFull = false;
        }

        // ---------- scanning ----------
        private void txtScan_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Up)
            {
                if (_scanHistory.Count == 0) return;
                if (_scanHistoryPosition == _scanHistory.Count)
                    _scanHistoryDraft = txtScan.Text;
                if (_scanHistoryPosition > 0)
                {
                    _scanHistoryPosition--;
                    ShowScanHistoryItem(_scanHistory[_scanHistoryPosition]);
                }
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Down && _scanHistoryPosition < _scanHistory.Count)
            {
                _scanHistoryPosition++;
                ShowScanHistoryItem(_scanHistoryPosition == _scanHistory.Count
                    ? _scanHistoryDraft
                    : _scanHistory[_scanHistoryPosition]);
                e.Handled = true;
                return;
            }

            if (e.Key != Key.Enter) return;

            string code = txtScan.Text.Trim();
            txtScan.Clear();
            _scanHistoryPosition = _scanHistory.Count;
            _scanHistoryDraft = "";
            if (code.Length > 0) ProcessScan(code);
        }

        private void ShowScanHistoryItem(string code)
        {
            txtScan.Text = code;
            txtScan.CaretIndex = txtScan.Text.Length;
        }

        // Every scanned QR serial is saved against the job that was active when it was scanned.
        // Scans are queued so they are saved in order and the panel never shows an older result after a newer one.
        private void ProcessScan(string code)
        {
            code = code.Trim();
            if (code.Length == 0 || _current == null || _line == null) return;

            _scanQueue.Enqueue(new PendingScan(_line, _current, _date, code));
            if (!_scanBusy) _ = DrainScanQueue();
        }

        private async Task DrainScanQueue()
        {
            _scanBusy = true;
            try
            {
                while (_scanQueue.Count > 0)
                    await SaveScan(_scanQueue.Dequeue());
            }
            finally
            {
                _scanBusy = false;
            }
        }

        // The jobs list is replaced by every database read, so the job is matched by value, not by reference.
        private bool IsActive(PendingScan p) =>
            ReferenceEquals(p.Line, _line) && p.Date == _date && _current != null &&
            _current.Urutan == p.Job.Urutan && _current.Barcode == p.Job.Barcode;

        private async Task SaveScan(PendingScan p)
        {
            if (IsActive(p) && _allScanned.Contains(p.Code))
            {
                const string warning = "Item ini sudah discan pada sesi ini.";
                txtStatus.Text = "PERINGATAN: " + warning;
                ShowScanPopup(warning, p.Code, ScanOutcome.AlreadyScanned);
                System.Media.SystemSounds.Beep.Play();
                return;
            }

            try
            {
                txtStatus.Text = "Menyimpan scan...";
                // SAME BARCODE IN MORE THAN ONE ROW: the scan is checked against this row's sticker range, while the box
                // (and the planned total that closes the last partial box) belongs to the whole barcode.
                ScanResult result = await Task.Run(() =>
                    SchedulerDb.RecordStickerScan(p.Date, p.Line.Id, p.Job.Barcode, p.Code, p.Job.QtyPerBox,
                        p.Job.BarcodeTotalJumlah, p.Job.FirstNo, p.Job.LastNo, p.Job.Jumlah));

                if (!result.Saved)
                {
                    txtStatus.Text = result.Message;
                    ShowScanPopup(result.Message, p.Code, result.Outcome);
                    System.Media.SystemSounds.Beep.Play();
                    return;
                }

                _scanVersion++;
                _lastRead = DateTime.MinValue;   // refresh the actual production count right away

                // The job may have changed during the database call: the scan is saved, but this panel is another job's.
                if (!IsActive(p)) return;

                _allScanned.Add(p.Code);
                _scanHistory.Add(p.Code);
                _scanHistoryPosition = _scanHistory.Count;
                _scanHistoryDraft = "";

                // The previous box's list is still on screen (held for a moment): the next scan starts the new list now.
                if (_clearListTimer.IsEnabled)
                {
                    _clearListTimer.Stop();
                    _scans.Clear();
                }
                _scans.Add(p.Code);
                lstScans.ScrollIntoView(p.Code);

                // The database is authoritative: a box holds qty_per_box individual sticker scans.
                _scansInBox = result.ScansInOpenBox;
                _boxName = result.BoxSerial;                                        // the box this scan went into
                _boxFull = result.ScansInOpenBox == 0 && result.CompletedBoxes > 0; // that box is now complete
                _boxCount = result.CompletedBoxes % BoxesPerPallet;
                _palletCount = result.CompletedBoxes / BoxesPerPallet;
                if (result.ScansInOpenBox == 0 && result.CompletedBoxes > 0 && result.CompletedBoxes % BoxesPerPallet == 0)
                    ShowPalletFull();

                // A box was just completed: the list on screen is emptied after a short hold (so the last scan stays visible
                // for a moment) and the next box starts with a clean list.
                // Only the visible list is cleared; _allScanned (the "already scanned this session" check) is kept.
                if (result.ScansInOpenBox == 0 && result.CompletedBoxes > 0)
                {
                    _clearListTimer.Stop();
                    _clearListTimer.Start();
                }

                txtStatus.Text = result.Message;
                UpdateScanCounters();
            }
            catch (Exception ex)
            {
                // Nothing was saved: tell the operator to scan this item again.
                string message = "Gagal menyimpan scan, scan ulang item ini. (" + ex.Message + ")";
                txtStatus.Text = message;
                ShowScanPopup(message, p.Code, ScanOutcome.Error);
                System.Media.SystemSounds.Hand.Play();
            }
        }

        // Inline warning banner in the scan panel (no separate window). It is not focusable, so the scanner's
        // next characters keep going to the scan box. A new warning replaces the previous one.
        private void ShowScanPopup(string message, string serial, ScanOutcome outcome)
        {
            bool unknownSerial = outcome == ScanOutcome.NotFound;
            Brush accent = unknownSerial ? Brushes.DarkOrange : Brushes.Firebrick;

            scanBanner.Background = unknownSerial
                ? new SolidColorBrush(Color.FromRgb(0xFF, 0xF7, 0xED))
                : new SolidColorBrush(Color.FromRgb(0xFE, 0xF2, 0xF2));
            scanBanner.BorderBrush = accent;
            txtBannerTitle.Foreground = accent;
            txtBannerTitle.Text = unknownSerial ? "SERIAL TIDAK DIKENAL" : "SCAN DITOLAK";
            txtBannerMessage.Text = message;
            txtBannerSerial.Text = "Serial: " + serial;
            scanBanner.Visibility = Visibility.Visible;

            _popupTimer.Stop();
            _popupTimer.Start();   // restarts the wait
        }

        private void CloseScanPopup()
        {
            _popupTimer.Stop();
            scanBanner.Visibility = Visibility.Collapsed;
        }

        private void ShowPalletFull()
        {
            txtScanned.Text = BoxesCompleted.ToString();
            txtScanned.Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)); // green

            _holdTimer.Stop();
            _holdTimer.Start();                     // restarts the one-second wait
        }

        private void UpdateScanCounters()
        {
            txtScanPerBox.Text = "SCAN per BOX: " + _scansInBox + " / " + ScanPerBox + "\n" +
                                 "Box: " + (string.IsNullOrEmpty(_boxName) ? "-" : _boxName + (_boxFull ? "\n(penuh)" : ""));
            txtPerBox.Text = (_current?.BoxesRequired ?? 0).ToString();
            txtPallet.Text = "Pallet: " + _palletCount;

            // while the completed-pallet display is held, don't overwrite the box total
            if (!_holdTimer.IsEnabled)
            {
                txtScanned.Text = BoxesCompleted.ToString();
            }
        }

        private void UpdateSelisih(int selisih, int selisihHari)
        {
            txtSelisih.Text = selisih.ToString();
            txtSelisihJam.Text = selisihHari.ToString();

            const int tolerance = 50;   // how far behind counts as the deepest red

            Color color;
            if (selisih >= 0)
            {
                color = (Color)ColorConverter.ConvertFromString("#22C55E");   // green: caught up
            }
            else
            {
                Color amber = (Color)ColorConverter.ConvertFromString("#F59E0B");
                Color deepRed = (Color)ColorConverter.ConvertFromString("#DC2626");

                double ratio = Math.Min(1.0, -selisih / (double)tolerance);
                color = Blend(amber, deepRed, ratio);
            }

            bdSelisih.Background = new SolidColorBrush(color);

            // text color follows the background
            Brush textBrush = GetTextBrush(color);
            txtSelisihTitle.Foreground = textBrush;
            txtSelisih.Foreground = textBrush;
            txtSelisihJam.Foreground = textBrush;
            txtSelisihCaption.Foreground = textBrush;
        }

        // Light backgrounds get black text, dark backgrounds get white text.
        private static Brush GetTextBrush(Color background)
        {
            double brightness = 0.299 * background.R + 0.587 * background.G + 0.114 * background.B;
            return brightness > 130 ? Brushes.Black : Brushes.White;
        }

        private static Color Blend(Color a, Color b, double t)
        {
            return Color.FromRgb(
                (byte)(a.R + (b.R - a.R) * t),
                (byte)(a.G + (b.G - a.G) * t),
                (byte)(a.B + (b.B - a.B) * t));
        }

        private void UpdateClock()
        {
            DateTime now = DateTime.Now;
            txtClockDate.Text = now.ToString("dd MMMM yyyy", CultureInfo.InvariantCulture);
            txtClockTime.Text = now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        }

        // ---------- keep the cursor in the scan box ----------
        // ---------- auto-fit to any screen ----------
        // The layout is designed for 1366 x 768. The whole UI is scaled by min(width/1366, height/768) (never below MinScale),
        // and rootGrid is given the window size divided by that scale, so the * rows/columns absorb the spare space
        // on the other axis instead of leaving black bars.
        private const double DesignWidth = 1366, DesignHeight = 768, MinScale = 0.7;

        private void fitHost_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyFit(e.NewSize);

        private void ApplyFit(Size host)
        {
            if (host.Width <= 0 || host.Height <= 0) return;
            double s = Math.Max(MinScale, Math.Min(host.Width / DesignWidth, host.Height / DesignHeight));
            scale.ScaleX = scale.ScaleY = s;
            rootGrid.Width = host.Width / s;
            rootGrid.Height = host.Height / s;
            if (_web?.CoreWebView2 != null) _web.ZoomFactor = WebZoom * s;   // the web tab is a native window: it does not follow the transform, so zoom it by the same scale
        }

        // The WebView2 is a native window, so it cannot live inside the scaled tree (its window would keep the unscaled size).
        // It sits in fitHost (not scaled) on top of the webHost placeholder and is moved / sized to cover exactly the
        // placeholder's on-screen rectangle. It is hidden whenever the web tab is not showing.
        private Rect _webRect = Rect.Empty;

        private void UpdateWebBounds()
        {
            if (_web == null) return;
            try
            {
                bool show = webHost.IsVisible && webHost.ActualWidth > 0 && webHost.ActualHeight > 0;
                var vis = show ? Visibility.Visible : Visibility.Collapsed;
                if (_web.Visibility != vis) _web.Visibility = vis;
                if (!show) return;

                Rect r = webHost.TransformToAncestor(fitHost).TransformBounds(new Rect(0, 0, webHost.ActualWidth, webHost.ActualHeight));
                if (r == _webRect) return;   // unchanged: do not trigger another layout pass
                _webRect = r;
                _web.Margin = new Thickness(Math.Round(r.X), Math.Round(r.Y), 0, 0);
                _web.Width = Math.Round(r.Width);
                _web.Height = Math.Round(r.Height);
            }
            catch (InvalidOperationException) { }   // placeholder not connected to the window yet
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            ApplyFit(new Size(fitHost.ActualWidth, fitHost.ActualHeight));
            txtScan.Focus();
            await PickFirstLine();
        }

        private void Window_Activated(object sender, EventArgs e)
        {
            txtScan.Focus();
        }

        private void txtScan_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (_pickerOpen || e.NewFocus is TextBox) return;
            Dispatcher.BeginInvoke(new Action(() => txtScan.Focus()),
                                   DispatcherPriority.Background);
        }

        // ---------- buttons and closing ----------
        // ---------------- Tampilan Web: the production website filtered on the picked line ----------------
        // Read-only display: clicks, selection, context menu, new windows and navigation away are blocked; only scrolling works.
        // Never throws: any failure (runtime missing, browser process crash, site unreachable) shows a message and retries.
        private WebView2? _web;
        private string _webLoaded = "";     // address the web view was last told to open
        private string _webWanted = "";     // address it should show (newest request wins)
        private bool _webBusy;
        private const double WebZoom = 0.75;   // 75 %
        private readonly DispatcherTimer _webRetry = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        private bool _webRetryHooked;

        // Runs in every page: swallow mouse / keyboard actions that could click a link or button, and make text unselectable.
        // Mouse wheel, touch and scroll bars are not affected, so scrolling still works.
        private const string WebLockScript = @"(function () {
            var stop = function (e) { e.preventDefault(); e.stopPropagation(); e.stopImmediatePropagation(); };
            ['click','dblclick','auxclick','contextmenu','mousedown','mouseup','pointerdown','pointerup','dragstart','selectstart','submit']
                .forEach(function (t) { document.addEventListener(t, stop, true); });
            document.addEventListener('keydown', function (e) { if (e.key === 'Tab' || e.key === 'Enter') stop(e); }, true);
            var st = document.createElement('style');
            st.textContent = '*{user-select:none!important;-webkit-user-select:none!important;cursor:default!important}' +
                             'a,button,input,select,textarea,label{pointer-events:none!important}';
            (document.head || document.documentElement).appendChild(st);
        })();";

        private void tabMain_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!ReferenceEquals(e.OriginalSource, tabMain)) return;   // ignore selection changes of grids / combo boxes inside the tabs
            if (ReferenceEquals(tabMain.SelectedItem, tabWeb)) _ = ShowWebAsync();
        }

        private void ShowWebMessage(string text)
        {
            txtWebInfo.Text = text;
            txtWebInfo.Visibility = Visibility.Visible;
        }

        private void ScheduleWebRetry()
        {
            if (!_webRetryHooked)
            {
                _webRetryHooked = true;
                _webRetry.Tick += (s, e) =>
                {
                    _webRetry.Stop();
                    if (ReferenceEquals(tabMain.SelectedItem, tabWeb)) _ = ShowWebAsync();
                };
            }
            _webRetry.Stop();
            _webRetry.Start();
        }

        /// <summary>Throws the web view away; the next ShowWebAsync builds a fresh one.</summary>
        private void ResetWeb()
        {
            if (_web != null)
            {
                try { fitHost.Children.Remove(_web); } catch { }
                _webRect = Rect.Empty;
                try { _web.Dispose(); } catch { }
                _web = null;
            }
            _webLoaded = "";
        }

        private async Task EnsureWebAsync()
        {
            if (_web != null && _web.CoreWebView2 != null) return;
            ResetWeb();   // nothing, or a half-made control

            var web = new WebView2
            {
                CreationProperties = new CoreWebView2CreationProperties
                {
                    // the default folder (next to the .exe) may not be writable
                    UserDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LineProductionWPF", "WebView2")
                }
            };
            web.HorizontalAlignment = HorizontalAlignment.Left;   // positioned by UpdateWebBounds (Margin + Width + Height)
            web.VerticalAlignment = VerticalAlignment.Top;
            web.Visibility = Visibility.Collapsed;                // shown once it has been placed over webHost
            _web = web;
            fitHost.Children.Add(web);
            UpdateWebBounds();
            await web.EnsureCoreWebView2Async();
            ApplyFit(new Size(fitHost.ActualWidth, fitHost.ActualHeight));   // match the web page zoom to the UI scale

            CoreWebView2 core = web.CoreWebView2;
            var st = core.Settings;
            st.AreDefaultContextMenusEnabled = false;
            st.AreDevToolsEnabled = false;
            st.IsStatusBarEnabled = false;
            st.IsZoomControlEnabled = false;
            st.IsPinchZoomEnabled = false;
            st.IsSwipeNavigationEnabled = false;
            st.AreBrowserAcceleratorKeysEnabled = false;
            st.IsGeneralAutofillEnabled = false;
            st.IsPasswordAutosaveEnabled = false;
            await core.AddScriptToExecuteOnDocumentCreatedAsync(WebLockScript);

            core.NewWindowRequested += (s, a) => a.Handled = true;   // no pop-ups

            core.NavigationStarting += (s, a) =>
            {
                // only the wanted page (same host + path, the filter may differ) may load; links to anywhere else are cancelled
                try
                {
                    if (Uri.TryCreate(a.Uri, UriKind.Absolute, out Uri? to) && Uri.TryCreate(_webWanted, UriKind.Absolute, out Uri? want) &&
                        (!to.Authority.Equals(want.Authority, StringComparison.OrdinalIgnoreCase) || to.AbsolutePath != want.AbsolutePath))
                        a.Cancel = true;
                }
                catch { /* let it through */ }
            };

            web.NavigationCompleted += (s, a) =>
            {
                if (a.IsSuccess)
                {
                    txtWebInfo.Visibility = Visibility.Collapsed;
                    web.ZoomFactor = WebZoom * scale.ScaleX;   // keep 75 % (times the UI scale) after every page load
                }
                else if (a.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)   // "canceled" = a newer address replaced this one
                {
                    _webLoaded = "";
                    ShowWebMessage("Halaman web tidak bisa dimuat (" + a.WebErrorStatus + "). Mencoba lagi...");
                    ScheduleWebRetry();
                }
            };

            core.ProcessFailed += (s, a) =>
            {
                // the browser or its renderer died or hung: build a new web view
                ResetWeb();
                ShowWebMessage("Tampilan web berhenti (" + a.ProcessFailedKind + "). Memuat ulang...");
                ScheduleWebRetry();
            };

            web.ZoomFactor = WebZoom * scale.ScaleX;
        }

        private async Task ShowWebAsync()
        {
            try
            {
                if (_line == null)
                {
                    ShowWebMessage("Pilih line dulu.");
                    return;
                }

                _webWanted = SchedulerDb.BuildWebUrl(_line);   // newest request always wins
                if (_webBusy) return;   // the running call picks up the newer address in its loop below

                _webBusy = true;
                try
                {
                    while (true)
                    {
                        string url = _webWanted;
                        await EnsureWebAsync();
                        if (_webLoaded != url)
                        {
                            _webLoaded = url;
                            _web!.CoreWebView2.Navigate(url);
                        }
                        if (url == _webWanted) break;   // nothing newer came in meanwhile
                    }
                }
                finally
                {
                    _webBusy = false;
                }
            }
            catch (Exception ex)
            {
                ResetWeb();
                ShowWebMessage("Tampilan web tidak bisa dibuka (WebView2 Runtime belum terpasang?).\n" + ex.Message +
                               "\n\nMencoba lagi otomatis...");
                ScheduleWebRetry();
            }
        }

        private void btnKeluar_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }

        protected override void OnClosed(EventArgs e)
        {
            _clockTimer.Stop();
            _holdTimer.Stop();
            CloseScanPopup();
            _webRetry.Stop();
            try { _web?.Dispose(); } catch { }
            serialMonitor.Shutdown();
            base.OnClosed(e);
        }

        // ---------- ganti nomor line: only lines that have a schedule today ----------
        private async void bdNomorLine_Click(object sender, MouseButtonEventArgs e)
        {
            List<KanbanLine> lines;
            try
            {
                lines = await Task.Run(() => SchedulerDb.LoadScheduledLines(DateTime.Today));   // fresh: a schedule may have been made just now
            }
            catch (Exception ex)
            {
                txtStatus.Text = "Gagal membaca database: " + ex.Message;
                return;
            }

            if (lines.Count == 0)
            {
                ShowNoSchedule();
                return;
            }

            var menu = new ContextMenu
            {
                PlacementTarget = bdNomorLine,
                Placement = PlacementMode.Bottom,
                LayoutTransform = new ScaleTransform(scale.ScaleX, scale.ScaleY)   // popups live in their own window and do not inherit the UI scale
            };

            foreach (KanbanLine line in lines)
            {
                var item = new MenuItem
                {
                    Header = line.Name,
                    FontSize = 24,
                    Padding = new Thickness(12, 8, 12, 8),
                    IsChecked = _line != null && line.Id == _line.Id
                };
                var chosen = line;
                item.Click += (s, args) => { if (_line == null || chosen.Id != _line.Id) SelectLine(chosen); };
                menu.Items.Add(item);
            }

            _pickerOpen = true;
            menu.Closed += (s, args) => { _pickerOpen = false; txtScan.Focus(); };
            menu.IsOpen = true;
        }
    }
}
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Controls.Primitives;

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

        // ---- scan panel (box / pallet of the current job) ----
        private readonly ObservableCollection<string> _scans = new ObservableCollection<string>();
        private readonly HashSet<string> _allScanned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // quick duplicate check (the database is the real one)
        private bool _pickerOpen;

        // Scans are saved one at a time, in the order they were scanned, each for the job that was active when scanned.
        private sealed record PendingScan(KanbanLine Line, KanbanJob Job, DateTime Date, string Code);
        private readonly Queue<PendingScan> _scanQueue = new Queue<PendingScan>();
        private bool _scanBusy;
        private int _scanVersion;          // +1 per saved scan: tells whether a database read is older than a scan
        private bool _syncBoxProgress;     // next Render() takes the box progress from the (fresh) database read
        private Window? _scanPopup;
        private readonly DispatcherTimer _popupTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        private readonly DispatcherTimer _clockTimer = new DispatcherTimer();
        private readonly DispatcherTimer _holdTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)   // how long 10 / 10 stays on screen
        };
        private int ScanPerBox => _current != null && _current.QtyPerBox > 0 ? _current.QtyPerBox : 1;
        private int BoxesPerPallet => DefaultBoxesPerPallet;
        private int BoxesCompleted => _palletCount * BoxesPerPallet + _boxCount;
        private int _scansInBox, _boxCount, _palletCount;

        public DashboardProduksiELRWindow()
        {
            InitializeComponent();
            lstScans.ItemsSource = _scans;

            _popupTimer.Tick += (s, e) => CloseScanPopup();

            _holdTimer.Tick += (s, e) =>
            {
                _holdTimer.Stop();
                txtScanned.Foreground = new SolidColorBrush(Color.FromRgb(0x2F, 0x6F, 0xE4)); // back to blue
                UpdateScanCounters();                                                          // show the real 0
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
            _jobs = new List<KanbanJob>();
            _tracker.Open(_date, line.Id);
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
                _tracker.Open(_date, _line.Id);
                _lastRead = DateTime.MinValue;
            }

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
                    if (ReferenceEquals(line, _line) && date == _date)
                    {
                        _jobs = jobs;
                        // Take box progress from this read only if no scan was saved or is waiting meanwhile,
                        // otherwise the read is older than the panel.
                        _syncBoxProgress = versionBefore == _scanVersion && !_scanBusy && _scanQueue.Count == 0;
                        _tracker.Update(_jobs, DateTime.Now);
                        txtStatus.Text = jobs.Count == 0
                            ? "Belum ada jadwal untuk " + line.Name + " tanggal " + date.ToString("dd MMM yyyy", CultureInfo.InvariantCulture) + "."
                            : "Data diperbarui " + DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
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

        // ---------- display ----------
        private void Render()
        {
            KanbanNumbers n = _tracker.Calculate(_jobs, DateTime.Now);

            // the next job became current: the scan panel starts fresh
            if (!ReferenceEquals(n.Current, _current) && (n.Current == null || _current == null || n.Current.Urutan != _current.Urutan))
                ResetScanPanel();
            _current = n.Current;

            // After a restart or a job change the panel shows what the database holds (also scans from other stations).
            if (_syncBoxProgress && _current != null)
            {
                _scansInBox = _current.OpenBoxScans;
                _boxCount = _current.CompletedBoxes % BoxesPerPallet;
                _palletCount = _current.CompletedBoxes / BoxesPerPallet;
            }
            _syncBoxProgress = false;

            PartNomor.Text = _current?.PartNumber ?? "-";
            JobNo.Text = _current == null ? "-" : _current.JobNo;
            Posisi.Text = _current == null || _current.PosisiPart.Length == 0 ? "-" : _current.PosisiPart;

            txtTarget.Text = n.TargetJob.ToString();
            txtTargetJam.Text = "/" + n.TargetDay;
            txtActual.Text = n.ActualJob.ToString();
            txtActualJam.Text = "/" + n.ActualDay;
            txtCount.Text = n.CountJob.ToString();
            txtCountJam.Text = "/" + n.CountDay;

            UpdateSelisih(n.SelisihJob, n.SelisihDay);
            UpdateScanCounters();

            // the table: re-bind when the jobs list changed, otherwise only refresh the numbers
            if (!ReferenceEquals(dgvSchedule.ItemsSource, _jobs)) dgvSchedule.ItemsSource = _jobs;
            else dgvSchedule.Items.Refresh();
        }

        private void ResetScanPanel()
        {
            _holdTimer.Stop();
            txtScanned.Foreground = new SolidColorBrush(Color.FromRgb(0x2F, 0x6F, 0xE4));
            _scans.Clear();
            _allScanned.Clear();
            _scansInBox = _boxCount = _palletCount = 0;
        }

        // ---------- scanning ----------
        private void txtScan_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;

            string code = txtScan.Text.Trim();
            txtScan.Clear();
            if (code.Length > 0) ProcessScan(code);
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
                ScanResult result = await Task.Run(() =>
                    SchedulerDb.RecordStickerScan(p.Date, p.Line.Id, p.Job.Barcode, p.Code, p.Job.QtyPerBox, p.Job.Jumlah));

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
                _scans.Add(p.Code);
                lstScans.ScrollIntoView(p.Code);

                // The database is authoritative: a box holds qty_per_box individual sticker scans.
                _scansInBox = result.ScansInOpenBox;
                _boxCount = result.CompletedBoxes % BoxesPerPallet;
                _palletCount = result.CompletedBoxes / BoxesPerPallet;
                if (result.ScansInOpenBox == 0 && result.CompletedBoxes > 0 && result.CompletedBoxes % BoxesPerPallet == 0)
                    ShowPalletFull();

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

        // One small warning window at a time. It must not take the keyboard focus, or the scanner's next
        // characters would go to the popup instead of the scan box and be lost.
        private void ShowScanPopup(string message, string serial, ScanOutcome outcome)
        {
            CloseScanPopup();

            bool unknownSerial = outcome == ScanOutcome.NotFound;
            Brush accent = unknownSerial ? Brushes.DarkOrange : Brushes.Firebrick;
            var popup = new Window
            {
                Title = unknownSerial ? "Serial tidak dikenal" : "Scan ditolak",
                Width = 420,
                SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.ToolWindow,
                ShowInTaskbar = false,
                ShowActivated = false,
                Topmost = true,
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = unknownSerial
                    ? new SolidColorBrush(Color.FromRgb(0xFF, 0xF7, 0xED))
                    : new SolidColorBrush(Color.FromRgb(0xFE, 0xF2, 0xF2)),
                Content = new Border
                {
                    Padding = new Thickness(18),
                    BorderThickness = new Thickness(4, 0, 0, 0),
                    BorderBrush = accent,
                    Child = new StackPanel
                    {
                        Children =
                        {
                            new TextBlock
                            {
                                Text = unknownSerial ? "SERIAL TIDAK DIKENAL" : "SCAN DITOLAK",
                                FontSize = 20,
                                FontWeight = FontWeights.Bold,
                                Foreground = accent,
                                Margin = new Thickness(0, 0, 0, 8)
                            },
                            new TextBlock
                            {
                                Text = message,
                                FontSize = 15,
                                TextWrapping = TextWrapping.Wrap,
                                Foreground = Brushes.Black,
                                Margin = new Thickness(0, 0, 0, 8)
                            },
                            new TextBlock
                            {
                                Text = "Serial: " + serial,
                                FontSize = 14,
                                FontWeight = FontWeights.SemiBold,
                                FontFamily = new FontFamily("Consolas"),
                                Foreground = Brushes.DimGray,
                                TextWrapping = TextWrapping.Wrap
                            }
                        }
                    }
                }
            };

            _scanPopup = popup;
            popup.Show();
            _popupTimer.Stop();
            _popupTimer.Start();   // restarts the 2.5 second wait
        }

        private void CloseScanPopup()
        {
            _popupTimer.Stop();
            Window? popup = _scanPopup;
            _scanPopup = null;
            popup?.Close();
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
            txtScanPerBox.Text = "SCAN per BOX: " + _scansInBox + " / " + ScanPerBox;
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
        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
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
        private void btnKeluar_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }

        protected override void OnClosed(EventArgs e)
        {
            _clockTimer.Stop();
            _holdTimer.Stop();
            CloseScanPopup();
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
                Placement = PlacementMode.Bottom
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

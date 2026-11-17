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
        private readonly HashSet<string> _allScanned = new HashSet<string>();   // duplicate check
        private bool _pickerOpen;
        private readonly DispatcherTimer _clockTimer = new DispatcherTimer();
        private readonly DispatcherTimer _holdTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)   // how long 10 / 10 stays on screen
        };
        private int ScanPerBox => _current != null && _current.QtyPerBox > 0 ? _current.QtyPerBox : 1;
        private int BoxesPerPallet => DefaultBoxesPerPallet;
        private int _scansInBox, _boxCount, _palletCount;

        public DashboardProduksiELRWindow()
        {
            InitializeComponent();
            lstScans.ItemsSource = _scans;

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
                try
                {
                    var jobs = await Task.Run(() => SchedulerDb.LoadJobs(date, line.Id));
                    if (ReferenceEquals(line, _line) && date == _date)
                    {
                        _jobs = jobs;
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

            PartNomor.Text = _current?.PartNumber ?? "-";
            JobNo.Text = _current == null ? "-" : _current.JobNo;
            Posisi.Text = _current == null ? "-" : _current.Posisi;

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

        // Scanner, serial and server events should all end up here.
        // NOTE: this only fills the box / pallet panel. The ACTUAL board shows what the database holds (sticker_scan).
        private void ProcessScan(string code)
        {
            if (_current == null) return;           // no schedule: ignore scans
            if (!_allScanned.Add(code)) return;     // duplicate (red flash and beep go here later)

            _scans.Add(code);
            lstScans.ScrollIntoView(code);
            _scansInBox++;

            if (_scansInBox >= ScanPerBox)          // box full
            {
                _scansInBox = 0;
                _boxCount++;

                if (_boxCount >= BoxesPerPallet)    // pallet full
                {
                    _palletCount++;
                    _boxCount = 0;
                    _scans.Clear();                 // fresh list for the next pallet
                    ShowPalletFull();
                }
            }

            UpdateScanCounters();
        }

        private void ShowPalletFull()
        {
            txtScanned.Text = BoxesPerPallet.ToString();                                   // shows 10
            txtScanned.Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)); // green

            _holdTimer.Stop();
            _holdTimer.Start();                     // restarts the one-second wait
        }

        private void UpdateScanCounters()
        {
            txtScanPerBox.Text = "SCAN per BOX: " + _scansInBox + " / " + ScanPerBox;
            txtPerBox.Text = BoxesPerPallet.ToString();
            txtPallet.Text = "Pallet: " + _palletCount;

            // while the "full" display is held, don't overwrite the 10
            if (!_holdTimer.IsEnabled)
            {
                txtScanned.Text = _boxCount.ToString();
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
            txtClock.Text = DateTime.Now.ToString(
                "dd MMMM yyyy HH:mm:ss", CultureInfo.InvariantCulture);
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
        private void btnKembali_Click(object sender, RoutedEventArgs e)
        {
            var home = new MainWindow();
            Application.Current.MainWindow = home;
            home.Show();
            Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            _clockTimer.Stop();
            _holdTimer.Stop();
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

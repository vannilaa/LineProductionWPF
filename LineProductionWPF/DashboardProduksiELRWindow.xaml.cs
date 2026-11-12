using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Controls.Primitives;

namespace LineProductionWPF
{
    public partial class DashboardProduksiELRWindow : Window
    {
        // ===== THE ONE LINE TO CHANGE FOR THE SERVER =====
        private readonly IScheduleSource _source = new FakeScheduleSource();
        // ==================================================

        private readonly List<ScheduleRow> _plan = new List<ScheduleRow>();   // plan from the source, not shown
        private readonly ObservableCollection<ScheduleRow> _hourly = new ObservableCollection<ScheduleRow>(); // the table
        private ScheduleRow _liveRow;                                         // this hour's line in the table
        private readonly ObservableCollection<string> _scans = new ObservableCollection<string>();
        private readonly HashSet<string> _allScanned = new HashSet<string>();   // duplicate check
        private string _selectedLine = "";
        private bool _pickerOpen;
        private readonly DispatcherTimer _clockTimer = new DispatcherTimer();
        private readonly DispatcherTimer _holdTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)   // how long 10 / 10 stays on screen
        };

        // current product: every value below comes from the active schedule row
        private ScheduleRow _active;
        private int RatePerHour => _active?.Target ?? 0;
        private int ScanPerBox => _active?.ScanPerBox ?? 1;
        private int BoxesPerPallet => _active?.BoxesPerPallet ?? 1;

        // running numbers
        private DateTime _segmentStart;
        private int _countBanked, _countJam, _countTotal;
        private int _actualTotal, _actualJam;
        private int _scansInBox, _boxCount, _palletCount;

        public DashboardProduksiELRWindow()
        {
            InitializeComponent();
            lstScans.ItemsSource = _scans;

            dgvSchedule.ItemsSource = _hourly;

            _holdTimer.Tick += (s, e) =>
            {
                _holdTimer.Stop();
                txtScanned.Foreground = new SolidColorBrush(Color.FromRgb(0x2F, 0x6F, 0xE4)); // back to blue
                UpdateScanCounters();                                                          // show the real 0
            };

            LoadSchedule();

            _clockTimer.Interval = TimeSpan.FromSeconds(1);
            _clockTimer.Tick += (s, e) => { UpdateClock(); RefreshCards(); };
            _clockTimer.Start();

            UpdateClock();
            RefreshCards();
        }

        // ---------- data in ----------
        private void LoadSchedule()
        {
            _plan.Clear();
            _plan.AddRange(_source.GetSchedule());
            if (_selectedLine == "")
                _selectedLine = _plan.Select(r => r.NomorLine).FirstOrDefault() ?? "";
            NomorLine.Text = _selectedLine;
        }

        private ScheduleRow FindActiveRow()
        {
            int hour = DateTime.Now.Hour;
            return _plan.FirstOrDefault(r => r.NomorLine == _selectedLine && r.Jam == hour);
        }

        private void ApplyActiveRow()
        {
            ScheduleRow row = FindActiveRow();
            if (row == _active) return;

            bool newJob = _active == null || row == null || _active.JobNo != row.JobNo;

            if (_active != null)
            {
                UpdateCount(DateTime.Now);   // recalculate at the exact moment of the change
                _countBanked += _countJam;
            }
            _active = row;
            _segmentStart = DateTime.Now;
            _countJam = 0;
            _actualJam = 0;

            if (newJob)
            {
                _scansInBox = 0;
                _scans.Clear();
            }

            PartNomor.Text = row?.PartNumber ?? "-";
            JobNo.Text = row?.JobNo ?? "-";

            // start this hour's line in the table
            if (row == null)
            {
                _liveRow = null;
            }
            else
            {
                _liveRow = new ScheduleRow
                {
                    Jadwal = row.Jadwal,
                    Jam = row.Jam,
                    PartNumber = row.PartNumber,
                    JobNo = row.JobNo,
                    Target = row.Target
                };
                _hourly.Add(_liveRow);
            }
        }
        private void UpdateHourlyRow()
        {
            if (_liveRow == null) return;

            int jumlahTarget = _countTotal;
            int actual = _actualJam;
            int jumlahActual = _actualTotal;
            int selisih = _actualTotal - _countTotal;

            // only touch the table when something changed
            if (_liveRow.JumlahTarget == jumlahTarget && _liveRow.Actual == actual
                && _liveRow.JumlahActual == jumlahActual && _liveRow.Selisih == selisih) return;

            _liveRow.JumlahTarget = jumlahTarget;
            _liveRow.Actual = actual;
            _liveRow.JumlahActual = jumlahActual;
            _liveRow.Selisih = selisih;

            dgvSchedule.Items.Refresh();   // the row class has no change notification yet
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
        private void ProcessScan(string code)
        {
            if (_active == null) return;            // no schedule this hour: ignore scans
            if (!_allScanned.Add(code)) return;     // duplicate (red flash and beep go here later)

            _scans.Add(code);
            lstScans.ScrollIntoView(code);
            _actualTotal++;
            _actualJam++;
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

            RefreshCards();
        }

        private void ShowPalletFull()
        {
            txtScanned.Text = BoxesPerPallet.ToString();                                   // shows 10
            txtScanned.Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)); // green

            _holdTimer.Stop();
            _holdTimer.Start();                     // restarts the one-second wait
        }

        // ---------- display ----------
        private void UpdateCount(DateTime now)
        {
            if (_active == null)
            {
                _countJam = 0;
            }
            else
            {
                int seconds = (int)(now - _segmentStart).TotalSeconds;
                _countJam = seconds * RatePerHour / 3600;   // +1 every (3600 / rate) seconds
            }
            _countTotal = _countBanked + _countJam;
        }

        private void RefreshCards()
        {
            ApplyActiveRow();
            UpdateCount(DateTime.Now);

            txtActual.Text = _actualTotal.ToString();
            txtActualJam.Text = _actualJam.ToString();
            txtCount.Text = _countTotal.ToString();
            txtCountJam.Text = _countJam.ToString();

            UpdateSelisih(_actualTotal - _countTotal, _actualJam - _countJam);
            UpdateScanCounters();
            UpdateHourlyRow();
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

        private void UpdateSelisih(int selisih, int selisihJam)
        {
            txtSelisih.Text = selisih.ToString();
            txtSelisihJam.Text = selisihJam.ToString();

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

        private static Color GetStatusColor(double ratio)
        {
            Color green = (Color)ColorConverter.ConvertFromString("#22C55E");
            Color yellow = (Color)ColorConverter.ConvertFromString("#FACC15");
            Color red = (Color)ColorConverter.ConvertFromString("#EF4444");

            if (ratio < 0.5)
            {
                return Blend(green, yellow, ratio / 0.5);
            }
            return Blend(yellow, red, (ratio - 0.5) / 0.5);
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
        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            txtScan.Focus();
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

         // ---------- ganti nomor line ----------

        private void bdNomorLine_Click(object sender, MouseButtonEventArgs e)
        {
            var menu = new ContextMenu
            {
                PlacementTarget = bdNomorLine,
                Placement = PlacementMode.Bottom
            };

            foreach (string line in _plan.Select(r => r.NomorLine).Distinct())
            {
                var item = new MenuItem
                {
                    Header = line,
                    FontSize = 24,
                    Padding = new Thickness(12, 8, 12, 8),
                    IsChecked = line == _selectedLine
                };
                item.Click += (s, args) => SelectLine(line);
                menu.Items.Add(item);
            }

            _pickerOpen = true;
            menu.Closed += (s, args) => { _pickerOpen = false; txtScan.Focus(); };
            menu.IsOpen = true;
        }

        private void SelectLine(string line)
        {
            if (line == _selectedLine) return;

            var answer = MessageBox.Show(
                "Ganti ke " + line + "?\nHitungan saat ini akan direset.",
                "Ganti Line", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;

            _selectedLine = line;
            NomorLine.Text = line;
            PartNomor.Text = "-";
            JobNo.Text = "-";

            // reset everything that belonged to the old line
            _holdTimer.Stop();
            txtScanned.Foreground = new SolidColorBrush(Color.FromRgb(0x2F, 0x6F, 0xE4));
            _active = null;
            _liveRow = null;
            _hourly.Clear();
            _scans.Clear();
            _allScanned.Clear();
            _countBanked = _countJam = _countTotal = 0;
            _actualTotal = _actualJam = 0;
            _scansInBox = _boxCount = _palletCount = 0;

            RefreshCards();
        }
    }
}
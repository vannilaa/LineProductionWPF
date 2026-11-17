using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace LineProductionWPF
{
    /// <summary>
    /// Shows the schedule made in DailyScheduler for today as kanban boards:
    /// target (planned), actual (scanned), count (pcs per hour) and selisih. Read-only.
    /// </summary>
    public partial class KanbanWindow : Window
    {
        private static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(5);   // how often the database is read

        private readonly DispatcherTimer _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        private readonly KanbanTracker _tracker = new KanbanTracker();

        private List<KanbanLine> _lines = new List<KanbanLine>();
        private KanbanLine? _line;
        private DateTime _date = DateTime.Today;
        private List<KanbanJob> _jobs = new List<KanbanJob>();
        private DateTime _lastRead = DateTime.MinValue;
        private bool _reading;

        public KanbanWindow()
        {
            InitializeComponent();
            _clockTimer.Tick += (s, e) => Tick();
            _clockTimer.Start();
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                _lines = await Task.Run(SchedulerDb.LoadLines);
                if (_lines.Count > 0) SelectLine(_lines[0]);
                else txtStatus.Text = "Belum ada line production di database.";
            }
            catch (Exception ex)
            {
                txtStatus.Text = "Gagal membaca database: " + ex.Message;
            }
        }

        private void SelectLine(KanbanLine line)
        {
            _line = line;
            _date = DateTime.Today;
            NomorLine.Text = line.Name;
            _jobs = new List<KanbanJob>();
            _tracker.Open(_date, line.Id);
            _lastRead = DateTime.MinValue;   // read right away
            Tick();
        }

        // every second: clock, count, and (every few seconds) a fresh read of the scan numbers
        private async void Tick()
        {
            txtClock.Text = DateTime.Now.ToString("dd MMMM yyyy HH:mm:ss", CultureInfo.InvariantCulture);
            if (_line == null) return;

            if (_date != DateTime.Today)   // midnight: a new day, a new schedule
            {
                _date = DateTime.Today;
                _jobs = new List<KanbanJob>();
                _tracker.Open(_date, _line.Id);
                _lastRead = DateTime.MinValue;
            }

            if (!_reading && DateTime.Now - _lastRead >= RefreshEvery)
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

        private void Render()
        {
            KanbanNumbers n = _tracker.Calculate(_jobs, DateTime.Now);
            var job = n.Current;

            PartNomor.Text = job?.PartNumber ?? "-";
            JobNo.Text = job == null ? "JOB NO" : "JOB NO: " + job.JobNo;
            Posisi.Text = job == null ? "POSISI" : "POSISI: " + job.Posisi;
            txtAllDone.Text = n.AllDone ? "SEMUA JOB SELESAI" : "";
            txtRate.Text = job == null ? "job ini" : "job ini  (" + job.Target + " pcs/jam)";

            txtTarget.Text = n.TargetJob.ToString();
            txtTargetHari.Text = "/" + n.TargetDay;
            txtActual.Text = n.ActualJob.ToString();
            txtActualHari.Text = "/" + n.ActualDay;
            txtCount.Text = n.CountJob.ToString();
            txtCountHari.Text = "/" + n.CountDay;

            ShowSelisih(n.SelisihJob, n.SelisihDay);

            // the table: only re-bind when the jobs list changed, otherwise just refresh the numbers
            if (!ReferenceEquals(dgJobs.ItemsSource, _jobs)) dgJobs.ItemsSource = _jobs;
            else dgJobs.Items.Refresh();
        }

        private void ShowSelisih(int selisih, int selisihHari)
        {
            txtSelisih.Text = selisih.ToString();
            txtSelisihHari.Text = selisihHari.ToString();

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
                color = Blend(amber, deepRed, Math.Min(1.0, -selisih / (double)tolerance));
            }

            bdSelisih.Background = new SolidColorBrush(color);
            double brightness = 0.299 * color.R + 0.587 * color.G + 0.114 * color.B;
            Brush text = brightness > 130 ? Brushes.Black : Brushes.White;
            txtSelisihTitle.Foreground = text;
            txtSelisih.Foreground = text;
            txtSelisihCaption.Foreground = text;
            txtSelisihCaption2.Foreground = text;
            txtSelisihHari.Foreground = text;
        }

        private static Color Blend(Color a, Color b, double t)
        {
            return Color.FromRgb(
                (byte)(a.R + (b.R - a.R) * t),
                (byte)(a.G + (b.G - a.G) * t),
                (byte)(a.B + (b.B - a.B) * t));
        }

        private void bdNomorLine_Click(object sender, MouseButtonEventArgs e)
        {
            var menu = new ContextMenu { PlacementTarget = bdNomorLine, Placement = PlacementMode.Bottom };
            foreach (var line in _lines)
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
            menu.IsOpen = true;
        }

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
            base.OnClosed(e);
        }
    }
}

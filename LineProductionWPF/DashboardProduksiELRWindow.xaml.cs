using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace LineProductionWPF
{
    /// <summary>
    /// Interaction logic for DashboardProduksiELRWindow.xaml
    /// </summary>
    public partial class DashboardProduksiELRWindow : Window
    {
        private readonly DispatcherTimer _clockTimer = new DispatcherTimer();
        public DashboardProduksiELRWindow()
        {
            InitializeComponent();
            UpdateSelisih(0, 999);

            UpdateClock();
            _clockTimer.Interval = TimeSpan.FromSeconds(1);
            _clockTimer.Tick += (s, e) => UpdateClock();
            _clockTimer.Start();
        }

        private void UpdateClock()
        {
            txtClock.Text = DateTime.Now.ToString(
                "dd MMMM yyyy HH:mm:ss", CultureInfo.InvariantCulture);
        }
        protected override void OnClosed(EventArgs e)
        {
            _clockTimer.Stop();
            base.OnClosed(e);
        }
        private void btnKembali_Click(object sender, RoutedEventArgs e)
        {
            var home = new MainWindow();
            Application.Current.MainWindow = home;
            home.Show();
            Close();
        }

        private void UpdateSelisih(int selisih, int selisihJam)
        {
            txtSelisih.Text = selisih.ToString();
            txtSelisihJam.Text = selisihJam.ToString();   // the small number, bottom right

            const int tolerance = 50;   // change this to what counts as "far from 0"
            double ratio = Math.Min(1.0, Math.Abs(selisih) / (double)tolerance);

            bdSelisih.Background = new SolidColorBrush(GetStatusColor(ratio));
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

    }
}

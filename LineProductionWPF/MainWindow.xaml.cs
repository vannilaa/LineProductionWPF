using System;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Globalization;
using System.Windows.Threading;


namespace LineProductionWPF
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly DispatcherTimer _clockTimer = new DispatcherTimer();
        public MainWindow()
        {
            InitializeComponent();

            UpdateClock();
            _clockTimer.Interval = TimeSpan.FromSeconds (1);
            _clockTimer.Tick += (s, e) => UpdateClock();
            _clockTimer.Start ();
        }
        private void UpdateClock()
        {
            txtClock.Text = DateTime.Now.ToString(
                "dd MMMM yyyy HH:mm:ss", CultureInfo.InvariantCulture);
        }
        private void btnKeluar_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }

        private void btnELRBaru_Click(object sender, RoutedEventArgs e)
        {
            var next = new DashboardProduksiELRWindow();
            Application.Current.MainWindow = next;
            next.Show();
            Close();
        }
        protected override void OnClosed(EventArgs e)
        {
            _clockTimer.Stop();
            base.OnClosed(e);
        }
    }
}
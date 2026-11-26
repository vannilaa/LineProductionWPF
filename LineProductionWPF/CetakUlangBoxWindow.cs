using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace LineProductionWPF
{
    /// <summary>
    /// Dialog for reprinting a completed box label ("Cetak Ulang"),
    /// adapted from DailyScheduler's ReprintDialog: requires QR/serial, masked authorization code (map_code),
    /// and a reprint reason (with a mandatory note when "Lainnya" is chosen).
    /// </summary>
    public sealed class CetakUlangBoxWindow : Window
    {
        private const string OtherReason = "Lainnya";

        private static readonly string[] Reasons =
        {
            "Stiker rusak / sobek",
            "Hasil cetak miring / bergeser",
            "Hasil cetak pudar / tidak terbaca",
            "Printer macet / berhenti di tengah",
            "Data stiker salah",
            "Stiker hilang",
            OtherReason
        };

        private readonly int _lineId;
        private readonly TextBox _txtQr;
        private readonly PasswordBox _txtAuth;
        private readonly ComboBox _reason = new ComboBox { FontSize = 14, Margin = new Thickness(0, 4, 0, 10) };
        private readonly TextBlock _noteLabel = new TextBlock { Text = "Catatan (wajib):", FontWeight = FontWeights.SemiBold, Visibility = Visibility.Collapsed };
        private readonly TextBox _note = new TextBox
        {
            FontSize = 14,
            Height = 64,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxLength = 500,
            Margin = new Thickness(0, 4, 0, 10),
            Visibility = Visibility.Collapsed
        };
        private readonly TextBlock _txtError;
        private readonly Button _btnPrint;

        /// <summary>Serial of the box whose label was printed (set when the dialog returns true).</summary>
        public string? PrintedSerial { get; private set; }

        public string Reason { get; private set; } = "";
        public string? Note { get; private set; }

        /// <summary>Set when the label printed but saving it to box_print_job failed (the dialog still returns true).</summary>
        public string? LogError { get; private set; }

        public CetakUlangBoxWindow(int lineId, string? defaultBoxSerial = null)
        {
            _lineId = lineId;

            Title = "Cetak Ulang Label Box";
            Width = 460;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Background = new SolidColorBrush(Color.FromRgb(0xF3, 0xF4, 0xF6));

            var root = new StackPanel { Margin = new Thickness(18) };

            root.Children.Add(new TextBlock
            {
                Text = "Cetak Ulang Label Box",
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 12)
            });

            // QR / Box Serial
            root.Children.Add(new TextBlock { Text = "QR / Serial Box", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
            _txtQr = new TextBox
            {
                Text = defaultBoxSerial ?? "",
                FontSize = 14,
                Padding = new Thickness(6, 4, 6, 4),
                Margin = new Thickness(0, 0, 0, 10)
            };
            root.Children.Add(_txtQr);

            // Kode Otorisasi (masked)
            root.Children.Add(new TextBlock { Text = "Kode Otorisasi", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
            _txtAuth = new PasswordBox
            {
                FontSize = 14,
                Padding = new Thickness(6, 4, 6, 4),
                Margin = new Thickness(0, 0, 0, 10)
            };
            root.Children.Add(_txtAuth);

            // Alasan cetak ulang (wajib) — same as DailyScheduler ReprintDialog
            root.Children.Add(new TextBlock { Text = "Alasan cetak ulang (wajib):", FontWeight = FontWeights.SemiBold });
            foreach (string r in Reasons) _reason.Items.Add(r);
            root.Children.Add(_reason);

            root.Children.Add(_noteLabel);
            root.Children.Add(_note);

            _txtError = new TextBlock
            {
                Foreground = Brushes.Firebrick,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                Visibility = Visibility.Collapsed,
                Margin = new Thickness(0, 0, 0, 10)
            };
            root.Children.Add(_txtError);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            var btnCancel = new Button
            {
                Content = "Batal",
                Width = 90,
                Height = 32,
                Margin = new Thickness(0, 0, 8, 0),
                IsCancel = true
            };
            _btnPrint = new Button
            {
                Content = "Cetak ulang",
                Width = 115,
                Height = 32,
                FontWeight = FontWeights.Bold,
                IsDefault = true,
                IsEnabled = false
            };
            _btnPrint.Click += BtnPrint_Click;

            buttons.Children.Add(btnCancel);
            buttons.Children.Add(_btnPrint);
            root.Children.Add(buttons);

            Content = root;

            _txtQr.TextChanged += (_, _) => UpdatePrintEnabled();
            _txtAuth.PasswordChanged += (_, _) => UpdatePrintEnabled();
            _reason.SelectionChanged += (_, _) => UpdatePrintEnabled();
            _note.TextChanged += (_, _) => UpdatePrintEnabled();

            Loaded += (_, _) =>
            {
                UpdatePrintEnabled();
                if (string.IsNullOrWhiteSpace(_txtQr.Text)) _txtQr.Focus();
                else _txtAuth.Focus();
            };
        }

        private void UpdatePrintEnabled()
        {
            string? reason = _reason.SelectedItem as string;
            bool isOther = reason == OtherReason;

            _noteLabel.Visibility = isOther ? Visibility.Visible : Visibility.Collapsed;
            _note.Visibility = isOther ? Visibility.Visible : Visibility.Collapsed;

            bool hasQr = !string.IsNullOrWhiteSpace(_txtQr.Text);
            bool hasAuth = !string.IsNullOrWhiteSpace(_txtAuth.Password);
            bool hasReason = reason != null && (!isOther || !string.IsNullOrWhiteSpace(_note.Text));

            _btnPrint.IsEnabled = hasQr && hasAuth && hasReason;
        }

        private void ShowError(string msg)
        {
            _txtError.Text = msg;
            _txtError.Visibility = Visibility.Visible;
        }

        private async void BtnPrint_Click(object sender, RoutedEventArgs e)
        {
            _txtError.Visibility = Visibility.Collapsed;

            string qr = _txtQr.Text.Trim();
            string auth = _txtAuth.Password.Trim();
            string? reason = _reason.SelectedItem as string;
            string? note = string.IsNullOrWhiteSpace(_note.Text) ? null : _note.Text.Trim();

            if (qr.Length == 0 || auth.Length == 0 || reason == null) return;

            string checker = AppSettings.Current.TodayChecker;
            if (checker.Length == 0)
            {
                ShowError("Nama Checker hari ini belum diisi. Isi dulu lewat tombol Pengaturan di dashboard.");
                return;
            }

            _btnPrint.IsEnabled = false;
            try
            {
                string mapCode = await Task.Run(() => SchedulerDb.GetLineMapCode(_lineId));
                if (string.IsNullOrEmpty(mapCode) || !string.Equals(auth, mapCode, StringComparison.OrdinalIgnoreCase))
                {
                    ShowError("Kode Otorisasi salah.");
                    _txtAuth.SelectAll();
                    _txtAuth.Focus();
                    return;
                }

                var box = await Task.Run(() => SchedulerDb.LoadBoxForReprint(qr, _lineId));
                if (box == null)
                {
                    ShowError("Box dengan serial \"" + qr + "\" tidak ditemukan di line ini.");
                    _txtQr.Focus();
                    return;
                }

                Reason = reason;
                Note = note;

                var printResult = BoxLabelPrinter.Print(
                    customer:    box.Customer,
                    model:       box.Model,
                    jobNo:       box.JobNo,
                    partNumber:  box.PartNumber,
                    productName: box.ProductName,
                    qtyPerBox:   box.QtyPerBox,
                    boxSerial:   box.BoxSerial,
                    lineName:    box.LineName,
                    prodDate:           box.ProdDate,   
                    qrModelPartNumber:  box.QrModelPartNumber,
                    checkerName: checker,
                    askOnPaperMismatch: true);

                if (printResult != BoxLabelPrinter.PrintResult.Printed)
                {
                    ShowError("Label tidak tercetak: " + BoxLabelPrinter.Describe(printResult) +
                              ". Periksa printer lalu coba lagi.");
                    return;
                }

                // Save the print. A box that never printed gets its FIRST print here (reason/note NULL, like print_job);
                // otherwise it is a reprint with the chosen reason and note.
                string printer = BoxLabelPrinter.LastPrinterName ?? "";
                string serial = box.BoxSerial;
                try { await Task.Run(() => SchedulerDb.RecordBoxPrint(serial, _lineId, checker, printer, reason, note)); }
                catch (Exception ex) { LogError = ex.Message; }

                PrintedSerial = box.BoxSerial;
                DialogResult = true;
            }
            catch (Exception ex)
            {
                ShowError("Gagal mencetak ulang: " + ex.Message);
            }
            finally
            {
                UpdatePrintEnabled();
            }
        }
    }
}

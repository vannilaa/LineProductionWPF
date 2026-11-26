using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace LineProductionWPF
{
    /// <summary>
    /// Dialog for reprinting a completed box label ("Cetak Ulang").
    /// Requires the box QR/serial, an authorization code (line's map_code, masked), and a remark.
    /// </summary>
    public sealed class CetakUlangBoxWindow : Window
    {
        private readonly int _lineId;
        private readonly TextBox _txtQr;
        private readonly PasswordBox _txtAuth;
        private readonly TextBox _txtRemark;
        private readonly TextBlock _txtError;
        private readonly Button _btnPrint;

        public CetakUlangBoxWindow(int lineId, string? defaultBoxSerial = null)
        {
            _lineId = lineId;

            Title = "Cetak Ulang Label Box";
            Width = 420;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
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

            // Remark (same intent as DailyScheduler Printwindow)
            root.Children.Add(new TextBlock { Text = "Remark", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
            _txtRemark = new TextBox
            {
                FontSize = 14,
                Padding = new Thickness(6, 4, 6, 4),
                Margin = new Thickness(0, 0, 0, 10)
            };
            root.Children.Add(_txtRemark);

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
                Content = "Cetak",
                Width = 100,
                Height = 32,
                FontWeight = FontWeights.Bold,
                IsDefault = true
            };
            _btnPrint.Click += BtnPrint_Click;

            buttons.Children.Add(btnCancel);
            buttons.Children.Add(_btnPrint);
            root.Children.Add(buttons);

            Content = root;
            Loaded += (s, e) =>
            {
                if (string.IsNullOrWhiteSpace(_txtQr.Text)) _txtQr.Focus();
                else _txtAuth.Focus();
            };
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
            string remark = _txtRemark.Text.Trim();

            if (qr.Length == 0)
            {
                ShowError("Isi QR / Serial Box terlebih dahulu.");
                _txtQr.Focus();
                return;
            }
            if (auth.Length == 0)
            {
                ShowError("Isi Kode Otorisasi terlebih dahulu.");
                _txtAuth.Focus();
                return;
            }
            if (remark.Length == 0)
            {
                ShowError("Isi Remark / alasan cetak ulang terlebih dahulu.");
                _txtRemark.Focus();
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

                var box = await Task.Run(() => SchedulerDb.LoadBoxForReprint(qr));
                if (box == null)
                {
                    ShowError("Box dengan serial \"" + qr + "\" tidak ditemukan.");
                    _txtQr.Focus();
                    return;
                }

                BoxLabelPrinter.Print(
                    customer:    box.Customer,
                    model:       box.Model,
                    jobNo:       box.JobNo,
                    partNumber:  box.PartNumber,
                    productName: box.ProductName,
                    qtyPerBox:   box.QtyPerBox,
                    boxSerial:   box.BoxSerial,
                    lineName:    box.LineName,
                    prodDate:    box.ProdDate);

                DialogResult = true;
            }
            catch (Exception ex)
            {
                ShowError("Gagal mencetak ulang: " + ex.Message);
            }
            finally
            {
                _btnPrint.IsEnabled = true;
            }
        }
    }
}


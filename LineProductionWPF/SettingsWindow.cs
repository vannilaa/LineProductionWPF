using System;
using System.IO.Ports;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace LineProductionWPF
{
    /// <summary>
    /// Pengaturan (gear button on the dashboard), saved per PC in AppSettings:
    ///  - Checker: name of today's checker, printed on every box label and saved in box_print_job / box.checker_name.
    ///  - Printer label box: which printer prints the box labels (empty = Windows default), with its status.
    ///  - ESP (COM): port, baud, line ending, connect automatically when the app starts. Used by the Log Serial / COM tab.
    /// </summary>
    public sealed class SettingsWindow : Window
    {
        private const string WindowsDefault = "(Default Windows)";

        private readonly SerialMonitorView _serial;
        private readonly TextBox _txtChecker = new TextBox { FontSize = 16, MaxLength = 100, Margin = new Thickness(0, 4, 0, 2) };
        private readonly ComboBox _cbPrinter = new ComboBox { FontSize = 14, MinWidth = 260 };
        private readonly TextBlock _txtPrinterStatus = new TextBlock { Margin = new Thickness(0, 4, 0, 0) };
        private readonly ComboBox _cbPort = new ComboBox { FontSize = 14, Width = 110 };
        private readonly ComboBox _cbBaud = new ComboBox { FontSize = 14, Width = 110, ItemsSource = SerialMonitorView.Bauds };
        private readonly ComboBox _cbEnding = new ComboBox { FontSize = 14, Width = 170, ItemsSource = SerialMonitorView.Endings };
        private readonly CheckBox _chkAuto = new CheckBox { Content = "Hubungkan otomatis saat aplikasi dibuka", Margin = new Thickness(0, 8, 0, 0) };
        private readonly TextBlock _txtEspStatus = new TextBlock { Margin = new Thickness(0, 4, 0, 0), Foreground = Brushes.DimGray };
        private readonly TextBlock _txtError = new TextBlock { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };

        public SettingsWindow(SerialMonitorView serial)
        {
            _serial = serial;
            AppSettings st = AppSettings.Current;

            Title = "Pengaturan";
            Width = 520;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var root = new StackPanel { Margin = new Thickness(16) };

            // ---- Checker ----
            root.Children.Add(Header("Checker"));
            root.Children.Add(new TextBlock { Text = "Nama checker shift ini (dicetak di label box):" });
            root.Children.Add(_txtChecker);
            root.Children.Add(Hint("Diminta lagi setiap hari baru. Ganti saat pergantian shift."));
            _txtChecker.Text = st.TodayChecker;

            // ---- Printer ----
            root.Children.Add(Header("Printer label box"));
            var printerRow = new DockPanel();
            var btnPrinterRefresh = new Button { Content = "Segarkan", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(6, 0, 0, 0) };
            DockPanel.SetDock(btnPrinterRefresh, Dock.Right);
            printerRow.Children.Add(btnPrinterRefresh);
            printerRow.Children.Add(_cbPrinter);
            root.Children.Add(printerRow);
            root.Children.Add(_txtPrinterStatus);
            root.Children.Add(Hint("Pilihan ini disimpan sebagai printer default untuk label box di PC ini (default Windows tidak diubah)."));
            btnPrinterRefresh.Click += (s, e) => LoadPrinters(SelectedPrinter());
            _cbPrinter.SelectionChanged += (s, e) => ShowPrinterStatus();
            LoadPrinters(st.PrinterName);

            // ---- ESP (COM) ----
            root.Children.Add(Header("ESP (COM)"));
            var espRow = new WrapPanel();
            espRow.Children.Add(Label("Port"));
            espRow.Children.Add(_cbPort);
            var btnPortRefresh = new Button { Content = "Segarkan", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(4, 0, 12, 0) };
            espRow.Children.Add(btnPortRefresh);
            espRow.Children.Add(Label("Baud"));
            espRow.Children.Add(_cbBaud);
            root.Children.Add(espRow);
            var endRow = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            endRow.Children.Add(Label("Akhiran baris"));
            endRow.Children.Add(_cbEnding);
            root.Children.Add(endRow);
            root.Children.Add(_chkAuto);
            root.Children.Add(_txtEspStatus);
            btnPortRefresh.Click += (s, e) => LoadPorts(_cbPort.SelectedItem as string);
            LoadPorts(st.EspPort);
            _cbBaud.SelectedItem = SerialMonitorView.Bauds.Contains(st.EspBaud) ? st.EspBaud : 9600;
            _cbEnding.SelectedIndex = st.EspLineEnding >= 0 && st.EspLineEnding < SerialMonitorView.Endings.Length ? st.EspLineEnding : 0;
            _chkAuto.IsChecked = st.EspAutoConnect;
            _txtEspStatus.Text = _serial.IsConnected ? "Status: terhubung (lihat tab Log Serial / COM)." : "Status: tidak terhubung.";

            // ---- buttons ----
            root.Children.Add(_txtError);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            var btnSave = new Button { Content = "Simpan", IsDefault = true, FontWeight = FontWeights.Bold, Padding = new Thickness(18, 4, 18, 4), Margin = new Thickness(0, 0, 8, 0) };
            var btnCancel = new Button { Content = "Batal", IsCancel = true, Padding = new Thickness(18, 4, 18, 4) };
            btnSave.Click += (s, e) => Save();
            buttons.Children.Add(btnSave);
            buttons.Children.Add(btnCancel);
            root.Children.Add(buttons);

            Content = root;
            Loaded += (s, e) => { _txtChecker.Focus(); _txtChecker.SelectAll(); };
        }

        // ---------------- printer ----------------
        private void LoadPrinters(string? keep)
        {
            var items = new System.Collections.Generic.List<string> { WindowsDefault };
            items.AddRange(BoxLabelPrinter.ListPrinters());
            // A saved printer that is gone stays in the list so the operator sees it is missing.
            if (!string.IsNullOrEmpty(keep) && !items.Contains(keep, StringComparer.OrdinalIgnoreCase)) items.Add(keep);
            _cbPrinter.ItemsSource = items;
            _cbPrinter.SelectedItem = string.IsNullOrEmpty(keep) ? WindowsDefault : items.First(i => string.Equals(i, keep, StringComparison.OrdinalIgnoreCase));
            ShowPrinterStatus();
        }

        /// <summary>Selected printer name; "" = Windows default.</summary>
        private string SelectedPrinter() =>
            _cbPrinter.SelectedItem is string name && name != WindowsDefault ? name : "";

        private void ShowPrinterStatus()
        {
            string name = SelectedPrinter();
            string status = BoxLabelPrinter.PrinterStatus(name);
            string which = name.Length == 0
                ? (BoxLabelPrinter.DefaultPrinterName() is { Length: > 0 } d ? "Default Windows = " + d + ". " : "Tidak ada printer default Windows. ")
                : "";
            _txtPrinterStatus.Text = which + "Status: " + status;
            _txtPrinterStatus.Foreground = status == "Siap" ? Brushes.ForestGreen : Brushes.Firebrick;
        }

        // ---------------- ESP port ----------------
        private void LoadPorts(string? keep)
        {
            var names = SerialPort.GetPortNames().OrderBy(n => n.Length).ThenBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            if (!string.IsNullOrEmpty(keep) && !names.Contains(keep, StringComparer.OrdinalIgnoreCase)) names.Add(keep);   // saved but unplugged
            _cbPort.ItemsSource = names;
            if (!string.IsNullOrEmpty(keep)) _cbPort.SelectedItem = names.First(n => string.Equals(n, keep, StringComparison.OrdinalIgnoreCase));
            else if (names.Count > 0) _cbPort.SelectedIndex = 0;
        }

        // ---------------- save ----------------
        private void Save()
        {
            string checker = (_txtChecker.Text ?? "").Trim();
            if (checker.Length == 0)
            {
                ShowError("Isi nama Checker.");
                _txtChecker.Focus();
                return;
            }

            AppSettings st = AppSettings.Current;
            st.CheckerName = checker;
            st.CheckerDate = DateTime.Today.ToString("yyyy-MM-dd");
            st.PrinterName = SelectedPrinter();
            st.EspPort = _cbPort.SelectedItem as string ?? "";
            st.EspBaud = _cbBaud.SelectedItem is int b ? b : 9600;
            st.EspLineEnding = Math.Max(0, _cbEnding.SelectedIndex);
            st.EspAutoConnect = _chkAuto.IsChecked == true;

            string? error = AppSettings.Save();
            if (error != null)
            {
                ShowError("Pengaturan dipakai sekarang, tapi gagal disimpan ke file: " + error);
                _serial.ApplySettings(st, connectNow: st.EspAutoConnect);
                return;
            }

            // Reconnects when the port/baud changed; connects now when auto-connect is on.
            _serial.ApplySettings(st, connectNow: st.EspAutoConnect);
            DialogResult = true;
        }

        private void ShowError(string message)
        {
            _txtError.Text = message;
            _txtError.Visibility = Visibility.Visible;
        }

        // ---------------- small helpers ----------------
        private static TextBlock Header(string text) => new TextBlock
        {
            Text = text, FontSize = 15, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 12, 0, 4)
        };

        private static TextBlock Hint(string text) => new TextBlock
        {
            Text = text, FontSize = 11, Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap
        };

        private static TextBlock Label(string text) => new TextBlock
        {
            Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0)
        };
    }
}

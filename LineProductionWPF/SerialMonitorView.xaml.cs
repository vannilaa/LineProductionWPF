using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace LineProductionWPF
{
    /// <summary>Serial monitor (like the Arduino IDE): choose COM port and baud, connect, read incoming text, send text.</summary>
    public partial class SerialMonitorView : UserControl
    {
        private static readonly int[] Bauds = { 300, 1200, 2400, 4800, 9600, 19200, 38400, 57600, 74880, 115200, 230400, 250000, 500000, 1000000 };
        private static readonly string[] Endings = { "Tanpa akhiran baris", "Newline", "Carriage return", "Both NL & CR" };
        private const int MaxChars = 200_000;   // the log keeps the last part only

        private SerialPort? _port;
        private bool _lineStart = true;         // next character starts a new line (for the timestamp)
        private bool _prevCr;                   // last character received was CR (so a following LF is not a second line break)

        // USB-serial adapters can be unplugged without any event: check now and then that the port still exists.
        private readonly DispatcherTimer _watch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };

        public SerialMonitorView()
        {
            InitializeComponent();
            cbBaud.ItemsSource = Bauds;
            cbBaud.SelectedItem = 9600;
            cbEnding.ItemsSource = Endings;
            cbEnding.SelectedIndex = 0;
            _watch.Tick += (s, e) => CheckPortStillThere();
            RefreshPorts();
            UpdateUi();
        }

        // ---------------- port list ----------------
        private void RefreshPorts()
        {
            string? keep = cbPort.SelectedItem as string;
            string[] names = SerialPort.GetPortNames().OrderBy(n => n.Length).ThenBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();
            cbPort.ItemsSource = names;
            if (keep != null && names.Contains(keep)) cbPort.SelectedItem = keep;
            else if (names.Length > 0) cbPort.SelectedIndex = 0;
        }

        private void cbPort_DropDownOpened(object? sender, EventArgs e)
        {
            if (_port == null) RefreshPorts();   // pick up a newly plugged adapter
        }

        private void btnRefresh_Click(object sender, RoutedEventArgs e) => RefreshPorts();

        // ---------------- connect / disconnect ----------------
        private void btnConnect_Click(object sender, RoutedEventArgs e)
        {
            if (_port != null) { Disconnect("Terputus."); return; }

            if (cbPort.SelectedItem is not string name || cbBaud.SelectedItem is not int baud)
            {
                txtState.Text = "Pilih port dan baud dulu.";
                return;
            }

            try
            {
                var port = new SerialPort(name, baud, Parity.None, 8, StopBits.One) { Encoding = Encoding.UTF8, WriteTimeout = 1000 };
                port.DataReceived += Port_DataReceived;
                port.Open();
                _port = port;
                _lineStart = true;
                _prevCr = false;
                _watch.Start();
                UpdateUi();
            }
            catch (UnauthorizedAccessException)
            {
                txtState.Text = name + " sedang dipakai program lain (tutup Arduino IDE / PuTTY dulu).";
            }
            catch (Exception ex)
            {
                txtState.Text = "Gagal membuka " + name + ": " + ex.Message;
            }
        }

        /// <summary>Closes the port. Also called when the window closes.</summary>
        public void Shutdown() => Disconnect(null);

        private void Disconnect(string? message)
        {
            _watch.Stop();
            SerialPort? p = _port;
            _port = null;
            if (p != null)
            {
                p.DataReceived -= Port_DataReceived;
                try { p.Close(); } catch { /* adapter already gone */ }
                try { p.Dispose(); } catch { }
            }
            UpdateUi();
            if (message != null) txtState.Text = message;
        }

        private void CheckPortStillThere()
        {
            if (_port == null) return;
            if (!SerialPort.GetPortNames().Contains(_port.PortName, StringComparer.OrdinalIgnoreCase))
            {
                string n = _port.PortName;
                Disconnect(n + " hilang (kabel dicabut?). Terputus.");
                RefreshPorts();
            }
        }

        private void UpdateUi()
        {
            bool on = _port != null;
            btnConnect.Content = on ? "Putuskan" : "Hubungkan";
            cbPort.IsEnabled = !on;
            cbBaud.IsEnabled = !on;
            btnRefresh.IsEnabled = !on;
            txtSend.IsEnabled = on;
            cbEnding.IsEnabled = on;
            btnSend.IsEnabled = on;
            txtState.Text = on ? "Terhubung: " + _port!.PortName + " @ " + _port.BaudRate + " baud" : "Tidak terhubung.";
        }

        // ---------------- receive ----------------
        private void Port_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            var port = (SerialPort)sender;
            string text;
            try { text = port.ReadExisting(); }
            catch { return; }   // closed or unplugged meanwhile
            if (text.Length == 0) return;
            Dispatcher.BeginInvoke(new Action(() => { if (ReferenceEquals(_port, port)) Append(text); }));
        }

        private void Append(string text)
        {
            var sb = new StringBuilder(text.Length + 16);
            foreach (char raw in text)
            {
                char ch = raw;
                if (ch == '\r') { ch = '\n'; _prevCr = true; }
                else if (ch == '\n') { if (_prevCr) { _prevCr = false; continue; } }   // LF of a CR+LF pair
                else _prevCr = false;

                if (ch != '\n' && ch != '\t' && char.IsControl(ch)) continue;   // hide other control characters

                if (_lineStart && chkTimestamp.IsChecked == true)
                    sb.Append(DateTime.Now.ToString("HH:mm:ss.fff -> "));
                sb.Append(ch);
                _lineStart = ch == '\n';
            }

            txtLog.AppendText(sb.ToString());
            if (txtLog.Text.Length > MaxChars)
                txtLog.Text = txtLog.Text.Substring(txtLog.Text.Length - MaxChars / 2);
            if (chkAutoscroll.IsChecked == true) txtLog.ScrollToEnd();
        }

        private void btnClear_Click(object sender, RoutedEventArgs e)
        {
            txtLog.Clear();
            _lineStart = true;
        }

        // ---------------- send ----------------
        private void txtSend_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { Send(); e.Handled = true; }
        }

        private void btnSend_Click(object sender, RoutedEventArgs e) => Send();

        private void Send()
        {
            if (_port == null || !_port.IsOpen) return;
            string ending = cbEnding.SelectedIndex switch { 1 => "\n", 2 => "\r", 3 => "\r\n", _ => "" };
            try
            {
                _port.Write(txtSend.Text + ending);
                txtSend.Clear();
            }
            catch (Exception ex)
            {
                Disconnect("Gagal mengirim: " + ex.Message);
            }
        }
    }
}

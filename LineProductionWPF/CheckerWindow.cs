using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace LineProductionWPF
{
    /// <summary>
    /// Asks for the Checker of a line. Shown when the app opens, when the line is changed and on a new day.
    /// Pre-filled with the name entered today for this line on this PC, so an unchanged Checker is just Enter.
    /// The name is saved locally (AppSettings, per line), so it never changes another PC / line.
    /// </summary>
    public sealed class CheckerWindow : Window
    {
        private readonly int _lineId;
        private readonly TextBox _txt = new TextBox { FontSize = 22, MaxLength = 100, Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 6, 0, 4) };
        private readonly TextBlock _error = new TextBlock { Foreground = Brushes.Firebrick, Visibility = Visibility.Collapsed };

        public CheckerWindow(KanbanLine line)
        {
            _lineId = line.Id;
            Title = "Checker - " + line.Name;
            Width = 440;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var root = new StackPanel { Margin = new Thickness(18) };
            root.Children.Add(new TextBlock { Text = line.Name, FontSize = 20, FontWeight = FontWeights.Bold });
            root.Children.Add(new TextBlock { Text = "Nama Checker (dicetak di label box):", Margin = new Thickness(0, 10, 0, 0) });
            root.Children.Add(_txt);
            root.Children.Add(new TextBlock
            {
                Text = "Tekan Enter bila nama masih sama. Bisa diganti kapan saja lewat Pengaturan.",
                FontSize = 11,
                Foreground = Brushes.DimGray,
                TextWrapping = TextWrapping.Wrap
            });
            root.Children.Add(_error);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            var ok = new Button { Content = "OK", IsDefault = true, FontWeight = FontWeights.Bold, Padding = new Thickness(24, 4, 24, 4), Margin = new Thickness(0, 0, 8, 0) };
            var skip = new Button { Content = "Nanti", IsCancel = true, Padding = new Thickness(18, 4, 18, 4) };
            ok.Click += (s, e) => Save();
            buttons.Children.Add(ok);
            buttons.Children.Add(skip);
            root.Children.Add(buttons);
            Content = root;

            _txt.Text = AppSettings.Current.CheckerFor(line.Id);
            Loaded += (s, e) => { _txt.Focus(); _txt.SelectAll(); Keyboard.Focus(_txt); };
        }

        private void Save()
        {
            string name = (_txt.Text ?? "").Trim();
            if (name.Length == 0)
            {
                _error.Text = "Isi nama Checker (atau tekan Nanti).";
                _error.Visibility = Visibility.Visible;
                _txt.Focus();
                return;
            }
            AppSettings.Current.SetChecker(_lineId, name);
            AppSettings.Save();
            DialogResult = true;
        }
    }
}

using System;
using System.Globalization;
using System.IO;
using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QRCoder;

namespace LineProductionWPF
{
    /// <summary>
    /// Prints a 6 × 9 cm box label to the system default printer whenever a box is completed.
    /// </summary>
    public static class BoxLabelPrinter
    {
        // Label physical size: 9 cm wide × 6 cm tall (landscape).
        // 1 cm ≈ 37.795 WPF device-independent pixels (96 dpi).
        private const double CmToPx = 96.0 / 2.54;
        private const double LabelWidth  = 9.0 * CmToPx;   // ≈ 340 px
        private const double LabelHeight = 6.0 * CmToPx;   // ≈ 227 px

        private const string Manufacturer = "PT. SUNGWOO INDONESIA";

        /// <summary>
        /// Prints a label for the box that was just completed.
        /// Errors are silently swallowed so a print failure never breaks scanning.
        /// </summary>
        public static void Print(
            string customer,
            string model,
            string jobNo,
            string partNumber,
            string productName,
            int qtyPerBox,
            string boxSerial,
            string lineName,
            DateTime prodDate)
        {
            try
            {
                var queue = GetDefaultPrintQueue();
                if (queue == null) return;

                var dialog = new PrintDialog { PrintQueue = queue };

                var ticket = queue.DefaultPrintTicket.Clone();
                ticket.PageMediaSize = new PageMediaSize(PageMediaSizeName.Unknown, LabelWidth, LabelHeight);
                ticket.PageOrientation = PageOrientation.Landscape;
                dialog.PrintTicket = ticket;

                var visual = BuildLabel(
                    customer, model, jobNo, partNumber, productName,
                    qtyPerBox, boxSerial, lineName, prodDate);

                dialog.PrintVisual(visual, "Box Label - " + (boxSerial.Length > 0 ? boxSerial : partNumber));
            }
            catch
            {
                // Print failures must not interrupt production scanning.
            }
        }

        private static FrameworkElement BuildLabel(
            string customer, string model, string jobNo,
            string partNumber, string productName,
            int qtyPerBox, string boxSerial, string lineName, DateTime prodDate)
        {
            var borderBrush = Brushes.Black;
            const double lineThickness = 1.5;

            var root = new Border
            {
                Width      = LabelWidth,
                Height     = LabelHeight,
                Background = Brushes.White,
                Padding    = new Thickness(8, 6, 8, 6) // safe print margin so borders are never cut off
            };

            var frame = new Border
            {
                Background      = Brushes.White,
                BorderBrush     = borderBrush,
                BorderThickness = new Thickness(lineThickness)
            };

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // 0: Customer
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // 1: Model/JobNo | PartNumber/ProductName
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 2: QTY/Date | QR | Line/Lot#
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // 3: Footer

            // ── Row 0: Customer ──────────────────────────────────────────────────
            var customerBorder = new Border
            {
                BorderBrush     = borderBrush,
                BorderThickness = new Thickness(0, 0, 0, lineThickness),
                Padding         = new Thickness(4, 2, 4, 2)
            };
            customerBorder.Child = new TextBlock
            {
                Text          = string.IsNullOrWhiteSpace(customer) ? "PT. TOYOTA MOTOR MANUFACTURING INDONESIA" : customer,
                FontSize      = 12,
                FontWeight    = FontWeights.Bold,
                Foreground    = Brushes.Black,
                TextAlignment = TextAlignment.Center,
                TextTrimming  = TextTrimming.CharacterEllipsis
            };
            Grid.SetRow(customerBorder, 0);
            grid.Children.Add(customerBorder);

            // ── Row 1: Model + Job No | Part Number + Product Name ───────────────
            var row1 = new Grid();
            row1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.36, GridUnitType.Star) });
            row1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.64, GridUnitType.Star) });

            var leftTopBorder = new Border
            {
                BorderBrush     = borderBrush,
                BorderThickness = new Thickness(0, 0, lineThickness, lineThickness),
                Padding         = new Thickness(4, 2, 4, 2)
            };
            var leftTopStack = new StackPanel
            {
                VerticalAlignment   = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            leftTopStack.Children.Add(new TextBlock
            {
                Text          = string.IsNullOrWhiteSpace(model) ? "-" : model,
                FontSize      = 17,
                FontWeight    = FontWeights.Bold,
                Foreground    = Brushes.Black,
                TextAlignment = TextAlignment.Center,
                TextWrapping  = TextWrapping.Wrap
            });
            if (!string.IsNullOrWhiteSpace(jobNo) && jobNo != "-")
            {
                leftTopStack.Children.Add(new TextBlock
                {
                    Text          = jobNo,
                    FontSize      = 21,
                    FontWeight    = FontWeights.Bold,
                    Foreground    = Brushes.Black,
                    TextAlignment = TextAlignment.Center,
                    TextWrapping  = TextWrapping.Wrap,
                    Margin        = new Thickness(0, 1, 0, 0)
                });
            }
            leftTopBorder.Child = leftTopStack;
            Grid.SetColumn(leftTopBorder, 0);
            row1.Children.Add(leftTopBorder);

            var rightTopBorder = new Border
            {
                BorderBrush     = borderBrush,
                BorderThickness = new Thickness(0, 0, 0, lineThickness),
                Padding         = new Thickness(4, 2, 4, 2)
            };
            var rightTopStack = new StackPanel
            {
                VerticalAlignment   = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            rightTopStack.Children.Add(new TextBlock
            {
                Text          = partNumber,
                FontSize      = 17,
                FontWeight    = FontWeights.Bold,
                Foreground    = Brushes.Black,
                TextAlignment = TextAlignment.Center,
                TextWrapping  = TextWrapping.Wrap
            });
            if (!string.IsNullOrWhiteSpace(productName))
            {
                rightTopStack.Children.Add(new TextBlock
                {
                    Text          = productName,
                    FontSize      = 10,
                    FontWeight    = FontWeights.Bold,
                    Foreground    = Brushes.Black,
                    TextAlignment = TextAlignment.Center,
                    TextWrapping  = TextWrapping.Wrap,
                    Margin        = new Thickness(0, 2, 0, 0)
                });
            }
            rightTopBorder.Child = rightTopStack;
            Grid.SetColumn(rightTopBorder, 1);
            row1.Children.Add(rightTopBorder);

            Grid.SetRow(row1, 1);
            grid.Children.Add(row1);

            // ── Row 2: [QTY + Prod Date] | [QR] | [Line + Lot # + Blank] ─────────
            var row2 = new Grid();
            row2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.28, GridUnitType.Star) });
            row2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.32, GridUnitType.Star) });
            row2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.40, GridUnitType.Star) });

            // Col 0: QTY (top) + Prod Date (bottom)
            var col0Grid = new Grid();
            col0Grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0.64, GridUnitType.Star) });
            col0Grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0.36, GridUnitType.Star) });

            var qtyBorder = new Border
            {
                BorderBrush     = borderBrush,
                BorderThickness = new Thickness(0, 0, lineThickness, lineThickness),
                Padding         = new Thickness(3, 2, 3, 2)
            };
            var qtyGrid = new Grid();
            qtyGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            qtyGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var qtyLbl = new TextBlock { Text = "QTY", FontSize = 8, FontWeight = FontWeights.Bold };
            Grid.SetRow(qtyLbl, 0);
            qtyGrid.Children.Add(qtyLbl);
            var qtyVal = new TextBlock
            {
                Text                = qtyPerBox.ToString(CultureInfo.InvariantCulture),
                FontSize            = 32,
                FontWeight          = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center
            };
            Grid.SetRow(qtyVal, 1);
            qtyGrid.Children.Add(qtyVal);
            qtyBorder.Child = qtyGrid;
            Grid.SetRow(qtyBorder, 0);
            col0Grid.Children.Add(qtyBorder);

            var dateBorder = new Border
            {
                BorderBrush     = borderBrush,
                BorderThickness = new Thickness(0, 0, lineThickness, 0),
                Padding         = new Thickness(3, 2, 3, 2)
            };
            var dateGrid = new Grid();
            dateGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            dateGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var dateLbl = new TextBlock { Text = "Prod Date", FontSize = 7.5, FontWeight = FontWeights.Bold };
            Grid.SetRow(dateLbl, 0);
            dateGrid.Children.Add(dateLbl);
            var dateVal = new TextBlock
            {
                Text                = prodDate.ToString("dd-MMM-yyyy", CultureInfo.InvariantCulture),
                FontSize            = 14,
                FontWeight          = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center
            };
            Grid.SetRow(dateVal, 1);
            dateGrid.Children.Add(dateVal);
            dateBorder.Child = dateGrid;
            Grid.SetRow(dateBorder, 1);
            col0Grid.Children.Add(dateBorder);

            Grid.SetColumn(col0Grid, 0);
            row2.Children.Add(col0Grid);

            // Col 1: QR Code
            var qrBorder = new Border
            {
                BorderBrush     = borderBrush,
                BorderThickness = new Thickness(0, 0, lineThickness, 0),
                Padding         = new Thickness(2)
            };
            string qrPayload = boxSerial.Length > 0 ? boxSerial : partNumber;
            qrBorder.Child = new Image
            {
                Source              = CreateQrImage(qrPayload),
                Stretch             = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center
            };
            Grid.SetColumn(qrBorder, 1);
            row2.Children.Add(qrBorder);

            // Col 2: Line name (top) | [Lot # (left) | Blank (right)] (bottom)
            var col2Grid = new Grid();
            col2Grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            col2Grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var lineBorder = new Border
            {
                BorderBrush     = borderBrush,
                BorderThickness = new Thickness(0, 0, 0, lineThickness),
                Padding         = new Thickness(3, 1, 3, 1)
            };
            lineBorder.Child = new TextBlock
            {
                Text          = string.IsNullOrWhiteSpace(lineName) ? "-" : lineName,
                FontSize      = 14,
                FontWeight    = FontWeights.Bold,
                TextAlignment = TextAlignment.Center,
                TextTrimming  = TextTrimming.CharacterEllipsis
            };
            Grid.SetRow(lineBorder, 0);
            col2Grid.Children.Add(lineBorder);

            var lotRowGrid = new Grid();
            lotRowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.46, GridUnitType.Star) });
            lotRowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.54, GridUnitType.Star) });

            var lotBorder = new Border
            {
                BorderBrush     = borderBrush,
                BorderThickness = new Thickness(0, 0, lineThickness, 0),
                Padding         = new Thickness(3, 2, 3, 2)
            };
            var lotStack = new StackPanel();
            lotStack.Children.Add(new TextBlock { Text = "Lot #", FontSize = 9, FontWeight = FontWeights.Bold });
            lotStack.Children.Add(new TextBlock
            {
                Text         = boxSerial.Length > 0 ? boxSerial : "-",
                FontSize     = 14,
                FontWeight   = FontWeights.Bold,
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 2, 0, 0)
            });
            lotBorder.Child = lotStack;
            Grid.SetColumn(lotBorder, 0);
            lotRowGrid.Children.Add(lotBorder);

            // Right blank cell (for stamp / future use)
            var blankCell = new Border();
            Grid.SetColumn(blankCell, 1);
            lotRowGrid.Children.Add(blankCell);

            Grid.SetRow(lotRowGrid, 1);
            col2Grid.Children.Add(lotRowGrid);

            Grid.SetColumn(col2Grid, 2);
            row2.Children.Add(col2Grid);

            Grid.SetRow(row2, 2);
            grid.Children.Add(row2);

            // ── Row 3: Footer – PT. SUNGWOO INDONESIA | Checker: ─────────────────
            var footerBorder = new Border
            {
                BorderBrush     = borderBrush,
                BorderThickness = new Thickness(0, lineThickness, 0, 0)
            };
            var footerGrid = new Grid();
            // Align the divider with the Lot # / Blank divider in Row 2:
            // 0.28 + 0.32 + (0.40 * 0.46) = 0.784* left, 0.40 * 0.54 = 0.216* right.
            footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.784, GridUnitType.Star) });
            footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.216, GridUnitType.Star) });

            var mfgBorder = new Border
            {
                BorderBrush     = borderBrush,
                BorderThickness = new Thickness(0, 0, lineThickness, 0),
                Padding         = new Thickness(6, 2, 6, 2)
            };
            mfgBorder.Child = new TextBlock
            {
                Text                = Manufacturer,
                FontSize            = 12,
                FontWeight          = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center
            };
            Grid.SetColumn(mfgBorder, 0);
            footerGrid.Children.Add(mfgBorder);

            var checkerBorder = new Border
            {
                Padding = new Thickness(6, 2, 6, 2)
            };
            checkerBorder.Child = new TextBlock
            {
                Text                = "",
                FontSize            = 12,
                FontWeight          = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment   = VerticalAlignment.Center
            };
            Grid.SetColumn(checkerBorder, 1);
            footerGrid.Children.Add(checkerBorder);

            footerBorder.Child = footerGrid;
            Grid.SetRow(footerBorder, 3);
            grid.Children.Add(footerBorder);

            frame.Child = grid;
            root.Child = frame;

            root.Measure(new Size(LabelWidth, LabelHeight));
            root.Arrange(new Rect(0, 0, LabelWidth, LabelHeight));
            root.UpdateLayout();

            return root;
        }

        private static BitmapImage CreateQrImage(string content)
        {
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(content, QRCodeGenerator.ECCLevel.M);
            var qr = new PngByteQRCode(data);
            byte[] bytes = qr.GetGraphic(8, drawQuietZones: false);

            var image = new BitmapImage();
            using (var ms = new MemoryStream(bytes))
            {
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = ms;
                image.EndInit();
                image.Freeze();
            }
            return image;
        }

        private static PrintQueue? GetDefaultPrintQueue()
        {
            try
            {
                using var server = new LocalPrintServer();
                return server.DefaultPrintQueue;
            }
            catch
            {
                return null;
            }
        }
    }
}

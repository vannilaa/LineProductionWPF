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

        // Manual fine-tune (mm) applied after the driver offset correction.
        // Positive Y moves the print DOWN, negative moves it UP. Leave 0 unless still off.
        private const double NudgeXMm = 0.0;
        private const double NudgeYMm = 0.0;

        private const string Manufacturer = "PT. SUNGWOO INDONESIA";

        /// <summary>Outcome of a print attempt.</summary>
        public enum PrintResult
        {
            Printed,
            /// <summary>No default printer, or it is offline / paused / in error.</summary>
            PrinterNotReady,
            /// <summary>The driver's paper size is not 90 x 60 mm, so the label was not printed.</summary>
            PaperMismatch,
            /// <summary>Printing threw an error.</summary>
            Failed
        }

        /// <summary>Short Indonesian text for a non-printed result, for the status bar / grid.</summary>
        public static string Describe(PrintResult r) => r switch
        {
            PrintResult.Printed         => "Tercetak",
            PrintResult.PrinterNotReady => "Printer tidak siap",
            PrintResult.PaperMismatch   => "Ukuran kertas salah",
            _                           => "Gagal cetak"
        };

        /// <summary>
        /// Prints a box label. Never throws, so a print failure never breaks scanning.
        /// With <paramref name="askOnPaperMismatch"/> false (auto-print) a wrong paper size is skipped
        /// without any popup; with true (Cetak Ulang) the operator is asked whether to print anyway.
        /// </summary>
        public static PrintResult Print(
            string customer,
            string model,
            string jobNo,
            string partNumber,
            string productName,
            int qtyPerBox,
            string boxSerial,
            string lineName,
            DateTime prodDate,
            bool askOnPaperMismatch = false,
            bool qrModelPartNumber = false)
        {
            try
            {
                var queue = GetDefaultPrintQueue();
                if (queue == null || !IsReady(queue)) return PrintResult.PrinterNotReady;

                var dialog = new PrintDialog { PrintQueue = queue };

                var expected = new Size(LabelWidth, LabelHeight);
                PrintTicket? ticket = PrepareTicket(queue, expected, out Size pageSize, out bool sizeChanged);
                if (ticket != null)
                    dialog.PrintTicket = ticket;

                if (sizeChanged)
                {
                    if (!askOnPaperMismatch) return PrintResult.PaperMismatch;

                    string msg = string.Format(CultureInfo.CurrentCulture,
                        "Driver printer memakai ukuran kertas {0:0.#} x {1:0.#} mm, bukan {2:0.#} x {3:0.#} mm.\n\n" +
                        "Atur ukuran kertas di Printer Preferences menjadi {2:0.#} x {3:0.#} mm supaya hasil tidak terpotong.\n\nTetap cetak?",
                        pageSize.Width * 25.4 / 96.0, pageSize.Height * 25.4 / 96.0,
                        expected.Width * 25.4 / 96.0, expected.Height * 25.4 / 96.0);
                    if (MessageBox.Show(msg, "Ukuran kertas berbeda", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                        return PrintResult.PaperMismatch;
                }

                // Read the printer's actual printable area before building/scaling.
                double targetW = dialog.PrintableAreaWidth  > 0 ? dialog.PrintableAreaWidth  : LabelWidth;
                double targetH = dialog.PrintableAreaHeight > 0 ? dialog.PrintableAreaHeight : LabelHeight;

                var visual = BuildLabel(
                    customer, model, jobNo, partNumber, productName,
                    qtyPerBox, boxSerial, lineName, prodDate, qrModelPartNumber);

                // If the printer's printable area is portrait (taller than wide),
                // rotate the 90x60mm landscape label 90° so it fills the page without top/bottom whitespace.
                if (targetW < targetH)
                    visual.LayoutTransform = new RotateTransform(90);

                // Wrap in a Viewbox sized to the printer's printable area so it fills the label cleanly.
                var container = new Viewbox
                {
                    Width   = targetW,
                    Height  = targetH,
                    Stretch = Stretch.Uniform,
                    Child   = visual
                };
                container.Measure(new Size(targetW, targetH));
                container.Arrange(new Rect(0, 0, targetW, targetH));
                container.UpdateLayout();

                // Rasterize at 203 DPI (thermal label printer native DPI) so the driver
                // receives a crisp 1:1 bitmap with no XPS vector issues.
                const double dpi = 203.0;
                int pxWidth  = (int)Math.Ceiling(targetW * dpi / 96.0);
                int pxHeight = (int)Math.Ceiling(targetH * dpi / 96.0);
                var rtb = new RenderTargetBitmap(pxWidth, pxHeight, dpi, dpi, PixelFormats.Pbgra32);
                rtb.Render(container);
                rtb.Freeze();

                var printImage = new Image
                {
                    Source  = rtb,
                    Width   = targetW,
                    Height  = targetH,
                    Stretch = Stretch.Fill
                };
                RenderOptions.SetBitmapScalingMode(printImage, BitmapScalingMode.NearestNeighbor);
                // PrintVisual draws starting at the driver's imageable-area origin, not the
                // physical page corner. Our image is already sized to the full label, so a
                // non-zero driver top/left margin pushes it down/right and cuts off the bottom.
                // Shift it back by that origin so the label lands exactly on the sticker.
                double offsetX = 0, offsetY = 0;
                try
                {
                    var area = queue.GetPrintCapabilities(dialog.PrintTicket).PageImageableArea;
                    if (area != null)
                    {
                        offsetX = area.OriginWidth;
                        offsetY = area.OriginHeight;
                    }
                }
                catch { /* driver doesn't report capabilities; assume no offset */ }

                printImage.Measure(new Size(targetW, targetH));
                printImage.Arrange(new Rect(0, 0, targetW, targetH));
                printImage.RenderTransform = new TranslateTransform(
                    -offsetX + NudgeXMm * CmToPx / 10.0,
                    -offsetY + NudgeYMm * CmToPx / 10.0);
                printImage.UpdateLayout();

                dialog.PrintVisual(printImage, "Box Label - " + (boxSerial.Length > 0 ? boxSerial : partNumber));
                return PrintResult.Printed;
            }
            catch
            {
                // Print failures must not interrupt production scanning; the caller shows the result.
                return PrintResult.Failed;
            }
        }

        private static bool IsReady(PrintQueue queue)
        {
            try
            {
                queue.Refresh();
                return !(queue.IsOffline || queue.IsPaused || queue.IsInError ||
                         queue.IsNotAvailable || queue.IsPaperJammed || queue.IsOutOfPaper ||
                         queue.IsDoorOpened || queue.NeedUserIntervention);
            }
            catch
            {
                return true;   // status unknown: try printing and let PrintVisual report a failure
            }
        }

        private static FrameworkElement BuildLabel(
            string customer, string model, string jobNo,
            string partNumber, string productName,
            int qtyPerBox, string boxSerial, string lineName, DateTime prodDate,
            bool qrModelPartNumber = false)
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

            // ── Row 1: Model + Job No | Part Number + Product Name [| QR] ───────
            var row1 = new Grid();
            if (qrModelPartNumber)
            {
                row1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.34, GridUnitType.Star) });
                row1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.66, GridUnitType.Star) });
                row1.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            }
            else
            {
                row1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.36, GridUnitType.Star) });
                row1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.64, GridUnitType.Star) });
            }

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
                BorderThickness = new Thickness(0, 0, qrModelPartNumber ? lineThickness : 0, lineThickness),
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

            if (qrModelPartNumber)
            {
                var qrTopBorder = new Border
                {
                    BorderBrush     = borderBrush,
                    BorderThickness = new Thickness(0, 0, 0, lineThickness),
                    Padding         = new Thickness(2)
                };
                var qrElement = MakeQrBitmap($"{model.Trim()};{partNumber.Trim()}", margin: 0, QRCodeGenerator.ECCLevel.M);
                qrElement.Width  = 48;
                qrElement.Height = 48;
                qrTopBorder.Child = qrElement;
                Grid.SetColumn(qrTopBorder, 2);
                row1.Children.Add(qrTopBorder);
            }

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
            var qtyLbl = new TextBlock { Text = "QTY", FontSize = 10, FontWeight = FontWeights.Bold };
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
            var dateLbl = new TextBlock { Text = "Prod Date", FontSize = 10, FontWeight = FontWeights.Bold };
            Grid.SetRow(dateLbl, 0);
            dateGrid.Children.Add(dateLbl);
            var dateVal = new Viewbox
            {
                Stretch             = Stretch.Uniform,
                StretchDirection    = StretchDirection.DownOnly,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text       = prodDate.ToString("dd-MMM-yyyy", CultureInfo.InvariantCulture),
                    FontSize   = 13,
                    FontWeight = FontWeights.Bold
                }
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
            qrBorder.Child = MakeQrBitmap(qrPayload, margin: 0, QRCodeGenerator.ECCLevel.M);
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
            lotStack.Children.Add(new TextBlock { Text = "Lot #", FontSize = 10, FontWeight = FontWeights.Bold });
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

        private static readonly QRCodeGenerator _qrGenerator = new QRCodeGenerator();

        /// <summary>
        /// QR code generated as an exact 1-bit/grayscale bitmap displayed with NearestNeighbor scaling,
        /// ensuring every module is a razor-sharp square with zero anti-aliasing blur.
        /// </summary>
        private static FrameworkElement MakeQrBitmap(string text, double margin, QRCodeGenerator.ECCLevel level = QRCodeGenerator.ECCLevel.M)
        {
            var data = _qrGenerator.CreateQrCode(text, level);
            var m = data.ModuleMatrix;
            int total = m.Count;

            int minX = total, minY = total, maxX = -1, maxY = -1;
            for (int y = 0; y < total; y++)
                for (int x = 0; x < total; x++)
                    if (m[y][x])
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }

            if (maxX < 0) return new Border();

            int size = Math.Max(maxX - minX + 1, maxY - minY + 1);
            var raw = new byte[size * size];
            for (int y = 0; y < size; y++)
            {
                int srcY = y + minY;
                for (int x = 0; x < size; x++)
                {
                    int srcX = x + minX;
                    bool dark = srcY < total && srcX < total && m[srcY][srcX];
                    raw[y * size + x] = dark ? (byte)0 : (byte)255;
                }
            }

            var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Gray8, null, raw, size);
            bitmap.Freeze();

            var img = new Image
            {
                Source = bitmap,
                Stretch = Stretch.Uniform,
                Margin = new Thickness(margin),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center
            };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
            RenderOptions.SetEdgeMode(img, EdgeMode.Aliased);
            return img;
        }

        private static PrintTicket? PrepareTicket(PrintQueue queue, Size expected, out Size pageSize, out bool sizeChanged)
        {
            pageSize = expected;
            sizeChanged = false;
            try
            {
                PrintTicket current = queue.UserPrintTicket ?? queue.DefaultPrintTicket;
                PrintTicket wanted = current.Clone();
                wanted.PageMediaSize = new PageMediaSize(expected.Width, expected.Height);

                PrintTicket validated = queue.MergeAndValidatePrintTicket(current, wanted).ValidatedPrintTicket;

                PageMediaSize? got = validated.PageMediaSize;
                if (got != null && got.Width.HasValue && got.Height.HasValue)
                {
                    pageSize = new Size(got.Width.Value, got.Height.Value);
                    double tolerance = 1.5 * CmToPx / 10.0; // 1.5 mm
                    sizeChanged = Math.Abs(pageSize.Width - expected.Width) > tolerance ||
                                  Math.Abs(pageSize.Height - expected.Height) > tolerance;
                }
                return validated;
            }
            catch
            {
                return null;
            }
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

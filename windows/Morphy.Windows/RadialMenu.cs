using System;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Controls;
using System.Windows.Automation;

namespace Morphy;

internal sealed class RadialMenu : Window
{
    private readonly Ring ring;
    internal Action<OutputFormat>? Pick;
    internal Action? Dismiss;
    private readonly Native.Point origin;
    private bool moved;
    private double scale;
    internal RadialMenu(string path, OutputFormat[] outputs)
    {
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ShowInTaskbar = false; ShowActivated = false; Topmost = true; ResizeMode = ResizeMode.NoResize;
        Width = Height = 300;
        ring = new Ring(path, outputs); Content = ring;
        ring.Invoked = slot => { ring.Selected = slot; moved = true; ExecuteSelection(); };
        ring.EngineMissing = Conversion.OfficeRoutes.ContainsKey(Conversion.Extension(path)) && EngineInstaller.Find() == null;
        Native.GetCursorPos(out origin);
        SourceInitialized += (_, _) =>
        {
            nint handle = new WindowInteropHelper(this).Handle;
            Native.SetWindowLongPtr(handle, -20, Native.GetWindowLongPtr(handle, -20) | 0x08000000 | 0x80);
        };
        Loaded += (_, _) =>
        {
            var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(origin.X, origin.Y));
            scale = Native.ScaleAt(origin);
            int size = (int)(300 * scale);
            int x = Math.Clamp(origin.X - size / 2, screen.WorkingArea.Left, Math.Max(screen.WorkingArea.Left, screen.WorkingArea.Right - size));
            int y = Math.Clamp(origin.Y - size / 2, screen.WorkingArea.Top, Math.Max(screen.WorkingArea.Top, screen.WorkingArea.Bottom - size));
            Native.SetWindowPos(new WindowInteropHelper(this).Handle, new nint(-1), x, y, size, size, 0x0010);
        };
    }
    internal static int Hit(double x, double y)
    {
        double radius = Math.Sqrt(x * x + y * y);
        if (radius < 58 || radius > 135) return -1;
        double angle = (Math.Atan2(x, -y) * 180 / Math.PI + 390) % 360;
        if (angle % 60 < 2 || angle % 60 > 58) return -1;
        return (int)(angle / 60);
    }
    internal static void RenderPreview(string destination, bool? light = null, bool more = false)
    {
        if (!destination.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            System.IO.Directory.CreateDirectory(destination);
            RenderPreview(System.IO.Path.Combine(destination, "menu-light.png"), true);
            RenderPreview(System.IO.Path.Combine(destination, "menu-dark.png"), false);
            RenderPreview(System.IO.Path.Combine(destination, "menu-more.png"), false, true); return;
        }
        var preview = new Ring("샘플 이미지.png", Conversion.Outputs("sample.png"), light) { Width = 300, Height = 300, Selected = 1 };
        if (more) { preview.Toggle(); preview.Selected = 3; }
        preview.Measure(new Size(300, 300)); preview.Arrange(new Rect(0, 0, 300, 300)); preview.UpdateLayout();
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(600, 600, 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(preview);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var file = System.IO.File.Create(destination); encoder.Save(file);
    }
    internal void Move(Native.Point point)
    {
        if (point.X != origin.X || point.Y != origin.Y) moved = true;
        if (!moved || !IsLoaded) return;
        var local = PointFromScreen(new Point(point.X, point.Y));
        ring.Selected = Hit(local.X - 150, local.Y - 150);
        ring.InvalidateVisual();
    }
    internal void Navigate(int direction)
    {
        for (int i = 0; i < 6; i++) { ring.Selected = (ring.Selected + direction + 6) % 6; if (ring.Items[ring.Selected] != null) break; }
        moved = true; ring.InvalidateVisual();
    }
    internal void Click(Native.Point point)
    {
        if (!IsLoaded) return;
        var local = PointFromScreen(new Point(point.X, point.Y)); ring.Selected = Hit(local.X - 150, local.Y - 150);
        moved = true; ExecuteSelection();
    }
    internal void ExecuteSelection()
    {
        object? item = moved && ring.Selected >= 0 ? ring.Items[ring.Selected] : null;
        if (item is OutputFormat format) { Dismiss?.Invoke(); Pick?.Invoke(format); }
        else if (item is string) { ring.Toggle(); moved = false; }
        else Dismiss?.Invoke();
    }
    private sealed class Ring : Canvas
    {
        private readonly string file;
        private readonly OutputFormat[] formats;
        internal bool EngineMissing;
        internal Action<int>? Invoked;
        private readonly bool light;
        private readonly ImageSource? fileIcon;
        internal Ring(string file, OutputFormat[] formats, bool? lightOverride = null)
        {
            this.file = System.IO.Path.GetFileName(file); this.formats = formats; Items = MainRing(formats);
            if (System.IO.File.Exists(file))
            {
                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(file);
                if (icon != null) fileIcon = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
            }
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            light = lightOverride ?? (key?.GetValue("AppsUseLightTheme") is not int value || value != 0);
            AutomationProperties.SetName(this, file + " 변환 형식"); RebuildButtons();
        }
        internal int Selected = -1;
        private bool more;
        internal object?[] Items { get; private set; }
        private void RebuildButtons()
        {
            Children.Clear();
            for (int i = 0; i < Items.Length; i++)
            {
                if (Items[i] == null) continue;
                int slot = i; string label = Items[i] is OutputFormat f ? f.Name + "로 변환" : (string)Items[i]!;
                var button = new Button { Width = 68, Height = 36, Opacity = 0, Focusable = true, Content = label };
                AutomationProperties.SetName(button, label); button.Click += (_, _) => Invoked?.Invoke(slot);
                double angle = (i * 60 - 90) * Math.PI / 180;
                SetLeft(button, 150 + 96 * Math.Cos(angle) - 34); SetTop(button, 150 + 96 * Math.Sin(angle) - 18); Children.Add(button);
            }
        }
        private static object?[] MainRing(OutputFormat[] formats)
        {
            var slots = new object?[6];
            foreach (var f in formats.Where(f => f.Slot >= 0)) slots[f.Slot] = f;
            if (formats.Any(f => f.Slot < 0)) slots[4] = "더 보기";
            return slots;
        }
        internal void Toggle()
        {
            more = !more; Items = MainRing(formats);
            if (more) { Items = new object?[6]; Items[4] = "돌아가기"; int[] near = [3, 5, 2, 0, 1]; int i = 0; foreach (var f in formats.Where(f => f.Slot < 0)) Items[near[i++]] = f; }
            Selected = -1; RebuildButtons(); InvalidateVisual();
        }
        protected override void OnRender(DrawingContext dc)
        {
            var plate = new CombinedGeometry(GeometryCombineMode.Exclude,
                new EllipseGeometry(new Point(150, 150), 135, 135), new EllipseGeometry(new Point(150, 150), 53, 53));
            dc.DrawGeometry(Brand.Plate(light), new Pen(Brand.Faint(light), 1), plate);
            for (int i = 0; i < 6; i++)
            {
                // Same inset geometry and round join as the Mac ring: 6pt gaps,
                // 58/130 radii, 7pt corners. Insetting before stroking keeps gaps even.
                double mid = (i * 60 - 90) * Math.PI / 180, edge = Math.PI / 6;
                double outer = 123, inner = 65, dOut = Math.Asin(10 / outer), dIn = Math.Asin(10 / inner);
                Point At(double r, double theta) => new(150 + r * Math.Cos(theta), 150 + r * Math.Sin(theta));
                var shape = new StreamGeometry();
                using (var context = shape.Open())
                {
                    context.BeginFigure(At(outer, mid - edge + dOut), true, true);
                    context.ArcTo(At(outer, mid + edge - dOut), new Size(outer, outer), 0, false, SweepDirection.Clockwise, true, false);
                    context.LineTo(At(inner, mid + edge - dIn), true, false);
                    context.ArcTo(At(inner, mid - edge + dIn), new Size(inner, inner), 0, false, SweepDirection.Counterclockwise, true, false);
                }
                var fill = Items[i] == null ? Brand.Faint(light) : i == Selected ? Brand.Selected : Brand.Tint(light);
                dc.DrawGeometry(fill, new Pen(fill, 14) { LineJoin = PenLineJoin.Round }, shape);
                if (Items[i] != null)
                {
                    string label = Items[i] is OutputFormat f ? f.Name : (string)Items[i]!;
                    var ink = i == Selected ? Brand.SelectedInk : Brand.Ink(light);
                    var text = Text(label, Items[i] is OutputFormat ? 18 : 13, ink); text.SetFontWeight(FontWeights.Bold);
                    var center = At(94, mid); dc.DrawText(text, new Point(center.X - text.Width / 2, center.Y - text.Height / 2));
                    if (EngineMissing && Items[i] is OutputFormat format && format.Extension != "html") { var hint = Text("기능 추가 필요", 8, ink); dc.DrawText(hint, new Point(center.X - hint.Width / 2, center.Y + 10)); }
                }
            }
            // Keep the middle transparent, like macOS; show the selected file, not a brand card.
            var inkCenter = Brand.Ink(light);
            if (more) { var title = Text("더 보기", 10, inkCenter); dc.DrawText(title, new Point(150 - title.Width / 2, 144)); return; }
            if (fileIcon != null) dc.DrawImage(fileIcon, new Rect(139, 123, 22, 22));
            else dc.DrawRoundedRectangle(Brushes.WhiteSmoke, new Pen(Brushes.Gray, 1), new Rect(143, 123, 14, 20), 1, 1);
            string name = System.IO.Path.GetFileNameWithoutExtension(file);
            if (name.Length > 9) name = name[..4] + "…" + name[^4..];
            var titleFile = Text(name, 9, inkCenter); dc.DrawText(titleFile, new Point(150 - titleFile.Width / 2, 148));
            var subtitle = Text(System.IO.Path.GetExtension(file).TrimStart('.').ToUpperInvariant(), 9, inkCenter); subtitle.SetFontWeight(FontWeights.Bold);
            dc.DrawText(subtitle, new Point(150 - subtitle.Width / 2, 161));
        }
        private FormattedText Text(string text, double size, Brush brush) => new(text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Malgun Gothic"), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
    }
}

using System;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Morphy;

internal static class Brand
{
    // Shared with Sources/Morphy/RadialMenu.swift.
    internal static Color Color(uint rgb) => System.Windows.Media.Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    internal static Brush Brush(uint rgb) => new SolidColorBrush(Color(rgb));
    internal static Brush Main => Brush(0xF47B55);
    internal static Brush OnMain => Brush(0x3A1A0E);
    internal static Brush Plate(bool light) => SystemParameters.HighContrast ? SystemColors.WindowBrush : Brush(light ? 0xFFF7EDu : 0x2B211Du);
    internal static Brush Tint(bool light) => SystemParameters.HighContrast ? SystemColors.WindowBrush : Brush(light ? 0xFDE3D5u : 0x573529u);
    internal static Brush Faint(bool light) => SystemParameters.HighContrast ? SystemColors.WindowBrush : Brush(light ? 0xFEF1E5u : 0x392721u);
    internal static Brush Ink(bool light) => SystemParameters.HighContrast ? SystemColors.WindowTextBrush : Brush(light ? 0x6B2E19u : 0xFFE4D6u);
    internal static Brush Selected => SystemParameters.HighContrast ? SystemColors.HighlightBrush : Main;
    internal static Brush SelectedInk => SystemParameters.HighContrast ? SystemColors.HighlightTextBrush : OnMain;
    internal static BitmapSource Logo
    {
        get
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Morphy.Brand.png")!;
            var image = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad); image.Freeze(); return image;
        }
    }
    internal static Image LogoView(double size = 56) => new() { Source = Logo, Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 16) };
    internal static void Apply(Window window)
    {
        window.Icon = Logo; window.Background = Plate(true); window.Foreground = Ink(true); window.FontFamily = new FontFamily("Segoe UI");
        var button = new Style(typeof(Button));
        button.Setters.Add(new Setter(Control.BackgroundProperty, Selected));
        button.Setters.Add(new Setter(Control.ForegroundProperty, SelectedInk));
        button.Setters.Add(new Setter(Control.BorderBrushProperty, Selected));
        window.Resources[typeof(Button)] = button;
        var progress = new Style(typeof(ProgressBar));
        progress.Setters.Add(new Setter(Control.ForegroundProperty, Selected));
        progress.Setters.Add(new Setter(Control.BackgroundProperty, Tint(true)));
        window.Resources[typeof(ProgressBar)] = progress;
    }
}

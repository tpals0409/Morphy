using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;

namespace Morphy;

internal sealed class OperationWindow : Window
{
    private readonly TextBlock label = new() { FontSize = 15, Margin = new Thickness(0, 0, 0, 16) };
    private readonly ProgressBar bar = new() { Minimum = 0, Maximum = 100, Height = 10 };
    private bool finished;
    internal OperationWindow(string title, CancellationTokenSource cancellation)
    {
        Title = title; Width = 400; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        label.Text = title;
        var panel = new StackPanel { Margin = new Thickness(28) }; panel.Children.Add(label); panel.Children.Add(bar);
        var button = new Button { Content = "취소", Margin = new Thickness(0, 20, 0, 0), Padding = new Thickness(14, 6, 14, 6), HorizontalAlignment = HorizontalAlignment.Right };
        button.Click += (_, _) => { cancellation.Cancel(); button.IsEnabled = false; label.Text = "취소하는 중…"; };
        panel.Children.Add(button); Content = panel;
        Closing += (_, e) => { if (!finished) { cancellation.Cancel(); e.Cancel = true; label.Text = "취소하는 중…"; } };
    }
    internal void Update(string stage, double fraction) { label.Text = $"{stage} · {fraction:P0}"; bar.Value = fraction * 100; }
    internal void Finish() { finished = true; Close(); }
}

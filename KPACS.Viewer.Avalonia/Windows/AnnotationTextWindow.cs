using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace KPACS.Viewer.Windows;

public sealed class AnnotationTextWindow : Window
{
    private readonly TextBox _textBox;

    public AnnotationTextWindow(string? initialText = null)
    {
        Title = "Annotation";
        Width = 420;
        Height = 190;
        MinWidth = 320;
        MinHeight = 170;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;
        Background = new SolidColorBrush(Color.Parse("#FF202020"));

        _textBox = new TextBox
        {
            Text = initialText ?? string.Empty,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 72,
        };

        var okButton = new Button
        {
            Content = "OK",
            MinWidth = 90,
            IsDefault = true,
        };
        okButton.Click += (_, _) => Close(_textBox.Text ?? string.Empty);

        var skipButton = new Button
        {
            Content = "No Text",
            MinWidth = 90,
        };
        skipButton.Click += (_, _) => Close(string.Empty);

        var cancelButton = new Button
        {
            Content = "Cancel",
            MinWidth = 90,
            IsCancel = true,
        };
        cancelButton.Click += (_, _) => Close((string?)null);

        Content = new Border
        {
            Padding = new Thickness(16),
            Child = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Enter annotation text (optional):",
                        Foreground = Brushes.White,
                        FontWeight = FontWeight.SemiBold,
                    },
                    _textBox,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { skipButton, cancelButton, okButton },
                    },
                },
            },
        };

        Opened += (_, _) =>
        {
            _textBox.Focus();
            _textBox.CaretIndex = _textBox.Text?.Length ?? 0;
        };

        KeyDown += OnWindowKeyDown;
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close((string?)null);
            e.Handled = true;
        }
    }
}
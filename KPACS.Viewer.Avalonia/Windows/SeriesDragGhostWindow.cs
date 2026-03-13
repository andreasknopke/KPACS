using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using KPACS.Viewer.Controls;
using KPACS.Viewer.Models;

namespace KPACS.Viewer.Windows;

public sealed class SeriesDragGhostWindow : Window
{
    public SeriesDragGhostWindow(SeriesRecord series, string thumbnailPath)
    {
        Width = 108;
        Height = 86;
        CanResize = false;
        ShowInTaskbar = false;
        SystemDecorations = SystemDecorations.None;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Topmost = true;
        IsHitTestVisible = false;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];

        var ghostBorder = new Border
        {
            Width = 108,
            Height = 86,
            Padding = new Thickness(4),
            Background = new SolidColorBrush(Color.Parse("#D0000000")),
            BorderBrush = new SolidColorBrush(Color.Parse("#FFF1E000")),
            BorderThickness = new Thickness(1),
            Opacity = 0.9,
            IsHitTestVisible = false,
        };

        var grid = new Grid
        {
            RowDefinitions = new RowDefinitions("*,Auto"),
            IsHitTestVisible = false,
        };

        var thumbPanel = new DicomViewPanel
        {
            Width = 98,
            Height = 58,
            ShowOverlay = false,
            ShowToolboxButton = false,
            IsHitTestVisible = false,
        };
        if (!string.IsNullOrWhiteSpace(thumbnailPath) && File.Exists(thumbnailPath))
        {
            thumbPanel.LoadFile(thumbnailPath);
        }

        var label = new TextBlock
        {
            Text = $"S{Math.Max(series.SeriesNumber, 1)}",
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0),
            IsHitTestVisible = false,
        };

        grid.Children.Add(thumbPanel);
        Grid.SetRow(label, 1);
        grid.Children.Add(label);
        ghostBorder.Child = grid;
        Content = ghostBorder;
    }

    public void MoveTo(Point screenPoint, int offsetX, int offsetY)
    {
        Position = new PixelPoint(
            (int)Math.Round(screenPoint.X) + offsetX,
            (int)Math.Round(screenPoint.Y) + offsetY);
    }
}
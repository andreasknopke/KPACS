using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using ShapesPath = Avalonia.Controls.Shapes.Path;

namespace KPACS.Viewer;

public partial class StudyViewerWindow
{
    private static readonly IBrush s_toolboxIconBrush = new SolidColorBrush(Color.Parse("#FFF5FAFF"));
    private static readonly IBrush s_toolboxIconFillBrush = new SolidColorBrush(Color.Parse("#33F5FAFF"));

    private void InitializeToolboxIcons()
    {
        ConfigureIconButton(TopBarStudyBrowserButton, CreateStudyBrowserIcon(), "Bring the Study Browser to the front.");
        ConfigureIconButton(StudyBrowserButton, CreateStudyBrowserIcon(), "Bring the Study Browser to the front.");
        ConfigureLabeledIconButton(WorkspaceLayoutButton, CreateLayoutIcon(2, 2), "Layout", "Viewer layout presets and custom layouts.");
        ConfigureIconButton(ToolboxNavigateButton, CreateNavigateIcon(), "Navigate: left drag zoom/pan, wheel scroll, middle drag fast stack, right drag window/level.");
        ConfigureIconButton(ToolboxPixelLensButton, CreatePixelLensIcon(), "Pixel lens.");
        ConfigureIconButton(ToolboxLineButton, CreateLineMeasureIcon(), "Line measurement.");
        ConfigureIconButton(ToolboxAngleButton, CreateAngleMeasureIcon(), "Angle measurement.");
        ConfigureIconButton(ToolboxAnnotationButton, CreateAnnotationIcon(), "Text annotation.");
        ConfigureIconButton(ToolboxRectangleRoiButton, CreateRectangleRoiIcon(), "Rectangle ROI.");
        ConfigureIconButton(ToolboxEllipseRoiButton, CreateEllipseRoiIcon(), "Ellipse ROI.");
        ConfigureIconButton(ToolboxPolygonRoiButton, CreatePolygonRoiIcon(), "Polygon ROI.");
        ConfigureIconButton(ToolboxVolumeRoiButton, CreateVolumeRoiIcon(), "3D ROI across multiple slices.");
        ConfigureIconButton(ToolboxModifyButton, CreateModifyIcon(), "Modify selected measurement.");
        ConfigureIconButton(ToolboxEraseButton, CreateEraseIcon(), "Erase selected measurement.");
        ConfigureIconButton(ToolboxOverlayToggleButton, CreateOverlayIcon(), "Toggle image overlay.");
        ConfigureIconButton(Toolbox3DCursorButton, Create3DCursorIcon(), "3D cursor: hold SHIFT while clicking in a viewport. Click here to arm or clear the current 3D cursor.");
        ConfigureIconButton(ToolboxLinkedSyncToggleButton, CreateLinkedSyncIcon(), "Toggle linked sync between compatible viewports.");
        ConfigureIconButton(MeasurementInsightPinButton, CreatePinPanelIcon(), "Pin the ROI panel and retain the last measurement details.");
        ConfigureIconButton(MeasurementInsightCollapseButton, CreateChevronDownIcon(), "Collapse or expand the ROI histogram section.");
        ConfigureIconButton(VolumeRoiDraftPinButton, CreatePinPanelIcon(), "Pin the 3D ROI model panel and retain the last preview.");
    }

    private static void ConfigureIconButton(ContentControl button, Control icon, string toolTip)
    {
        button.Content = icon;
        ToolTip.SetTip(button, toolTip);
    }

    private static void ConfigureLabeledIconButton(ContentControl button, Control icon, string text, string toolTip)
    {
        button.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                icon,
                new TextBlock
                {
                    Text = text,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                }
            }
        };
        ToolTip.SetTip(button, toolTip);
    }

    private static Control CreateNavigateIcon() => CreateIconHost(
        StrokePath("M12 3 L12 21 M3 12 L21 12 M12 3 L9.5 5.5 M12 3 L14.5 5.5 M21 12 L18.5 9.5 M21 12 L18.5 14.5 M12 21 L9.5 18.5 M12 21 L14.5 18.5 M3 12 L5.5 9.5 M3 12 L5.5 14.5"),
        FilledEllipse(10, 10, 4, 4));

    private static Control CreatePixelLensIcon() => CreateIconHost(
        OutlineEllipse(4.5, 4.5, 9, 9),
        LineShape(12.5, 12.5, 19, 19),
        FilledRectangle(7, 7, 2.2, 2.2),
        FilledRectangle(10, 7, 2.2, 2.2),
        FilledRectangle(7, 10, 2.2, 2.2),
        FilledRectangle(10, 10, 2.2, 2.2));

    private static Control CreateLineMeasureIcon() => CreateIconHost(
        LineShape(5, 18, 19, 6),
        OutlineEllipse(3, 16, 4, 4),
        OutlineEllipse(17, 4, 4, 4));

    private static Control CreateAngleMeasureIcon() => CreateIconHost(
        LineShape(6, 18, 12, 10),
        LineShape(12, 10, 19, 17),
        StrokePath("M9 17 C10 13.8 12 12.1 15.2 12.2"),
        OutlineEllipse(10.4, 8.4, 3.2, 3.2));

    private static Control CreateAnnotationIcon() => CreateIconHost(
        OutlinePath("M5 6 L19 6 L19 15 L12.5 15 L8.5 18.5 L8.5 15 L5 15 Z"),
        LineShape(8, 9, 16, 9),
        LineShape(8, 12, 14, 12));

    private static Control CreateRectangleRoiIcon() => CreateIconHost(
        OutlineRectangle(6, 6, 12, 12),
        FilledEllipse(4.5, 4.5, 3, 3),
        FilledEllipse(16.5, 4.5, 3, 3),
        FilledEllipse(4.5, 16.5, 3, 3),
        FilledEllipse(16.5, 16.5, 3, 3));

    private static Control CreateEllipseRoiIcon() => CreateIconHost(
        OutlineEllipse(6, 7, 12, 10),
        FilledEllipse(4.5, 10.5, 3, 3),
        FilledEllipse(16.5, 10.5, 3, 3),
        FilledEllipse(10.5, 4.5, 3, 3),
        FilledEllipse(10.5, 16.5, 3, 3));

    private static Control CreatePolygonRoiIcon() => CreateIconHost(
        OutlinePath("M7 8 L15 6 L18 11 L14 18 L8 16 Z"),
        FilledEllipse(5.5, 6.5, 3, 3),
        FilledEllipse(13.5, 4.5, 3, 3),
        FilledEllipse(16.5, 9.5, 3, 3),
        FilledEllipse(12.5, 16.5, 3, 3),
        FilledEllipse(6.5, 14.5, 3, 3));

    private static Control CreateVolumeRoiIcon() => CreateIconHost(
        OutlinePath("M7 8 L14 6 L17 10 L13 14 L8 13 Z"),
        OutlinePath("M7 12 L14 10 L17 14 L13 18 L8 17 Z"),
        LineShape(7, 8, 7, 12, 1.5),
        LineShape(14, 6, 14, 10, 1.5),
        LineShape(17, 10, 17, 14, 1.5),
        LineShape(8, 13, 8, 17, 1.5));

    private static Control CreateModifyIcon() => CreateIconHost(
        OutlineRectangle(6, 6, 12, 12),
        LineShape(8, 12, 16, 12),
        StrokePath("M8 12 L10 10 M8 12 L10 14 M16 12 L14 10 M16 12 L14 14"),
        FilledEllipse(4.5, 4.5, 3, 3),
        FilledEllipse(16.5, 16.5, 3, 3));

    private static Control CreateEraseIcon() => CreateIconHost(
        OutlinePath("M8 6 L15 6 L20 11 L12 19 L5 12 Z"),
        LineShape(7.5, 16.5, 16.5, 7.5),
        LineShape(11.5, 18.5, 19.5, 10.5));

    private static Control CreateOverlayIcon() => CreateIconHost(
        OutlineRectangle(4.5, 5, 15, 14),
        LineShape(7.5, 9, 16.5, 9),
        LineShape(7.5, 12, 16.5, 12),
        LineShape(7.5, 15, 13.5, 15));

    private static Control CreateLinkedSyncIcon() => CreateIconHost(
        StrokePath("M8.5 15 C6.2 15 5 13.7 5 11.6 C5 9.5 6.2 8.2 8.5 8.2 L11 8.2"),
        StrokePath("M15.5 15.8 L13 15.8 C10.7 15.8 9.5 14.5 9.5 12.4 C9.5 10.3 10.7 9 13 9 L15.5 9"),
        LineShape(10.5, 12, 13.5, 12));

    private static Control CreateStudyBrowserIcon() => CreateIconHost(
        OutlineRectangle(4.5, 5.5, 15, 13),
        LineShape(8.5, 5.5, 8.5, 18.5),
        FilledRectangle(6, 8, 1.6, 1.6),
        FilledRectangle(6, 11.2, 1.6, 1.6),
        FilledRectangle(6, 14.4, 1.6, 1.6),
        LineShape(10.8, 9, 17.2, 9),
        LineShape(10.8, 12, 17.2, 12),
        LineShape(10.8, 15, 15.5, 15));

    private static Control Create3DCursorIcon() => CreateIconHost(
        LineShape(12, 4, 12, 20),
        LineShape(4, 12, 20, 12),
        OutlineEllipse(8.2, 8.2, 7.6, 7.6),
        FilledEllipse(10.7, 10.7, 2.6, 2.6));

    private static Control CreatePinPanelIcon() => CreateIconHost(
        StrokePath("M9 6 L15 6 L17.5 8.5 L15 11 L13.2 11 L13.2 18.5 L10.8 18.5 L10.8 11 L9 11 L6.5 8.5 Z", 1.5),
        LineShape(12, 18.5, 12, 21, 1.5));

    private static Control CreateChevronDownIcon() => CreateIconHost(
        StrokePath("M7 10 L12 15 L17 10", 2.0));

    private static Control CreateChevronRightIcon() => CreateIconHost(
        StrokePath("M10 7 L15 12 L10 17", 2.0));

    private static Control CreateLayoutIcon(int rows, int columns)
    {
        var canvas = new Canvas
        {
            Width = 24,
            Height = 24
        };

        const double outerLeft = 4;
        const double outerTop = 4;
        const double outerWidth = 16;
        const double outerHeight = 16;
        const double gap = 1.2;

        double cellWidth = (outerWidth - (gap * (columns - 1))) / columns;
        double cellHeight = (outerHeight - (gap * (rows - 1))) / rows;

        for (int row = 0; row < rows; row++)
        {
            for (int column = 0; column < columns; column++)
            {
                double left = outerLeft + (column * (cellWidth + gap));
                double top = outerTop + (row * (cellHeight + gap));
                canvas.Children.Add(OutlineRectangle(left, top, cellWidth, cellHeight));
            }
        }

        return new Viewbox
        {
            Width = 18,
            Height = 18,
            Stretch = Stretch.Uniform,
            Child = canvas
        };
    }

    private static Control CreateIconHost(params Control[] layers)
    {
        var canvas = new Canvas
        {
            Width = 24,
            Height = 24
        };

        foreach (Control layer in layers)
        {
            canvas.Children.Add(layer);
        }

        return new Viewbox
        {
            Width = 18,
            Height = 18,
            Stretch = Stretch.Uniform,
            Child = canvas
        };
    }

    private static ShapesPath StrokePath(string data, double thickness = 1.8) => new()
    {
        Data = Geometry.Parse(data),
        Stroke = s_toolboxIconBrush,
        StrokeThickness = thickness,
        StrokeLineCap = PenLineCap.Round
    };

    private static ShapesPath OutlinePath(string data, double thickness = 1.6) => new()
    {
        Data = Geometry.Parse(data),
        Stroke = s_toolboxIconBrush,
        Fill = Brushes.Transparent,
        StrokeThickness = thickness,
        StrokeLineCap = PenLineCap.Round
    };

    private static Line LineShape(double x1, double y1, double x2, double y2, double thickness = 1.8) => new()
    {
        StartPoint = new Point(x1, y1),
        EndPoint = new Point(x2, y2),
        Stroke = s_toolboxIconBrush,
        StrokeThickness = thickness,
        StrokeLineCap = PenLineCap.Round
    };

    private static Ellipse OutlineEllipse(double left, double top, double width, double height, double thickness = 1.6)
    {
        var ellipse = new Ellipse
        {
            Width = width,
            Height = height,
            Stroke = s_toolboxIconBrush,
            Fill = Brushes.Transparent,
            StrokeThickness = thickness
        };
        Canvas.SetLeft(ellipse, left);
        Canvas.SetTop(ellipse, top);
        return ellipse;
    }

    private static Ellipse FilledEllipse(double left, double top, double width, double height)
    {
        var ellipse = new Ellipse
        {
            Width = width,
            Height = height,
            Fill = s_toolboxIconBrush
        };
        Canvas.SetLeft(ellipse, left);
        Canvas.SetTop(ellipse, top);
        return ellipse;
    }

    private static Rectangle OutlineRectangle(double left, double top, double width, double height, double thickness = 1.5)
    {
        var rectangle = new Rectangle
        {
            Width = width,
            Height = height,
            Stroke = s_toolboxIconBrush,
            Fill = s_toolboxIconFillBrush,
            StrokeThickness = thickness,
            RadiusX = 1.5,
            RadiusY = 1.5
        };
        Canvas.SetLeft(rectangle, left);
        Canvas.SetTop(rectangle, top);
        return rectangle;
    }

    private static Rectangle FilledRectangle(double left, double top, double width, double height)
    {
        var rectangle = new Rectangle
        {
            Width = width,
            Height = height,
            Fill = s_toolboxIconBrush,
            RadiusX = 0.8,
            RadiusY = 0.8
        };
        Canvas.SetLeft(rectangle, left);
        Canvas.SetTop(rectangle, top);
        return rectangle;
    }
}

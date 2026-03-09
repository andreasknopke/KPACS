using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using KPACS.Viewer.Models;

namespace KPACS.Viewer.Controls;

public partial class DicomViewPanel
{
    private readonly List<StudyMeasurement> _measurements = [];
    private MeasurementTool _measurementTool;
    private Guid? _selectedMeasurementId;
    private MeasurementDraft? _measurementDraft;
    private MeasurementEditSession? _measurementEditSession;

    public event Action<StudyMeasurement>? MeasurementCreated;
    public event Action<StudyMeasurement>? MeasurementUpdated;
    public event Action<Guid?>? SelectedMeasurementChanged;

    public void SetMeasurementTool(MeasurementTool tool)
    {
        _measurementTool = tool;
        if (tool != MeasurementTool.PixelLens)
        {
            PixelLensPanel.IsVisible = false;
        }

        if (tool == MeasurementTool.None)
        {
            _measurementDraft = null;
            _measurementEditSession = null;
        }

        UpdateMeasurementPresentation();
    }

    public void SetMeasurements(IEnumerable<StudyMeasurement> measurements, Guid? selectedMeasurementId)
    {
        _measurements.Clear();
        _measurements.AddRange(measurements);
        _selectedMeasurementId = _measurements.Any(measurement => measurement.Id == selectedMeasurementId)
            ? selectedMeasurementId
            : null;
        UpdateMeasurementPresentation();
    }

    private void ResetMeasurementStateForNewImage()
    {
        _measurementDraft = null;
        _measurementEditSession = null;
        PixelLensPanel.IsVisible = false;
        UpdateMeasurementPresentation();
    }

    private void UpdateMeasurementPresentation()
    {
        UpdateMeasurementOverlay();
        if (_measurementTool != MeasurementTool.PixelLens)
        {
            PixelLensPanel.IsVisible = false;
        }
    }

    private bool HandleMeasurementPointerPressed(PointerPoint point, Point controlPoint, PointerPressedEventArgs e)
    {
        if (!point.Properties.IsLeftButtonPressed || _measurementTool == MeasurementTool.None)
        {
            return false;
        }

        if (_measurementTool == MeasurementTool.PixelLens)
        {
            UpdatePixelLens(controlPoint);
            e.Handled = true;
            return true;
        }

        if (!TryGetImagePoint(controlPoint, out Point imagePoint))
        {
            e.Handled = true;
            return true;
        }

        if (TryBeginMeasurementEdit(controlPoint, imagePoint, e.Pointer))
        {
            e.Handled = true;
            return true;
        }

        switch (_measurementTool)
        {
            case MeasurementTool.Line:
                StartDragMeasurement(MeasurementKind.Line, imagePoint, e.Pointer);
                e.Handled = true;
                return true;
            case MeasurementTool.RectangleRoi:
                StartDragMeasurement(MeasurementKind.RectangleRoi, imagePoint, e.Pointer);
                e.Handled = true;
                return true;
            case MeasurementTool.Angle:
                HandleAnglePressed(imagePoint);
                e.Handled = true;
                return true;
            case MeasurementTool.PolygonRoi:
                HandlePolygonPressed(imagePoint, e.ClickCount);
                e.Handled = true;
                return true;
            default:
                return false;
        }
    }

    private bool HandleMeasurementPointerMoved(Point controlPoint, PointerEventArgs e)
    {
        if (_measurementTool == MeasurementTool.PixelLens)
        {
            UpdatePixelLens(controlPoint);
            return true;
        }

        if (_measurementEditSession is not null)
        {
            if (TryGetImagePoint(controlPoint, out Point imagePoint))
            {
                ApplyMeasurementEdit(imagePoint);
            }

            e.Handled = true;
            return true;
        }

        if (_measurementDraft is not null)
        {
            if (TryGetImagePoint(controlPoint, out Point imagePoint))
            {
                _measurementDraft.CurrentPoint = ClampImagePoint(imagePoint);
                UpdateMeasurementPresentation();
            }

            e.Handled = true;
            return true;
        }

        return _measurementTool != MeasurementTool.None;
    }

    private bool HandleMeasurementPointerReleased(PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left || _measurementTool == MeasurementTool.None)
        {
            return false;
        }

        if (_measurementEditSession is not null)
        {
            _measurementEditSession = null;
            _capturedPointer?.Capture(null);
            _capturedPointer = null;
            e.Handled = true;
            return true;
        }

        if (_measurementDraft is not null && _measurementDraft.IsDragBased)
        {
            FinalizeDragMeasurement();
            _capturedPointer?.Capture(null);
            _capturedPointer = null;
            e.Handled = true;
            return true;
        }

        return _measurementTool != MeasurementTool.None;
    }

    private void HandleMeasurementPointerExited()
    {
        if (_measurementTool == MeasurementTool.PixelLens)
        {
            PixelLensPanel.IsVisible = false;
        }
    }

    private void StartDragMeasurement(MeasurementKind kind, Point imagePoint, IPointer pointer)
    {
        SetSelectedMeasurement(null);
        _measurementDraft = new MeasurementDraft(kind, [ClampImagePoint(imagePoint)], ClampImagePoint(imagePoint), true);
        pointer.Capture(RootGrid);
        _capturedPointer = pointer;
        UpdateMeasurementPresentation();
    }

    private void HandleAnglePressed(Point imagePoint)
    {
        Point clamped = ClampImagePoint(imagePoint);

        if (_measurementDraft is null || _measurementDraft.Kind != MeasurementKind.Angle)
        {
            SetSelectedMeasurement(null);
            _measurementDraft = new MeasurementDraft(MeasurementKind.Angle, [clamped], clamped, false);
            UpdateMeasurementPresentation();
            return;
        }

        if (_measurementDraft.Points.Count == 1)
        {
            _measurementDraft.Points.Add(clamped);
            _measurementDraft.CurrentPoint = clamped;
            UpdateMeasurementPresentation();
            return;
        }

        if (_measurementDraft.Points.Count == 2)
        {
            List<Point> finalized = [.. _measurementDraft.Points, clamped];
            FinalizeMeasurementDraft(finalized);
        }
    }

    private void HandlePolygonPressed(Point imagePoint, int clickCount)
    {
        Point clamped = ClampImagePoint(imagePoint);

        if (_measurementDraft is null || _measurementDraft.Kind != MeasurementKind.PolygonRoi)
        {
            SetSelectedMeasurement(null);
            _measurementDraft = new MeasurementDraft(MeasurementKind.PolygonRoi, [clamped], clamped, false);
            UpdateMeasurementPresentation();
            return;
        }

        if (clickCount >= 2 && _measurementDraft.Points.Count >= 2)
        {
            List<Point> finalized = [.. _measurementDraft.Points];
            if (Distance(finalized[^1], clamped) > 0.5)
            {
                finalized.Add(clamped);
            }

            if (finalized.Count >= 3)
            {
                FinalizeMeasurementDraft(finalized);
            }

            return;
        }

        _measurementDraft.Points.Add(clamped);
        _measurementDraft.CurrentPoint = clamped;
        UpdateMeasurementPresentation();
    }

    private void FinalizeDragMeasurement()
    {
        if (_measurementDraft is null)
        {
            return;
        }

        List<Point> points =
        [
            _measurementDraft.Points[0],
            ClampImagePoint(_measurementDraft.CurrentPoint),
        ];

        if (Distance(points[0], points[1]) < 1.5)
        {
            _measurementDraft = null;
            UpdateMeasurementPresentation();
            return;
        }

        FinalizeMeasurementDraft(points);
    }

    private void FinalizeMeasurementDraft(IReadOnlyList<Point> imagePoints)
    {
        if (_measurementDraft is null)
        {
            return;
        }

        StudyMeasurement measurement = StudyMeasurement.Create(
            _measurementDraft.Kind,
            FilePath,
            SpatialMetadata,
            imagePoints);

        _measurementDraft = null;
        SetSelectedMeasurement(measurement.Id);
        MeasurementCreated?.Invoke(measurement);
        UpdateMeasurementPresentation();
    }

    private bool TryBeginMeasurementEdit(Point controlPoint, Point imagePoint, IPointer pointer)
    {
        MeasurementHit? hit = HitTestMeasurement(controlPoint);
        if (hit is null)
        {
            return false;
        }

        SetSelectedMeasurement(hit.Measurement.Id);
        _measurementEditSession = new MeasurementEditSession(
            hit.Measurement,
            hit.ImagePoints,
            ClampImagePoint(imagePoint),
            hit.HandleIndex,
            hit.MoveWholeMeasurement);

        pointer.Capture(RootGrid);
        _capturedPointer = pointer;
        UpdateMeasurementPresentation();
        return true;
    }

    private void ApplyMeasurementEdit(Point imagePoint)
    {
        if (_measurementEditSession is null)
        {
            return;
        }

        Point clamped = ClampImagePoint(imagePoint);
        Point[] updatedPoints = _measurementEditSession.ImagePoints.ToArray();

        if (_measurementEditSession.MoveWholeMeasurement)
        {
            Vector delta = clamped - _measurementEditSession.StartImagePoint;
            for (int index = 0; index < updatedPoints.Length; index++)
            {
                updatedPoints[index] = ClampImagePoint(new Point(
                    _measurementEditSession.ImagePoints[index].X + delta.X,
                    _measurementEditSession.ImagePoints[index].Y + delta.Y));
            }
        }
        else if (_measurementEditSession.HandleIndex >= 0 && _measurementEditSession.HandleIndex < updatedPoints.Length)
        {
            updatedPoints[_measurementEditSession.HandleIndex] = clamped;
        }

        StudyMeasurement updated = _measurementEditSession.Measurement.WithAnchors(SpatialMetadata, updatedPoints);
        MeasurementUpdated?.Invoke(updated);
        UpdateMeasurementPresentation();
    }

    private void SetSelectedMeasurement(Guid? measurementId)
    {
        if (_selectedMeasurementId == measurementId)
        {
            return;
        }

        _selectedMeasurementId = measurementId;
        SelectedMeasurementChanged?.Invoke(measurementId);
        UpdateMeasurementPresentation();
    }

    private MeasurementHit? HitTestMeasurement(Point controlPoint)
    {
        const double handleThreshold = 10.0;
        const double lineThreshold = 8.0;

        List<RenderedMeasurement> renderedMeasurements = GetRenderedMeasurements()
            .OrderBy(rendered => rendered.Measurement.Id == _selectedMeasurementId ? 1 : 0)
            .ToList();

        for (int index = renderedMeasurements.Count - 1; index >= 0; index--)
        {
            RenderedMeasurement rendered = renderedMeasurements[index];

            for (int pointIndex = 0; pointIndex < rendered.ControlPoints.Length; pointIndex++)
            {
                if (Distance(rendered.ControlPoints[pointIndex], controlPoint) <= handleThreshold)
                {
                    return new MeasurementHit(rendered.Measurement, rendered.ImagePoints, pointIndex, false);
                }
            }

            if (IsPointOnMeasurement(rendered, controlPoint, lineThreshold))
            {
                return new MeasurementHit(rendered.Measurement, rendered.ImagePoints, -1, true);
            }
        }

        return null;
    }

    private static bool IsPointOnMeasurement(RenderedMeasurement rendered, Point controlPoint, double threshold)
    {
        return rendered.Measurement.Kind switch
        {
            MeasurementKind.Line => DistanceToSegment(controlPoint, rendered.ControlPoints[0], rendered.ControlPoints[1]) <= threshold,
            MeasurementKind.Angle =>
                DistanceToSegment(controlPoint, rendered.ControlPoints[0], rendered.ControlPoints[1]) <= threshold ||
                DistanceToSegment(controlPoint, rendered.ControlPoints[1], rendered.ControlPoints[2]) <= threshold,
            MeasurementKind.RectangleRoi =>
                BuildRect(rendered.ControlPoints[0], rendered.ControlPoints[1]).Inflate(threshold).Contains(controlPoint),
            MeasurementKind.PolygonRoi =>
                IsPointInsidePolygon(controlPoint, rendered.ControlPoints) ||
                PolygonSegments(rendered.ControlPoints).Any(segment => DistanceToSegment(controlPoint, segment.Start, segment.End) <= threshold),
            _ => false,
        };
    }

    private void UpdateMeasurementOverlay()
    {
        MeasurementOverlay.Children.Clear();

        foreach (RenderedMeasurement rendered in GetRenderedMeasurements())
        {
            DrawRenderedMeasurement(rendered);
        }

        DrawMeasurementDraft();
    }

    private List<RenderedMeasurement> GetRenderedMeasurements()
    {
        List<RenderedMeasurement> renderedMeasurements = [];

        foreach (StudyMeasurement measurement in _measurements)
        {
            if (!measurement.TryProjectTo(SpatialMetadata, out Point[] imagePoints))
            {
                continue;
            }

            Point[] controlPoints = imagePoints.Select(ImageToControlPoint).ToArray();
            renderedMeasurements.Add(new RenderedMeasurement(
                measurement,
                imagePoints,
                controlPoints,
                measurement.Id == _selectedMeasurementId));
        }

        return renderedMeasurements;
    }

    private void DrawRenderedMeasurement(RenderedMeasurement rendered)
    {
        IBrush stroke = new SolidColorBrush(rendered.IsSelected ? Color.Parse("#FFFFD54F") : Color.Parse("#FF35C7FF"));
        IBrush fill = new SolidColorBrush(rendered.IsSelected ? Color.Parse("#20FFD54F") : Color.Parse("#1035C7FF"));

        switch (rendered.Measurement.Kind)
        {
            case MeasurementKind.Line:
                AddLine(rendered.ControlPoints[0], rendered.ControlPoints[1], stroke, 2);
                AddMeasurementLabel(rendered, GetLineMeasurementText(rendered.ImagePoints), GetMidPoint(rendered.ControlPoints[0], rendered.ControlPoints[1]));
                break;
            case MeasurementKind.Angle:
                AddLine(rendered.ControlPoints[0], rendered.ControlPoints[1], stroke, 2);
                AddLine(rendered.ControlPoints[1], rendered.ControlPoints[2], stroke, 2);
                AddMeasurementLabel(rendered, GetAngleMeasurementText(rendered.ImagePoints), rendered.ControlPoints[1]);
                break;
            case MeasurementKind.RectangleRoi:
                Rect rect = BuildRect(rendered.ControlPoints[0], rendered.ControlPoints[1]);
                var rectangle = new Rectangle
                {
                    Width = Math.Max(1, rect.Width),
                    Height = Math.Max(1, rect.Height),
                    Stroke = stroke,
                    Fill = fill,
                    StrokeThickness = 2,
                };
                Canvas.SetLeft(rectangle, rect.X);
                Canvas.SetTop(rectangle, rect.Y);
                MeasurementOverlay.Children.Add(rectangle);
                AddMeasurementLabel(rendered, GetRectangleRoiMeasurementText(rendered.ImagePoints), rect.BottomRight);
                break;
            case MeasurementKind.PolygonRoi:
                var polygon = new Polygon
                {
                    Points = new Points(rendered.ControlPoints),
                    Stroke = stroke,
                    Fill = fill,
                    StrokeThickness = 2,
                };
                MeasurementOverlay.Children.Add(polygon);
                AddMeasurementLabel(rendered, GetPolygonRoiMeasurementText(rendered.ImagePoints), GetPolygonCenter(rendered.ControlPoints));
                break;
        }

        if (rendered.IsSelected)
        {
            foreach (Point controlPoint in rendered.ControlPoints)
            {
                AddHandle(controlPoint, stroke);
            }
        }
    }

    private void DrawMeasurementDraft()
    {
        if (_measurementDraft is null)
        {
            return;
        }

        IBrush stroke = new SolidColorBrush(Color.Parse("#FFFFDD00"));
        IReadOnlyList<Point> previewPoints = _measurementDraft.Kind switch
        {
            MeasurementKind.Line or MeasurementKind.RectangleRoi => [_measurementDraft.Points[0], _measurementDraft.CurrentPoint],
            MeasurementKind.Angle when _measurementDraft.Points.Count == 1 => [_measurementDraft.Points[0], _measurementDraft.CurrentPoint],
            MeasurementKind.Angle => [_measurementDraft.Points[0], _measurementDraft.Points[1], _measurementDraft.CurrentPoint],
            MeasurementKind.PolygonRoi => [.. _measurementDraft.Points, _measurementDraft.CurrentPoint],
            _ => _measurementDraft.Points,
        };

        Point[] controlPoints = previewPoints.Select(ImageToControlPoint).ToArray();

        if (_measurementDraft.Kind == MeasurementKind.Line && controlPoints.Length == 2)
        {
            AddLine(controlPoints[0], controlPoints[1], stroke, 1.5);
        }
        else if (_measurementDraft.Kind == MeasurementKind.RectangleRoi && controlPoints.Length == 2)
        {
            Rect rect = BuildRect(controlPoints[0], controlPoints[1]);
            var rectangle = new Rectangle
            {
                Width = Math.Max(1, rect.Width),
                Height = Math.Max(1, rect.Height),
                Stroke = stroke,
                StrokeThickness = 1.5,
            };
            Canvas.SetLeft(rectangle, rect.X);
            Canvas.SetTop(rectangle, rect.Y);
            MeasurementOverlay.Children.Add(rectangle);
        }
        else if (_measurementDraft.Kind == MeasurementKind.Angle)
        {
            if (controlPoints.Length >= 2)
            {
                AddLine(controlPoints[0], controlPoints[1], stroke, 1.5);
            }

            if (controlPoints.Length >= 3)
            {
                AddLine(controlPoints[1], controlPoints[2], stroke, 1.5);
            }
        }
        else if (_measurementDraft.Kind == MeasurementKind.PolygonRoi && controlPoints.Length >= 2)
        {
            var polyline = new Polyline
            {
                Points = new Points(controlPoints),
                Stroke = stroke,
                StrokeThickness = 1.5,
            };
            MeasurementOverlay.Children.Add(polyline);
        }

        foreach (Point controlPoint in controlPoints)
        {
            AddHandle(controlPoint, stroke, 3.5);
        }
    }

    private void AddMeasurementLabel(RenderedMeasurement rendered, string text, Point anchor)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var border = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#B0101010")),
            BorderBrush = new SolidColorBrush(rendered.IsSelected ? Color.Parse("#FFFFD54F") : Color.Parse("#8040D8FF")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(4, 2),
            Child = new TextBlock
            {
                Text = text,
                Foreground = new SolidColorBrush(Color.Parse("#FFFFF6A8")),
                FontSize = 11,
            },
        };

        Canvas.SetLeft(border, anchor.X + 10);
        Canvas.SetTop(border, anchor.Y + 10);
        MeasurementOverlay.Children.Add(border);
    }

    private void AddLine(Point start, Point end, IBrush stroke, double thickness)
    {
        MeasurementOverlay.Children.Add(new Line
        {
            StartPoint = start,
            EndPoint = end,
            Stroke = stroke,
            StrokeThickness = thickness,
        });
    }

    private void AddHandle(Point point, IBrush stroke, double radius = 4.5)
    {
        var ellipse = new Ellipse
        {
            Width = radius * 2,
            Height = radius * 2,
            Stroke = stroke,
            StrokeThickness = 1.5,
            Fill = new SolidColorBrush(Color.Parse("#CC101010")),
        };
        Canvas.SetLeft(ellipse, point.X - radius);
        Canvas.SetTop(ellipse, point.Y - radius);
        MeasurementOverlay.Children.Add(ellipse);
    }

    private void UpdatePixelLens(Point controlPoint)
    {
        if (_measurementTool != MeasurementTool.PixelLens || !TryGetImagePoint(controlPoint, out Point imagePoint))
        {
            PixelLensPanel.IsVisible = false;
            return;
        }

        PixelLensPanel.IsVisible = true;
        Canvas.SetLeft(PixelLensPanel, Math.Min(RootGrid.Bounds.Width - PixelLensPanel.Width - 8, controlPoint.X + 18));
        Canvas.SetTop(PixelLensPanel, Math.Min(RootGrid.Bounds.Height - PixelLensPanel.Height - 8, controlPoint.Y + 18));
        PixelLensText.Text = BuildPixelLensText(imagePoint);
        PixelLensImage.Source = CreatePixelLensBitmap(imagePoint);
    }

    private string BuildPixelLensText(Point imagePoint)
    {
        int x = (int)Math.Round(imagePoint.X);
        int y = (int)Math.Round(imagePoint.Y);
        return TryGetPixelValue(x, y, out double value)
            ? $"X:{x} Y:{y}\n{value:F1}"
            : $"X:{x} Y:{y}";
    }

    private IImage? CreatePixelLensBitmap(Point imagePoint)
    {
        if (_displayBitmap is null)
        {
            return null;
        }

        const int sourceSize = 17;
        const int destinationSize = 128;
        int centerX = (int)Math.Round(imagePoint.X);
        int centerY = (int)Math.Round(imagePoint.Y);
        int sourceRadius = sourceSize / 2;

        using ILockedFramebuffer sourceFramebuffer = _displayBitmap.Lock();
        int sourceStride = sourceFramebuffer.RowBytes;
        byte[] sourceBytes = new byte[sourceStride * sourceFramebuffer.Size.Height];
        Marshal.Copy(sourceFramebuffer.Address, sourceBytes, 0, sourceBytes.Length);

        var bitmap = new WriteableBitmap(
            new PixelSize(destinationSize, destinationSize),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);

        using ILockedFramebuffer destinationFramebuffer = bitmap.Lock();
        byte[] destinationBytes = new byte[destinationFramebuffer.RowBytes * destinationFramebuffer.Size.Height];
        int bytesPerPixel = 4;

        for (int y = 0; y < destinationSize; y++)
        {
            int sourceY = Math.Clamp(centerY - sourceRadius + (y * sourceSize / destinationSize), 0, _imageHeight - 1);
            for (int x = 0; x < destinationSize; x++)
            {
                int sourceX = Math.Clamp(centerX - sourceRadius + (x * sourceSize / destinationSize), 0, _imageWidth - 1);
                int sourceIndex = (sourceY * sourceStride) + (sourceX * bytesPerPixel);
                int destinationIndex = (y * destinationFramebuffer.RowBytes) + (x * bytesPerPixel);
                Array.Copy(sourceBytes, sourceIndex, destinationBytes, destinationIndex, bytesPerPixel);
            }
        }

        Marshal.Copy(destinationBytes, 0, destinationFramebuffer.Address, destinationBytes.Length);
        return bitmap;
    }

    private string GetLineMeasurementText(Point[] imagePoints)
    {
        if (SpatialMetadata is null || imagePoints.Length < 2)
        {
            return string.Empty;
        }

        double dx = (imagePoints[1].X - imagePoints[0].X) * SpatialMetadata.ColumnSpacing;
        double dy = (imagePoints[1].Y - imagePoints[0].Y) * SpatialMetadata.RowSpacing;
        double distanceMm = Math.Sqrt((dx * dx) + (dy * dy));
        return $"{distanceMm:F1} mm";
    }

    private string GetAngleMeasurementText(Point[] imagePoints)
    {
        if (imagePoints.Length < 3)
        {
            return string.Empty;
        }

        Vector left = imagePoints[0] - imagePoints[1];
        Vector right = imagePoints[2] - imagePoints[1];
        double leftLength = Math.Sqrt((left.X * left.X) + (left.Y * left.Y));
        double rightLength = Math.Sqrt((right.X * right.X) + (right.Y * right.Y));
        if (leftLength < 0.001 || rightLength < 0.001)
        {
            return string.Empty;
        }

        double cosine = ((left.X * right.X) + (left.Y * right.Y)) / (leftLength * rightLength);
        cosine = Math.Clamp(cosine, -1, 1);
        return $"{Math.Acos(cosine) * 180.0 / Math.PI:F1}°";
    }

    private string GetRectangleRoiMeasurementText(Point[] imagePoints)
    {
        if (SpatialMetadata is null || imagePoints.Length < 2)
        {
            return string.Empty;
        }

        Rect rect = BuildRect(imagePoints[0], imagePoints[1]);
        RoiStatistics stats = CalculateRectangleStatistics(rect);
        double area = rect.Width * SpatialMetadata.ColumnSpacing * rect.Height * SpatialMetadata.RowSpacing;
        return $"Mean {stats.Mean:F1}\nMin {stats.Min:F1}  Max {stats.Max:F1}\nArea {area:F1} mm²";
    }

    private string GetPolygonRoiMeasurementText(Point[] imagePoints)
    {
        if (SpatialMetadata is null || imagePoints.Length < 3)
        {
            return string.Empty;
        }

        RoiStatistics stats = CalculatePolygonStatistics(imagePoints);
        double areaPixels = 0;
        for (int index = 0; index < imagePoints.Length; index++)
        {
            Point current = imagePoints[index];
            Point next = imagePoints[(index + 1) % imagePoints.Length];
            areaPixels += (current.X * next.Y) - (next.X * current.Y);
        }

        double area = Math.Abs(areaPixels) * 0.5 * SpatialMetadata.ColumnSpacing * SpatialMetadata.RowSpacing;
        return $"Mean {stats.Mean:F1}\nMin {stats.Min:F1}  Max {stats.Max:F1}\nArea {area:F1} mm²";
    }

    private RoiStatistics CalculateRectangleStatistics(Rect imageRect)
    {
        int left = Math.Clamp((int)Math.Floor(imageRect.X), 0, _imageWidth - 1);
        int top = Math.Clamp((int)Math.Floor(imageRect.Y), 0, _imageHeight - 1);
        int right = Math.Clamp((int)Math.Ceiling(imageRect.Right), 0, _imageWidth - 1);
        int bottom = Math.Clamp((int)Math.Ceiling(imageRect.Bottom), 0, _imageHeight - 1);

        return CalculateStatistics((x, y) => x >= left && x <= right && y >= top && y <= bottom, left, top, right, bottom);
    }

    private RoiStatistics CalculatePolygonStatistics(Point[] imagePoints)
    {
        int left = Math.Clamp((int)Math.Floor(imagePoints.Min(point => point.X)), 0, _imageWidth - 1);
        int right = Math.Clamp((int)Math.Ceiling(imagePoints.Max(point => point.X)), 0, _imageWidth - 1);
        int top = Math.Clamp((int)Math.Floor(imagePoints.Min(point => point.Y)), 0, _imageHeight - 1);
        int bottom = Math.Clamp((int)Math.Ceiling(imagePoints.Max(point => point.Y)), 0, _imageHeight - 1);

        return CalculateStatistics(
            (x, y) => IsPointInsidePolygon(new Point(x + 0.5, y + 0.5), imagePoints),
            left,
            top,
            right,
            bottom);
    }

    private RoiStatistics CalculateStatistics(Func<int, int, bool> inside, int left, int top, int right, int bottom)
    {
        double min = double.PositiveInfinity;
        double max = double.NegativeInfinity;
        double sum = 0;
        int count = 0;

        for (int y = top; y <= bottom; y++)
        {
            for (int x = left; x <= right; x++)
            {
                if (!inside(x, y) || !TryGetPixelValue(x, y, out double value))
                {
                    continue;
                }

                min = Math.Min(min, value);
                max = Math.Max(max, value);
                sum += value;
                count++;
            }
        }

        if (count == 0)
        {
            return new RoiStatistics(0, 0, 0);
        }

        return new RoiStatistics(sum / count, min, max);
    }

    private bool TryGetPixelValue(int x, int y, out double value)
    {
        value = 0;

        if (_rawPixelData is null || _samplesPerPixel != 1 || x < 0 || y < 0 || x >= _imageWidth || y >= _imageHeight)
        {
            return false;
        }

        int pixelIndex = (y * _imageWidth) + x;
        int storedValue;

        if (_bitsAllocated <= 8)
        {
            storedValue = _rawPixelData[pixelIndex];
            if (_bitsStored < 8)
            {
                int mask = (1 << _bitsStored) - 1;
                storedValue &= mask;
                if (_isSigned)
                {
                    int signBit = 1 << (_bitsStored - 1);
                    if ((storedValue & signBit) != 0)
                    {
                        storedValue -= 1 << _bitsStored;
                    }
                }
            }
        }
        else
        {
            int byteIndex = pixelIndex * 2;
            if (byteIndex + 1 >= _rawPixelData.Length)
            {
                return false;
            }

            ushort rawValue = BinaryPrimitives.ReadUInt16LittleEndian(_rawPixelData.AsSpan(byteIndex, 2));
            if (_isSigned && _bitsStored >= 16)
            {
                storedValue = (short)rawValue;
            }
            else
            {
                storedValue = rawValue;
                if (_bitsStored < 16)
                {
                    int mask = (1 << _bitsStored) - 1;
                    storedValue &= mask;
                    if (_isSigned)
                    {
                        int signBit = 1 << (_bitsStored - 1);
                        if ((storedValue & signBit) != 0)
                        {
                            storedValue -= 1 << _bitsStored;
                        }
                    }
                }
            }
        }

        value = (storedValue * _rescaleSlope) + _rescaleIntercept;
        return true;
    }

    private Point ImageToControlPoint(Point imagePoint) => new(_panX + (imagePoint.X * _zoomFactor), _panY + (imagePoint.Y * _zoomFactor));

    private Point ClampImagePoint(Point imagePoint) =>
        new(
            Math.Clamp(imagePoint.X, 0, Math.Max(0, _imageWidth - 1)),
            Math.Clamp(imagePoint.Y, 0, Math.Max(0, _imageHeight - 1)));

    private static Rect BuildRect(Point a, Point b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    private static Point GetMidPoint(Point a, Point b) => new((a.X + b.X) / 2.0, (a.Y + b.Y) / 2.0);

    private static double Distance(Point a, Point b)
    {
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    private static double DistanceToSegment(Point point, Point start, Point end)
    {
        double dx = end.X - start.X;
        double dy = end.Y - start.Y;
        if (Math.Abs(dx) < 0.0001 && Math.Abs(dy) < 0.0001)
        {
            return Distance(point, start);
        }

        double t = ((point.X - start.X) * dx + (point.Y - start.Y) * dy) / ((dx * dx) + (dy * dy));
        t = Math.Clamp(t, 0, 1);
        Point projection = new(start.X + (t * dx), start.Y + (t * dy));
        return Distance(point, projection);
    }

    private static IEnumerable<(Point Start, Point End)> PolygonSegments(Point[] points)
    {
        for (int index = 0; index < points.Length; index++)
        {
            yield return (points[index], points[(index + 1) % points.Length]);
        }
    }

    private static bool IsPointInsidePolygon(Point point, Point[] polygon)
    {
        bool inside = false;
        for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
        {
            bool intersect = ((polygon[i].Y > point.Y) != (polygon[j].Y > point.Y)) &&
                (point.X < ((polygon[j].X - polygon[i].X) * (point.Y - polygon[i].Y) / ((polygon[j].Y - polygon[i].Y) + double.Epsilon)) + polygon[i].X);
            if (intersect)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    private static Point GetPolygonCenter(Point[] points)
    {
        if (points.Length == 0)
        {
            return default;
        }

        return new(points.Average(point => point.X), points.Average(point => point.Y));
    }

    private sealed class MeasurementDraft(MeasurementKind kind, List<Point> points, Point currentPoint, bool isDragBased)
    {
        public MeasurementKind Kind { get; } = kind;
        public List<Point> Points { get; } = points;
        public Point CurrentPoint { get; set; } = currentPoint;
        public bool IsDragBased { get; } = isDragBased;
    }

    private sealed class MeasurementEditSession(
        StudyMeasurement measurement,
        Point[] imagePoints,
        Point startImagePoint,
        int handleIndex,
        bool moveWholeMeasurement)
    {
        public StudyMeasurement Measurement { get; } = measurement;
        public Point[] ImagePoints { get; } = imagePoints;
        public Point StartImagePoint { get; } = startImagePoint;
        public int HandleIndex { get; } = handleIndex;
        public bool MoveWholeMeasurement { get; } = moveWholeMeasurement;
    }

    private sealed record MeasurementHit(StudyMeasurement Measurement, Point[] ImagePoints, int HandleIndex, bool MoveWholeMeasurement);
    private sealed record RenderedMeasurement(StudyMeasurement Measurement, Point[] ImagePoints, Point[] ControlPoints, bool IsSelected);
    private sealed record RoiStatistics(double Mean, double Min, double Max);
}
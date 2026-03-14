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
    private static readonly Size s_unboundedMeasureSize = new(double.PositiveInfinity, double.PositiveInfinity);
    private static readonly Point s_defaultMeasurementLabelOffset = new(10, 10);

    public sealed record RoiDistributionDetails(
        string QuantityLabel,
        string Modality,
        int PixelCount,
        double AreaSquareMillimeters,
        double Mean,
        double Median,
        double StandardDeviation,
        double Minimum,
        double Maximum,
        double Percentile10,
        double Percentile90,
        IReadOnlyList<int> HistogramBins,
        double HistogramMinValue,
        double HistogramMaxValue);

    private readonly List<StudyMeasurement> _measurements = [];
    private MeasurementTool _measurementTool;
    private bool _measurementNudgeMode;
    private Guid? _selectedMeasurementId;
    private MeasurementDraft? _measurementDraft;
    private MeasurementEditSession? _measurementEditSession;
    private Func<StudyMeasurement, Point[], string?>? _measurementTextSupplementProvider;

    public event Action<StudyMeasurement>? MeasurementCreated;
    public event Action<StudyMeasurement>? MeasurementUpdated;
    public event Action<Guid>? MeasurementDeleted;
    public event Action<Guid?>? SelectedMeasurementChanged;

    public void SetMeasurementTextSupplementProvider(Func<StudyMeasurement, Point[], string?>? provider)
    {
        _measurementTextSupplementProvider = provider;
        UpdateMeasurementPresentation();
    }

    public void SetMeasurementTool(MeasurementTool tool)
    {
        _measurementTool = tool;
        if (tool != MeasurementTool.PixelLens)
        {
            PixelLensPanel.IsVisible = false;
        }

        if (tool == MeasurementTool.None)
        {
            CancelMeasurementInteraction();
        }

        UpdateMeasurementPresentation();
        UpdateInteractiveCursor();
        UpdateSecondaryCaptureButton();
    }

    public void SetMeasurementNudgeMode(bool enabled)
    {
        _measurementNudgeMode = enabled;
        UpdateInteractiveCursor();
    }

    public void CancelMeasurementInteraction()
    {
        _measurementDraft = null;
        _measurementEditSession = null;
        ClearVolumeRoiDraft();
        PixelLensPanel.IsVisible = false;
        _capturedPointer?.Capture(null);
        _capturedPointer = null;
        UpdateMeasurementPresentation();
        UpdateInteractiveCursor();
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

    public bool TryGetMeasurementDistribution(StudyMeasurement measurement, out RoiDistributionDetails details)
    {
        details = default!;

        if (SpatialMetadata is null || !measurement.TryProjectTo(SpatialMetadata, out Point[] imagePoints))
        {
            return false;
        }

        if (!TryCollectMeasurementValues(measurement.Kind, imagePoints, out List<double> values, out double areaSquareMillimeters))
        {
            return false;
        }

        details = BuildDistributionDetails(values, areaSquareMillimeters);
        return true;
    }

    private void ResetMeasurementStateForNewImage()
    {
        _measurementDraft = null;
        _measurementEditSession = null;
        PixelLensPanel.IsVisible = false;
        _capturedPointer?.Capture(null);
        _capturedPointer = null;

        if (!CanRetainVolumeRoiDraftForCurrentSlice())
        {
            ClearVolumeRoiDraft();
        }
        else
        {
            NotifyVolumeRoiDraftChanged();
        }

        UpdateMeasurementPresentation();
        UpdateInteractiveCursor();
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

        if (_measurementTool == MeasurementTool.Erase)
        {
            if (TryDeleteMeasurement(controlPoint))
            {
                e.Handled = true;
                return true;
            }

            return false;
        }

        if (_measurementTool == MeasurementTool.Modify && _measurementNudgeMode)
        {
            if (TrySelectMeasurement(controlPoint))
            {
                e.Handled = true;
                return true;
            }

            SetSelectedMeasurement(null);
            e.Handled = true;
            return true;
        }

        Point imagePoint = GetImagePointFromControl(controlPoint);
        if (!IsImagePointWithinBounds(imagePoint))
        {
            e.Handled = true;
            return true;
        }

        if (_measurementTool == MeasurementTool.VolumeRoi)
        {
            HandleVolumeRoiPressed(imagePoint, e.ClickCount, e.KeyModifiers);
            e.Handled = true;
            return true;
        }

        if (TryBeginMeasurementEdit(controlPoint, e.Pointer))
        {
            e.Handled = true;
            return true;
        }

        if (_measurementTool == MeasurementTool.Modify)
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
            case MeasurementTool.Annotation:
                StartDragMeasurement(MeasurementKind.Annotation, imagePoint, e.Pointer);
                e.Handled = true;
                return true;
            case MeasurementTool.RectangleRoi:
                StartDragMeasurement(MeasurementKind.RectangleRoi, imagePoint, e.Pointer);
                e.Handled = true;
                return true;
            case MeasurementTool.EllipseRoi:
                StartDragMeasurement(MeasurementKind.EllipseRoi, imagePoint, e.Pointer);
                e.Handled = true;
                return true;
            case MeasurementTool.Angle:
                HandleAnglePressed(imagePoint);
                e.Handled = true;
                return true;
            case MeasurementTool.PolygonRoi:
                HandlePolygonPressed(imagePoint, e.ClickCount, e.KeyModifiers);
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
            ApplyMeasurementEdit(controlPoint);

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

        if (HandleVolumeRoiPointerMoved(controlPoint))
        {
            e.Handled = true;
            return true;
        }

        return _measurementTool is not MeasurementTool.None and not MeasurementTool.Erase;
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

        return _measurementTool is not MeasurementTool.None and not MeasurementTool.Erase;
    }

    private void HandleMeasurementPointerExited()
    {
        if (_measurementTool == MeasurementTool.PixelLens)
        {
            PixelLensPanel.IsVisible = false;
        }

        UpdateInteractiveCursor();
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

    private void HandlePolygonPressed(Point imagePoint, int clickCount, KeyModifiers modifiers)
    {
        Point clamped = ClampImagePoint(imagePoint);

        if (clickCount >= 2 &&
            (_measurementDraft is null ||
             _measurementDraft.Kind != MeasurementKind.PolygonRoi ||
             _measurementDraft.Points.Count < 2) &&
            TryCreateAutoOutlinedPolygonMeasurement(clamped))
        {
            return;
        }

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

    private bool TryBeginMeasurementEdit(Point controlPoint, IPointer pointer)
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
            ClampImagePoint(GetImagePointFromControl(controlPoint)),
            controlPoint,
            hit.HandleIndex,
            hit.MoveWholeMeasurement,
            hit.MoveLabel,
            GetMeasurementLabelOffset(hit.Measurement));

        pointer.Capture(RootGrid);
        _capturedPointer = pointer;
        UpdateMeasurementPresentation();
        return true;
    }

    private bool TryDeleteMeasurement(Point controlPoint)
    {
        MeasurementHit? hit = HitTestMeasurement(controlPoint);
        if (hit is null)
        {
            return false;
        }

        if (_selectedMeasurementId == hit.Measurement.Id)
        {
            SetSelectedMeasurement(null);
        }

        MeasurementDeleted?.Invoke(hit.Measurement.Id);
        UpdateMeasurementPresentation();
        return true;
    }

    private bool TrySelectMeasurement(Point controlPoint)
    {
        MeasurementHit? hit = HitTestMeasurement(controlPoint);
        if (hit is null)
        {
            return false;
        }

        SetSelectedMeasurement(hit.Measurement.Id);
        return true;
    }

    public bool TryNudgeSelectedMeasurement(Vector imageDelta)
    {
        if (_selectedMeasurementId is not Guid measurementId || SpatialMetadata is null)
        {
            return false;
        }

        StudyMeasurement? measurement = _measurements.FirstOrDefault(candidate => candidate.Id == measurementId);
        if (measurement is null || !measurement.TryProjectTo(SpatialMetadata, out Point[] imagePoints) || imagePoints.Length == 0)
        {
            return false;
        }

        Point[] nudgedPoints = imagePoints
            .Select(point => ClampImagePoint(new Point(point.X + imageDelta.X, point.Y + imageDelta.Y)))
            .ToArray();

        bool changed = false;
        for (int index = 0; index < imagePoints.Length; index++)
        {
            if (Distance(imagePoints[index], nudgedPoints[index]) > 0.01)
            {
                changed = true;
                break;
            }
        }

        if (!changed)
        {
            return false;
        }

        StudyMeasurement updated = measurement.WithAnchors(SpatialMetadata, nudgedPoints);
        MeasurementUpdated?.Invoke(updated);
        UpdateMeasurementPresentation();
        return true;
    }

    private void ApplyMeasurementEdit(Point controlPoint)
    {
        if (_measurementEditSession is null)
        {
            return;
        }

        if (_measurementEditSession.MoveLabel)
        {
            Vector labelDelta = controlPoint - _measurementEditSession.StartControlPoint;
            StudyMeasurement labelUpdated = _measurementEditSession.Measurement.WithLabelOffset(new Point(
                _measurementEditSession.InitialLabelOffset.X + labelDelta.X,
                _measurementEditSession.InitialLabelOffset.Y + labelDelta.Y));
            MeasurementUpdated?.Invoke(labelUpdated);
            UpdateMeasurementPresentation();
            return;
        }

        Point clamped = ClampImagePoint(GetImagePointFromControl(controlPoint));
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
        const double labelThreshold = 4.0;

        List<RenderedMeasurement> renderedMeasurements = GetRenderedMeasurements()
            .OrderBy(rendered => rendered.Measurement.Id == _selectedMeasurementId ? 1 : 0)
            .ToList();

        for (int index = renderedMeasurements.Count - 1; index >= 0; index--)
        {
            RenderedMeasurement rendered = renderedMeasurements[index];

            if (rendered.Label is not null && rendered.Label.Bounds.Inflate(labelThreshold).Contains(controlPoint))
            {
                return new MeasurementHit(rendered.Measurement, rendered.ImagePoints, -1, false, true);
            }

            for (int pointIndex = 0; pointIndex < rendered.ControlPoints.Length; pointIndex++)
            {
                if (Distance(rendered.ControlPoints[pointIndex], controlPoint) <= handleThreshold)
                {
                    return new MeasurementHit(rendered.Measurement, rendered.ImagePoints, pointIndex, false, false);
                }
            }

            if (IsPointOnMeasurement(rendered, controlPoint, lineThreshold))
            {
                return new MeasurementHit(rendered.Measurement, rendered.ImagePoints, -1, true, false);
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
            MeasurementKind.Annotation => DistanceToSegment(controlPoint, rendered.ControlPoints[0], rendered.ControlPoints[1]) <= threshold,
            MeasurementKind.RectangleRoi =>
                BuildRect(rendered.ControlPoints[0], rendered.ControlPoints[1]).Inflate(threshold).Contains(controlPoint),
            MeasurementKind.EllipseRoi => IsPointOnEllipseRoi(controlPoint, rendered.ControlPoints[0], rendered.ControlPoints[1], threshold),
            MeasurementKind.PolygonRoi =>
                IsPointInsidePolygon(controlPoint, rendered.ControlPoints) ||
                PolygonSegments(rendered.ControlPoints).Any(segment => DistanceToSegment(controlPoint, segment.Start, segment.End) <= threshold),
            MeasurementKind.VolumeRoi =>
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
        DrawVolumeRoiDraftOverlay();
    }

    private List<RenderedMeasurement> GetRenderedMeasurements()
    {
        List<RenderedMeasurement> renderedMeasurements = [];

        foreach (StudyMeasurement measurement in _measurements)
        {
            Point[] imagePoints;
            bool isInterpolatedVolumeSlice = false;
            if (measurement.Kind == MeasurementKind.VolumeRoi)
            {
                if (SpatialMetadata is null ||
                    !measurement.TryProjectVolumeContoursTo(SpatialMetadata, out Point[][] projectedContours, out isInterpolatedVolumeSlice) ||
                    projectedContours.Length == 0)
                {
                    continue;
                }

                imagePoints = projectedContours[0];
            }
            else if (!measurement.TryProjectTo(SpatialMetadata, out imagePoints))
            {
                continue;
            }

            Point[] controlPoints = imagePoints.Select(ImageToControlPoint).ToArray();
            bool isSelected = measurement.Id == _selectedMeasurementId;
            renderedMeasurements.Add(new RenderedMeasurement(
                measurement,
                imagePoints,
                controlPoints,
                CreateMeasurementLabel(measurement, imagePoints, controlPoints, isSelected),
                isSelected,
                isInterpolatedVolumeSlice));
        }

        return renderedMeasurements;
    }

    private void DrawRenderedMeasurement(RenderedMeasurement rendered)
    {
        IBrush stroke = new SolidColorBrush(rendered.IsSelected ? Color.Parse("#FFFFD54F") : Color.Parse("#FF35C7FF"));
        IBrush fill = new SolidColorBrush(rendered.IsSelected ? Color.Parse("#20FFD54F") : Color.Parse("#1035C7FF"));
        if (rendered.Measurement.Kind == MeasurementKind.VolumeRoi)
        {
            stroke = new SolidColorBrush(rendered.IsSelected
                ? rendered.IsInterpolatedVolumeSlice ? Color.Parse("#FFF9E27D") : Color.Parse("#FFFFD54F")
                : rendered.IsInterpolatedVolumeSlice ? Color.Parse("#A035C7FF") : Color.Parse("#FF35C7FF"));
            fill = new SolidColorBrush(rendered.IsSelected
                ? rendered.IsInterpolatedVolumeSlice ? Color.Parse("#10FFD54F") : Color.Parse("#28FFD54F")
                : rendered.IsInterpolatedVolumeSlice ? Color.Parse("#0635C7FF") : Color.Parse("#1635C7FF"));
        }

        switch (rendered.Measurement.Kind)
        {
            case MeasurementKind.Line:
                AddLine(rendered.ControlPoints[0], rendered.ControlPoints[1], stroke, 2);
                break;
            case MeasurementKind.Angle:
                AddLine(rendered.ControlPoints[0], rendered.ControlPoints[1], stroke, 2);
                AddLine(rendered.ControlPoints[1], rendered.ControlPoints[2], stroke, 2);
                break;
            case MeasurementKind.Annotation:
                AddArrow(rendered.ControlPoints[1], rendered.ControlPoints[0], stroke, 2);
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
                break;
            case MeasurementKind.EllipseRoi:
                Rect ellipseRect = BuildRect(rendered.ControlPoints[0], rendered.ControlPoints[1]);
                var ellipse = new Ellipse
                {
                    Width = Math.Max(1, ellipseRect.Width),
                    Height = Math.Max(1, ellipseRect.Height),
                    Stroke = stroke,
                    Fill = fill,
                    StrokeThickness = 2,
                };
                Canvas.SetLeft(ellipse, ellipseRect.X);
                Canvas.SetTop(ellipse, ellipseRect.Y);
                MeasurementOverlay.Children.Add(ellipse);
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
                break;
            case MeasurementKind.VolumeRoi:
                if (SpatialMetadata is not null && rendered.Measurement.TryProjectVolumeContoursTo(SpatialMetadata, out Point[][] projectedContours, out bool isInterpolatedVolumeSlice))
                {
                    foreach (Point[] contour in projectedContours)
                    {
                        var contourPolygon = new Polygon
                        {
                            Points = new Points(contour.Select(ImageToControlPoint)),
                            Stroke = stroke,
                            Fill = fill,
                            StrokeThickness = isInterpolatedVolumeSlice ? 1.35 : 2,
                        };
                        MeasurementOverlay.Children.Add(contourPolygon);
                    }
                }
                break;
        }

        if (rendered.Label is not null)
        {
            AddMeasurementLabel(rendered, rendered.Label);
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
            MeasurementKind.Line or MeasurementKind.Annotation or MeasurementKind.RectangleRoi or MeasurementKind.EllipseRoi => [_measurementDraft.Points[0], _measurementDraft.CurrentPoint],
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
        else if (_measurementDraft.Kind == MeasurementKind.Annotation && controlPoints.Length == 2)
        {
            AddArrow(controlPoints[1], controlPoints[0], stroke, 1.5);
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
        else if (_measurementDraft.Kind == MeasurementKind.EllipseRoi && controlPoints.Length == 2)
        {
            Rect rect = BuildRect(controlPoints[0], controlPoints[1]);
            var ellipse = new Ellipse
            {
                Width = Math.Max(1, rect.Width),
                Height = Math.Max(1, rect.Height),
                Stroke = stroke,
                StrokeThickness = 1.5,
            };
            Canvas.SetLeft(ellipse, rect.X);
            Canvas.SetTop(ellipse, rect.Y);
            MeasurementOverlay.Children.Add(ellipse);
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

    private void AddMeasurementLabel(RenderedMeasurement rendered, MeasurementLabel label)
    {
        Border border = CreateMeasurementLabelBorder(rendered.IsSelected, label.Text);
        Canvas.SetLeft(border, label.Bounds.X);
        Canvas.SetTop(border, label.Bounds.Y);
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

    private void AddArrow(Point start, Point tip, IBrush stroke, double thickness)
    {
        AddLine(start, tip, stroke, thickness);

        Vector direction = start - tip;
        double length = Math.Sqrt((direction.X * direction.X) + (direction.Y * direction.Y));
        if (length < 0.001)
        {
            return;
        }

        Vector unit = new(direction.X / length, direction.Y / length);
        const double arrowLength = 12.0;
        const double arrowWidth = 6.0;

        Point basePoint = new(
            tip.X + (unit.X * arrowLength),
            tip.Y + (unit.Y * arrowLength));

        Vector normal = new(-unit.Y, unit.X);
        Point left = new(
            basePoint.X + (normal.X * arrowWidth),
            basePoint.Y + (normal.Y * arrowWidth));
        Point right = new(
            basePoint.X - (normal.X * arrowWidth),
            basePoint.Y - (normal.Y * arrowWidth));

        AddLine(tip, left, stroke, thickness);
        AddLine(tip, right, stroke, thickness);
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
        return BuildRoiMeasurementText(stats, area);
    }

    private string GetEllipseRoiMeasurementText(Point[] imagePoints)
    {
        if (SpatialMetadata is null || imagePoints.Length < 2)
        {
            return string.Empty;
        }

        Rect rect = BuildRect(imagePoints[0], imagePoints[1]);
        RoiStatistics stats = CalculateEllipseStatistics(rect);
        double area = Math.PI * (rect.Width * SpatialMetadata.ColumnSpacing * 0.5) * (rect.Height * SpatialMetadata.RowSpacing * 0.5);
        return BuildRoiMeasurementText(stats, area);
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
        return BuildRoiMeasurementText(stats, area);
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

    private bool TryCollectMeasurementValues(MeasurementKind kind, Point[] imagePoints, out List<double> values, out double areaSquareMillimeters)
    {
        values = [];
        areaSquareMillimeters = 0;

        if (SpatialMetadata is null)
        {
            return false;
        }

        switch (kind)
        {
            case MeasurementKind.RectangleRoi:
            {
                Rect rect = BuildRect(imagePoints[0], imagePoints[1]);
                values = CollectRectangleValues(rect);
                areaSquareMillimeters = rect.Width * SpatialMetadata.ColumnSpacing * rect.Height * SpatialMetadata.RowSpacing;
                return values.Count > 0;
            }
            case MeasurementKind.EllipseRoi:
            {
                Rect rect = BuildRect(imagePoints[0], imagePoints[1]);
                values = CollectEllipseValues(rect);
                areaSquareMillimeters = Math.PI * (rect.Width * SpatialMetadata.ColumnSpacing * 0.5) * (rect.Height * SpatialMetadata.RowSpacing * 0.5);
                return values.Count > 0;
            }
            case MeasurementKind.PolygonRoi:
            {
                values = CollectPolygonValues(imagePoints);
                double areaPixels = 0;
                for (int index = 0; index < imagePoints.Length; index++)
                {
                    Point current = imagePoints[index];
                    Point next = imagePoints[(index + 1) % imagePoints.Length];
                    areaPixels += (current.X * next.Y) - (next.X * current.Y);
                }

                areaSquareMillimeters = Math.Abs(areaPixels) * 0.5 * SpatialMetadata.ColumnSpacing * SpatialMetadata.RowSpacing;
                return values.Count > 0;
            }
            default:
                return false;
        }
    }

    private List<double> CollectRectangleValues(Rect imageRect)
    {
        int left = Math.Clamp((int)Math.Floor(imageRect.X), 0, _imageWidth - 1);
        int top = Math.Clamp((int)Math.Floor(imageRect.Y), 0, _imageHeight - 1);
        int right = Math.Clamp((int)Math.Ceiling(imageRect.Right), 0, _imageWidth - 1);
        int bottom = Math.Clamp((int)Math.Ceiling(imageRect.Bottom), 0, _imageHeight - 1);
        return CollectValues((x, y) => x >= left && x <= right && y >= top && y <= bottom, left, top, right, bottom);
    }

    private List<double> CollectEllipseValues(Rect imageRect)
    {
        int left = Math.Clamp((int)Math.Floor(imageRect.X), 0, _imageWidth - 1);
        int top = Math.Clamp((int)Math.Floor(imageRect.Y), 0, _imageHeight - 1);
        int right = Math.Clamp((int)Math.Ceiling(imageRect.Right), 0, _imageWidth - 1);
        int bottom = Math.Clamp((int)Math.Ceiling(imageRect.Bottom), 0, _imageHeight - 1);
        double centerX = imageRect.X + (imageRect.Width * 0.5);
        double centerY = imageRect.Y + (imageRect.Height * 0.5);
        double radiusX = Math.Max(imageRect.Width * 0.5, 0.5);
        double radiusY = Math.Max(imageRect.Height * 0.5, 0.5);

        return CollectValues(
            (x, y) =>
            {
                double normalizedX = ((x + 0.5) - centerX) / radiusX;
                double normalizedY = ((y + 0.5) - centerY) / radiusY;
                return (normalizedX * normalizedX) + (normalizedY * normalizedY) <= 1.0;
            },
            left,
            top,
            right,
            bottom);
    }

    private List<double> CollectPolygonValues(Point[] imagePoints)
    {
        int left = Math.Clamp((int)Math.Floor(imagePoints.Min(point => point.X)), 0, _imageWidth - 1);
        int right = Math.Clamp((int)Math.Ceiling(imagePoints.Max(point => point.X)), 0, _imageWidth - 1);
        int top = Math.Clamp((int)Math.Floor(imagePoints.Min(point => point.Y)), 0, _imageHeight - 1);
        int bottom = Math.Clamp((int)Math.Ceiling(imagePoints.Max(point => point.Y)), 0, _imageHeight - 1);
        return CollectValues(
            (x, y) => IsPointInsidePolygon(new Point(x + 0.5, y + 0.5), imagePoints),
            left,
            top,
            right,
            bottom);
    }

    private List<double> CollectValues(Func<int, int, bool> inside, int left, int top, int right, int bottom)
    {
        var values = new List<double>();
        for (int y = top; y <= bottom; y++)
        {
            for (int x = left; x <= right; x++)
            {
                if (!inside(x, y) || !TryGetPixelValue(x, y, out double value))
                {
                    continue;
                }

                values.Add(value);
            }
        }

        return values;
    }

    private RoiDistributionDetails BuildDistributionDetails(List<double> values, double areaSquareMillimeters)
    {
        values.Sort();
        double mean = values.Average();
        double variance = values.Average(value => (value - mean) * (value - mean));
        int mid = values.Count / 2;
        double median = values.Count % 2 == 0
            ? (values[mid - 1] + values[mid]) * 0.5
            : values[mid];

        const int histogramBinCount = 32;
        int[] bins = new int[histogramBinCount];
        double min = values[0];
        double max = values[^1];
        if (Math.Abs(max - min) < 1e-6)
        {
            bins[histogramBinCount / 2] = values.Count;
        }
        else
        {
            double scale = (histogramBinCount - 1) / (max - min);
            foreach (double value in values)
            {
                int binIndex = Math.Clamp((int)Math.Round((value - min) * scale), 0, histogramBinCount - 1);
                bins[binIndex]++;
            }
        }

        string modality = (_modality ?? string.Empty).Trim().ToUpperInvariant();
        string quantityLabel = modality switch
        {
            "CT" => "HU",
            "MR" => "Intensity",
            _ => "Signal",
        };

        return new RoiDistributionDetails(
            quantityLabel,
            modality,
            values.Count,
            areaSquareMillimeters,
            mean,
            median,
            Math.Sqrt(Math.Max(variance, 0)),
            min,
            max,
            Percentile(values, 0.10),
            Percentile(values, 0.90),
            bins,
            min,
            max);
    }

    private RoiStatistics CalculateEllipseStatistics(Rect imageRect)
    {
        int left = Math.Clamp((int)Math.Floor(imageRect.X), 0, _imageWidth - 1);
        int top = Math.Clamp((int)Math.Floor(imageRect.Y), 0, _imageHeight - 1);
        int right = Math.Clamp((int)Math.Ceiling(imageRect.Right), 0, _imageWidth - 1);
        int bottom = Math.Clamp((int)Math.Ceiling(imageRect.Bottom), 0, _imageHeight - 1);

        double centerX = imageRect.X + (imageRect.Width * 0.5);
        double centerY = imageRect.Y + (imageRect.Height * 0.5);
        double radiusX = Math.Max(imageRect.Width * 0.5, 0.5);
        double radiusY = Math.Max(imageRect.Height * 0.5, 0.5);

        return CalculateStatistics(
            (x, y) =>
            {
                double normalizedX = ((x + 0.5) - centerX) / radiusX;
                double normalizedY = ((y + 0.5) - centerY) / radiusY;
                return (normalizedX * normalizedX) + (normalizedY * normalizedY) <= 1.0;
            },
            left,
            top,
            right,
            bottom);
    }

    private RoiStatistics CalculateStatistics(Func<int, int, bool> inside, int left, int top, int right, int bottom)
    {
        List<double> values = [];

        for (int y = top; y <= bottom; y++)
        {
            for (int x = left; x <= right; x++)
            {
                if (!inside(x, y) || !TryGetPixelValue(x, y, out double value))
                {
                    continue;
                }

                values.Add(value);
            }
        }

        if (values.Count == 0)
        {
            return RoiStatistics.Empty;
        }

        values.Sort();
        double mean = values.Average();
        double variance = values.Average(value => (value - mean) * (value - mean));
        int mid = values.Count / 2;
        double median = values.Count % 2 == 0
            ? (values[mid - 1] + values[mid]) * 0.5
            : values[mid];

        return new RoiStatistics(
            mean,
            median,
            Math.Sqrt(Math.Max(variance, 0)),
            values[0],
            values[^1],
            Percentile(values, 0.10),
            Percentile(values, 0.90),
            values.Count);
    }

    private bool TryGetPixelValue(int x, int y, out double value)
    {
        value = 0;

        if (x < 0 || y < 0 || x >= _imageWidth || y >= _imageHeight)
            return false;

        // Volume path: values are already rescaled
        if (_volumeSlicePixels is not null)
        {
            int idx = (y * _imageWidth) + x;
            if (idx < _volumeSlicePixels.Length)
            {
                value = _volumeSlicePixels[idx];
                return true;
            }
            return false;
        }

        if (_rawPixelData is null || _samplesPerPixel != 1)
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

    private Point ImageToControlPoint(Point imagePoint) => new(_panX + (ImageToDisplayX(imagePoint.X) * _zoomFactor), _panY + (ImageToDisplayY(imagePoint.Y) * _zoomFactor));

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

    private static bool IsPointOnEllipseRoi(Point point, Point firstCorner, Point secondCorner, double threshold)
    {
        Rect rect = BuildRect(firstCorner, secondCorner);
        if (rect.Width < 0.5 || rect.Height < 0.5)
        {
            return rect.Inflate(threshold).Contains(point);
        }

        double centerX = rect.X + (rect.Width * 0.5);
        double centerY = rect.Y + (rect.Height * 0.5);
        double radiusX = Math.Max((rect.Width * 0.5) + threshold, 0.5);
        double radiusY = Math.Max((rect.Height * 0.5) + threshold, 0.5);
        double normalizedX = (point.X - centerX) / radiusX;
        double normalizedY = (point.Y - centerY) / radiusY;
        return (normalizedX * normalizedX) + (normalizedY * normalizedY) <= 1.0;
    }

    private static Point GetPolygonCenter(Point[] points)
    {
        if (points.Length == 0)
        {
            return default;
        }

        return new(points.Average(point => point.X), points.Average(point => point.Y));
    }

    private MeasurementLabel? CreateMeasurementLabel(StudyMeasurement measurement, Point[] imagePoints, Point[] controlPoints, bool isSelected)
    {
        string text = GetMeasurementLabelText(measurement, imagePoints, controlPoints);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        Border border = CreateMeasurementLabelBorder(isSelected, text);
        border.Measure(s_unboundedMeasureSize);

        Point anchor = GetMeasurementLabelAnchor(measurement, controlPoints);
        Point offset = GetMeasurementLabelOffset(measurement);
        return new MeasurementLabel(text, new Rect(new Point(anchor.X + offset.X, anchor.Y + offset.Y), border.DesiredSize));
    }

    private string GetMeasurementLabelText(StudyMeasurement measurement, Point[] imagePoints, Point[] controlPoints) => measurement.Kind switch
    {
        MeasurementKind.Line when controlPoints.Length >= 2 => GetLineMeasurementText(imagePoints),
        MeasurementKind.Angle when controlPoints.Length >= 3 => GetAngleMeasurementText(imagePoints),
        MeasurementKind.Annotation when controlPoints.Length >= 2 => measurement.AnnotationText,
        MeasurementKind.RectangleRoi when controlPoints.Length >= 2 => ComposeMeasurementLabel(measurement, imagePoints, GetRectangleRoiMeasurementText(imagePoints)),
        MeasurementKind.EllipseRoi when controlPoints.Length >= 2 => ComposeMeasurementLabel(measurement, imagePoints, GetEllipseRoiMeasurementText(imagePoints)),
        MeasurementKind.PolygonRoi when controlPoints.Length >= 3 => ComposeMeasurementLabel(measurement, imagePoints, GetPolygonRoiMeasurementText(imagePoints)),
        MeasurementKind.VolumeRoi when controlPoints.Length >= 3 => GetVolumeRoiMeasurementText(measurement),
        _ => string.Empty,
    };

    private Point GetMeasurementLabelAnchor(StudyMeasurement measurement, Point[] controlPoints) => measurement.Kind switch
    {
        MeasurementKind.Line => GetMidPoint(controlPoints[0], controlPoints[1]),
        MeasurementKind.Angle => controlPoints[1],
        MeasurementKind.Annotation => controlPoints[1],
        MeasurementKind.RectangleRoi => BuildRect(controlPoints[0], controlPoints[1]).BottomRight,
        MeasurementKind.EllipseRoi => BuildRect(controlPoints[0], controlPoints[1]).BottomRight,
        MeasurementKind.PolygonRoi => GetPolygonCenter(controlPoints),
        MeasurementKind.VolumeRoi when SpatialMetadata is not null => ImageToControlPoint(GetVolumeRoiLabelAnchor(measurement, SpatialMetadata)),
        _ => controlPoints.Length > 0 ? controlPoints[0] : default,
    };

    private Border CreateMeasurementLabelBorder(bool isSelected, string text) => new()
    {
        Background = new SolidColorBrush(Color.Parse("#B0101010")),
        BorderBrush = new SolidColorBrush(isSelected ? Color.Parse("#FFFFD54F") : Color.Parse("#8040D8FF")),
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

    private Point GetMeasurementLabelOffset(StudyMeasurement measurement) => measurement.LabelOffset ?? s_defaultMeasurementLabelOffset;

    private string ComposeMeasurementLabel(StudyMeasurement measurement, Point[] imagePoints, string baseText)
    {
        string supplement = _measurementTextSupplementProvider?.Invoke(measurement, imagePoints)?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(baseText))
        {
            return supplement;
        }

        return string.IsNullOrWhiteSpace(supplement)
            ? baseText
            : $"{baseText}\n{supplement}";
    }

    private string BuildRoiMeasurementText(RoiStatistics stats, double areaSquareMillimeters)
    {
        if (stats.PixelCount == 0)
        {
            return string.Empty;
        }

        string modality = (_modality ?? string.Empty).Trim().ToUpperInvariant();
        string quantityLabel = modality switch
        {
            "CT" => "HU",
            "MR" => "Intensity",
            _ => "Signal",
        };

        return $"{quantityLabel} mean {stats.Mean:F1}  med {stats.Median:F1}\n{quantityLabel} σ {stats.StandardDeviation:F1}  p10/p90 {stats.Percentile10:F1}/{stats.Percentile90:F1}\n{quantityLabel} min/max {stats.Min:F1}/{stats.Max:F1}\nArea {areaSquareMillimeters:F1} mm²";
    }

    private static double Percentile(IReadOnlyList<double> sortedValues, double percentile)
    {
        if (sortedValues.Count == 0)
        {
            return 0;
        }

        double index = Math.Clamp(percentile, 0, 1) * (sortedValues.Count - 1);
        int lower = (int)Math.Floor(index);
        int upper = (int)Math.Ceiling(index);
        if (lower == upper)
        {
            return sortedValues[lower];
        }

        double fraction = index - lower;
        return sortedValues[lower] + ((sortedValues[upper] - sortedValues[lower]) * fraction);
    }

    private Point GetImagePointFromControl(Point controlPoint) => new(
        DisplayToImageX((controlPoint.X - _panX) / _zoomFactor),
        DisplayToImageY((controlPoint.Y - _panY) / _zoomFactor));

    private bool IsImagePointWithinBounds(Point imagePoint) =>
        imagePoint.X >= 0 && imagePoint.Y >= 0 && imagePoint.X < _imageWidth && imagePoint.Y < _imageHeight;

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
        Point startControlPoint,
        int handleIndex,
        bool moveWholeMeasurement,
        bool moveLabel,
        Point initialLabelOffset)
    {
        public StudyMeasurement Measurement { get; } = measurement;
        public Point[] ImagePoints { get; } = imagePoints;
        public Point StartImagePoint { get; } = startImagePoint;
        public Point StartControlPoint { get; } = startControlPoint;
        public int HandleIndex { get; } = handleIndex;
        public bool MoveWholeMeasurement { get; } = moveWholeMeasurement;
        public bool MoveLabel { get; } = moveLabel;
        public Point InitialLabelOffset { get; } = initialLabelOffset;
    }

    private sealed record MeasurementHit(StudyMeasurement Measurement, Point[] ImagePoints, int HandleIndex, bool MoveWholeMeasurement, bool MoveLabel);
    private sealed record RenderedMeasurement(StudyMeasurement Measurement, Point[] ImagePoints, Point[] ControlPoints, MeasurementLabel? Label, bool IsSelected, bool IsInterpolatedVolumeSlice = false);
    private sealed record MeasurementLabel(string Text, Rect Bounds);
    private sealed record RoiStatistics(double Mean, double Median, double StandardDeviation, double Min, double Max, double Percentile10, double Percentile90, int PixelCount)
    {
        public static RoiStatistics Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0);
    }
}
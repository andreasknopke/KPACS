using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using KPACS.Viewer.Controls;
using KPACS.Viewer.Models;
using SpatialVector3D = KPACS.Viewer.Models.Vector3D;

namespace KPACS.Viewer;

public partial class StudyViewerWindow
{
    private const double DefaultVolumeRoiPreviewYaw = -0.55;
    private const double DefaultVolumeRoiPreviewPitch = 0.38;
    private const int SavedVolumeRoiPreviewSampleCount = 40;
    private bool _volumeRoiPreviewPinned;
    private double _volumeRoiPreviewYaw = DefaultVolumeRoiPreviewYaw;
    private double _volumeRoiPreviewPitch = DefaultVolumeRoiPreviewPitch;
    private DicomViewPanel.VolumeRoiDraftPreview? _currentVolumeRoiDraftPreview;
    private DicomViewPanel.VolumeRoiDraftPreview? _lastVolumeRoiPreviewSnapshot;
    private bool _lastVolumeRoiPreviewWasDraft;

    private void RefreshVolumeRoiDraftPanel()
    {
        ViewportSlot? slot = _activeSlot;
        if (slot?.Panel is not null && slot.Panel.TryGetVolumeRoiDraftPreview(out DicomViewPanel.VolumeRoiDraftPreview preview))
        {
            ApplyVolumeRoiDraftPreview(preview);
            return;
        }

        foreach (ViewportSlot candidate in _slots.Where(candidate => candidate.Panel.TryGetVolumeRoiDraftPreview(out _)))
        {
            if (candidate.Panel.TryGetVolumeRoiDraftPreview(out preview))
            {
                ApplyVolumeRoiDraftPreview(preview);
                return;
            }
        }

        if (_selectedMeasurementId is Guid measurementId &&
            _studyMeasurements.FirstOrDefault(candidate => candidate.Id == measurementId && candidate.Kind == MeasurementKind.VolumeRoi) is { } selectedVolumeRoi &&
            TryCreateVolumeRoiMeasurementPreview(selectedVolumeRoi, out DicomViewPanel.VolumeRoiDraftPreview measurementPreview))
        {
            ApplyVolumeRoiPreview(measurementPreview, isDraft: false);
            return;
        }

        if (_volumeRoiPreviewPinned && _lastVolumeRoiPreviewSnapshot is not null)
        {
            ApplyVolumeRoiPreview(_lastVolumeRoiPreviewSnapshot, _lastVolumeRoiPreviewWasDraft);
            return;
        }

        HideVolumeRoiDraftPanel();
    }

    private void ApplyVolumeRoiDraftPreview(DicomViewPanel.VolumeRoiDraftPreview preview)
    {
        ApplyVolumeRoiPreview(preview, isDraft: true);
    }

    private void ApplyVolumeRoiPreview(DicomViewPanel.VolumeRoiDraftPreview preview, bool isDraft)
    {
        _currentVolumeRoiDraftPreview = preview;
        _lastVolumeRoiPreviewSnapshot = preview;
        _lastVolumeRoiPreviewWasDraft = isDraft;
        VolumeRoiAddButton.IsVisible = isDraft && preview.SupportsAdditiveMode;
        VolumeRoiAddButton.IsChecked = isDraft && preview.IsAdditiveModeEnabled;
        VolumeRoiDraftPinButton.IsChecked = _volumeRoiPreviewPinned;
        VolumeRoiDraftTitleText.Text = isDraft ? "3D ROI draft" : "3D ROI model";
        VolumeRoiDraftStatusText.Text = isDraft
            ? $"{preview.OrientationLabel} · {preview.ContourCount} drawn · {preview.SliceCount} mesh slices · {(preview.VolumeCubicMillimeters / 1000.0):F1} ml"
            : $"{preview.OrientationLabel} · {preview.ContourCount} source slices · {preview.SliceCount} mesh slices · {(preview.VolumeCubicMillimeters / 1000.0):F1} ml";
        UpdateVolumeRoiCorrectionControls(preview, isDraft);
        VolumeRoiDraftHintText.Text = isDraft
            ? preview.IsAdditiveModeEnabled
                ? "Add mode is on: click to draft another region on the current slice or double-click to auto-outline and merge it into the 3D ROI. Shrink/Grow still refine the latest auto-outline, arrows rotate the mesh, Enter finishes, Esc cancels."
                : "Click to place points, double-click without a drawn line to auto-outline, or double-click with a line to close a slice contour. Turn on Add to merge another region into the model, use Shrink/Grow to refine the auto-outline, scroll to another slice, rotate the mesh with arrow keys, then press Enter to finish or Esc to cancel."
            : "Selected 3D ROI model preview. Scroll through the series to highlight the current slice contour; choose another measurement to hide this panel.";
        RenderVolumeRoiDraftPreview(preview);
        VolumeRoiDraftPanel.IsVisible = true;
    }

    private void HideVolumeRoiDraftPanel()
    {
        _currentVolumeRoiDraftPreview = null;
        VolumeRoiDraftTitleText.Text = "3D ROI draft";
        VolumeRoiAddButton.IsVisible = false;
        VolumeRoiAddButton.IsChecked = false;
        VolumeRoiDraftPinButton.IsChecked = _volumeRoiPreviewPinned;
        VolumeRoiDraftCanvas.Children.Clear();
        VolumeRoiDraftStatusText.Text = string.Empty;
        VolumeRoiDraftCorrectionRow.IsVisible = false;
        VolumeRoiShrinkButton.IsEnabled = false;
        VolumeRoiGrowButton.IsEnabled = false;
        VolumeRoiCorrectionText.Text = "Sensitivity: default";
        VolumeRoiDraftHintText.Text = "Click to place points, double-click without a line to auto-outline, or double-click with a line to close a slice contour. Scroll to another slice, use arrow keys to rotate the mesh preview, then press Enter to finish or Esc to cancel.";
        VolumeRoiDraftPanel.IsVisible = false;
    }

    private void OnVolumeRoiAddClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ViewportSlot? slotWithDraft = _activeSlot?.Panel is { HasVolumeRoiDraft: true }
            ? _activeSlot
            : _slots.FirstOrDefault(candidate => candidate.Panel.HasVolumeRoiDraft);

        bool enabled = VolumeRoiAddButton.IsChecked == true;
        if (slotWithDraft?.Panel.TrySetVolumeRoiAdditiveMode(enabled) == true)
        {
            RefreshVolumeRoiDraftPanel();
            UpdateStatus();
        }
        else
        {
            VolumeRoiAddButton.IsChecked = _currentVolumeRoiDraftPreview?.IsAdditiveModeEnabled == true;
        }

        e.Handled = true;
    }

    private void UpdateVolumeRoiCorrectionControls(DicomViewPanel.VolumeRoiDraftPreview preview, bool isDraft)
    {
        bool showCorrection = isDraft && preview.SupportsAutoOutlineCorrection;
        VolumeRoiDraftCorrectionRow.IsVisible = showCorrection;
        VolumeRoiShrinkButton.IsEnabled = showCorrection;
        VolumeRoiGrowButton.IsEnabled = showCorrection;

        string levelText = preview.AutoOutlineSensitivityLevel switch
        {
            > 0 => $"grow +{preview.AutoOutlineSensitivityLevel}",
            < 0 => $"shrink {preview.AutoOutlineSensitivityLevel}",
            _ => "default"
        };

        VolumeRoiCorrectionText.Text = showCorrection
            ? $"Sensitivity: {levelText}"
            : "Sensitivity: default";
    }

    private void OnVolumeRoiShrinkClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        AdjustVolumeRoiAutoOutlineSensitivity(-1);
        e.Handled = true;
    }

    private void OnVolumeRoiGrowClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        AdjustVolumeRoiAutoOutlineSensitivity(1);
        e.Handled = true;
    }

    private void AdjustVolumeRoiAutoOutlineSensitivity(int delta)
    {
        ViewportSlot? slotWithDraft = _activeSlot?.Panel is { HasVolumeRoiDraft: true }
            ? _activeSlot
            : _slots.FirstOrDefault(candidate => candidate.Panel.HasVolumeRoiDraft);

        if (slotWithDraft?.Panel.TryAdjustVolumeRoiAutoOutlineSensitivity(delta, out _) == true)
        {
            RefreshVolumeRoiDraftPanel();
            UpdateStatus();
        }
    }

    private void OnVolumeRoiPreviewPinClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _volumeRoiPreviewPinned = VolumeRoiDraftPinButton.IsChecked == true;
        if (!_volumeRoiPreviewPinned && _currentVolumeRoiDraftPreview is null)
        {
            HideVolumeRoiDraftPanel();
        }
        else
        {
            VolumeRoiDraftPinButton.IsChecked = _volumeRoiPreviewPinned;
        }

        SaveViewerSettings();
        e.Handled = true;
    }

    private bool TryCreateVolumeRoiMeasurementPreview(StudyMeasurement measurement, out DicomViewPanel.VolumeRoiDraftPreview preview)
    {
        preview = default!;
        if (measurement.VolumeContours is null || measurement.VolumeContours.Length == 0)
        {
            return false;
        }

        ViewportSlot? slot = _activeSlot is not null && SlotContainsMeasurementSource(_activeSlot, measurement)
            ? _activeSlot
            : FindSlotForMeasurement(measurement);
        if (slot?.Panel is null)
        {
            return false;
        }

        DicomSpatialMetadata? metadata = slot.CurrentSpatialMetadata;
        double currentPlanePosition = metadata is not null &&
            !string.IsNullOrWhiteSpace(measurement.FrameOfReferenceUid) &&
            string.Equals(measurement.FrameOfReferenceUid, metadata.FrameOfReferenceUid, StringComparison.Ordinal)
                ? metadata.Origin.Dot(metadata.Normal)
                : measurement.VolumeContours[0].PlanePosition;

        List<DicomViewPanel.VolumeRoiDraftPreviewContour> contours = BuildMeasurementVolumeRoiPreviewContours(measurement.VolumeContours, currentPlanePosition);
        if (contours.Count == 0)
        {
            return false;
        }

        preview = new DicomViewPanel.VolumeRoiDraftPreview(
            slot.Panel.OrientationLabel,
            measurement.VolumeContours.Count(contour => contour.IsClosed && contour.Anchors.Length >= 3),
            contours.Count(contour => contour.IsClosed),
            EstimateMeasurementVolumeCubicMillimeters(measurement.VolumeContours),
            measurement.VolumeContours.Min(contour => contour.PlanePosition),
            currentPlanePosition,
            contours);
        return true;
    }

    private static List<DicomViewPanel.VolumeRoiDraftPreviewContour> BuildMeasurementVolumeRoiPreviewContours(
        IEnumerable<VolumeRoiContour> sourceContours,
        double currentPlanePosition)
    {
        VolumeRoiContour[] contours = sourceContours
            .Where(contour => contour.Anchors.Any(anchor => anchor.PatientPoint is not null))
            .ToArray();
        if (contours.Length == 0)
        {
            return [];
        }

        List<DicomViewPanel.VolumeRoiDraftPreviewContour> previewContours = [];
        foreach (IGrouping<int, VolumeRoiContour> componentGroup in contours.GroupBy(contour => contour.ComponentId).OrderBy(group => group.Key))
        {
            List<(VolumeRoiContour Source, SpatialVector3D[] Points)> closedContours = [];
            foreach (VolumeRoiContour contour in componentGroup
                .Where(contour => contour.IsClosed && contour.Anchors.Length >= 3)
                .OrderBy(contour => contour.PlanePosition))
            {
                SpatialVector3D[] resampled = ResampleMeasurementContour(contour, SavedVolumeRoiPreviewSampleCount);
                if (resampled.Length < 3)
                {
                    continue;
                }

                if (closedContours.Count > 0)
                {
                    resampled = AlignMeasurementContourPoints(closedContours[^1].Points, resampled);
                }

                closedContours.Add((contour, resampled));
            }

            for (int index = 0; index < closedContours.Count; index++)
            {
                (VolumeRoiContour contour, SpatialVector3D[] points) = closedContours[index];
                previewContours.Add(new DicomViewPanel.VolumeRoiDraftPreviewContour(
                    points,
                    contour.PlanePosition,
                    IsCurrentMeasurementPlane(contour.PlanePosition, currentPlanePosition),
                    true,
                    false));

                if (index >= closedContours.Count - 1)
                {
                    continue;
                }

                (VolumeRoiContour nextContour, SpatialVector3D[] nextPoints) = closedContours[index + 1];
                int sectionCount = GetMeasurementInterpolationSectionCount(Math.Abs(nextContour.PlanePosition - contour.PlanePosition));
                for (int section = 1; section < sectionCount; section++)
                {
                    double t = section / (double)sectionCount;
                    double planePosition = Lerp(contour.PlanePosition, nextContour.PlanePosition, t);
                    previewContours.Add(new DicomViewPanel.VolumeRoiDraftPreviewContour(
                        InterpolateMeasurementContourPoints(points, nextPoints, t),
                        planePosition,
                        IsCurrentMeasurementPlane(planePosition, currentPlanePosition),
                        true,
                        true));
                }
            }
        }

        return previewContours
            .OrderBy(contour => contour.PlanePosition)
            .ThenBy(contour => contour.IsInterpolated)
            .ToList();
    }

    private static bool IsCurrentMeasurementPlane(double planePosition, double currentPlanePosition) => Math.Abs(planePosition - currentPlanePosition) <= 0.25;

    private static SpatialVector3D[] ResampleMeasurementContour(VolumeRoiContour contour, int sampleCount)
    {
        SpatialVector3D[] points = contour.Anchors
            .Where(anchor => anchor.PatientPoint is not null)
            .Select(anchor => anchor.PatientPoint!.Value)
            .ToArray();
        if (points.Length < 3 || sampleCount < 3)
        {
            return points;
        }

        double[] cumulative = new double[points.Length + 1];
        for (int index = 0; index < points.Length; index++)
        {
            cumulative[index + 1] = cumulative[index] + GetMeasurementDistance(points[index], points[(index + 1) % points.Length]);
        }

        double totalLength = cumulative[^1];
        if (totalLength <= double.Epsilon)
        {
            return points;
        }

        SpatialVector3D[] result = new SpatialVector3D[sampleCount];
        double step = totalLength / sampleCount;
        int segmentIndex = 0;
        for (int sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
        {
            double target = sampleIndex * step;
            while (segmentIndex < points.Length - 1 && cumulative[segmentIndex + 1] < target)
            {
                segmentIndex++;
            }

            double segmentStart = cumulative[segmentIndex];
            double segmentEnd = cumulative[segmentIndex + 1];
            double segmentLength = Math.Max(double.Epsilon, segmentEnd - segmentStart);
            double t = (target - segmentStart) / segmentLength;
            result[sampleIndex] = Lerp(points[segmentIndex], points[(segmentIndex + 1) % points.Length], t);
        }

        if (GetMeasurementSignedContourArea(result, contour.PlaneOrigin, contour.RowDirection, contour.ColumnDirection) < 0)
        {
            Array.Reverse(result);
        }

        return result;
    }

    private static SpatialVector3D[] AlignMeasurementContourPoints(SpatialVector3D[] reference, SpatialVector3D[] candidate)
    {
        if (reference.Length == 0 || candidate.Length == 0 || reference.Length != candidate.Length)
        {
            return candidate;
        }

        int bestShift = 0;
        double bestCost = double.MaxValue;
        for (int shift = 0; shift < candidate.Length; shift++)
        {
            double cost = 0;
            for (int index = 0; index < reference.Length; index++)
            {
                SpatialVector3D delta = reference[index] - candidate[(index + shift) % candidate.Length];
                cost += delta.Dot(delta);
            }

            if (cost < bestCost)
            {
                bestCost = cost;
                bestShift = shift;
            }
        }

        if (bestShift == 0)
        {
            return candidate;
        }

        SpatialVector3D[] aligned = new SpatialVector3D[candidate.Length];
        for (int index = 0; index < candidate.Length; index++)
        {
            aligned[index] = candidate[(index + bestShift) % candidate.Length];
        }

        return aligned;
    }

    private static SpatialVector3D[] InterpolateMeasurementContourPoints(SpatialVector3D[] first, SpatialVector3D[] second, double t)
    {
        int count = Math.Min(first.Length, second.Length);
        SpatialVector3D[] points = new SpatialVector3D[count];
        for (int index = 0; index < count; index++)
        {
            points[index] = Lerp(first[index], second[index], t);
        }

        return points;
    }

    private static int GetMeasurementInterpolationSectionCount(double gapMillimeters)
    {
        if (gapMillimeters <= 2)
        {
            return 1;
        }

        return Math.Clamp((int)Math.Round(gapMillimeters / 3.0, MidpointRounding.AwayFromZero), 1, 8);
    }

    private static double EstimateMeasurementVolumeCubicMillimeters(IEnumerable<VolumeRoiContour> sourceContours)
    {
        double volume = 0;
        foreach (VolumeRoiContour[] contours in sourceContours
            .Where(contour => contour.IsClosed && contour.Anchors.Length >= 3)
            .GroupBy(contour => contour.ComponentId)
            .Select(group => group.OrderBy(contour => contour.PlanePosition).ToArray()))
        {
            if (contours.Length < 2)
            {
                continue;
            }

            for (int index = 0; index < contours.Length - 1; index++)
            {
                double areaA = Math.Abs(GetMeasurementSignedContourArea(
                    contours[index].Anchors.Where(anchor => anchor.PatientPoint is not null).Select(anchor => anchor.PatientPoint!.Value).ToArray(),
                    contours[index].PlaneOrigin,
                    contours[index].RowDirection,
                    contours[index].ColumnDirection));
                double areaB = Math.Abs(GetMeasurementSignedContourArea(
                    contours[index + 1].Anchors.Where(anchor => anchor.PatientPoint is not null).Select(anchor => anchor.PatientPoint!.Value).ToArray(),
                    contours[index + 1].PlaneOrigin,
                    contours[index + 1].RowDirection,
                    contours[index + 1].ColumnDirection));
                double thickness = Math.Abs(contours[index + 1].PlanePosition - contours[index].PlanePosition);
                volume += ((areaA + areaB) * 0.5) * thickness;
            }
        }

        return volume;
    }

    private static double GetMeasurementDistance(SpatialVector3D first, SpatialVector3D second) => (first - second).Length;

    private static double GetMeasurementSignedContourArea(
        IReadOnlyList<SpatialVector3D> contour,
        SpatialVector3D planeOrigin,
        SpatialVector3D rowDirection,
        SpatialVector3D columnDirection)
    {
        if (contour.Count < 3)
        {
            return 0;
        }

        double area = 0;
        for (int index = 0; index < contour.Count; index++)
        {
            SpatialVector3D currentRelative = contour[index] - planeOrigin;
            SpatialVector3D nextRelative = contour[(index + 1) % contour.Count] - planeOrigin;
            double currentX = currentRelative.Dot(rowDirection);
            double currentY = currentRelative.Dot(columnDirection);
            double nextX = nextRelative.Dot(rowDirection);
            double nextY = nextRelative.Dot(columnDirection);
            area += (currentX * nextY) - (nextX * currentY);
        }

        return area * 0.5;
    }

    private static double Lerp(double first, double second, double t) => first + ((second - first) * t);

    private static SpatialVector3D Lerp(SpatialVector3D first, SpatialVector3D second, double t) => first + ((second - first) * t);

    private bool TryRotateVolumeRoiDraftPreview(Key key, bool accelerate)
    {
        if (_currentVolumeRoiDraftPreview is null || !VolumeRoiDraftPanel.IsVisible)
        {
            return false;
        }

        double delta = accelerate ? 0.18 : 0.09;
        switch (key)
        {
            case Key.Left:
                _volumeRoiPreviewYaw -= delta;
                break;
            case Key.Right:
                _volumeRoiPreviewYaw += delta;
                break;
            case Key.Up:
                _volumeRoiPreviewPitch = Math.Clamp(_volumeRoiPreviewPitch - delta, -1.25, 1.25);
                break;
            case Key.Down:
                _volumeRoiPreviewPitch = Math.Clamp(_volumeRoiPreviewPitch + delta, -1.25, 1.25);
                break;
            default:
                return false;
        }

        RenderVolumeRoiDraftPreview(_currentVolumeRoiDraftPreview);
        return true;
    }

    private void RenderVolumeRoiDraftPreview(DicomViewPanel.VolumeRoiDraftPreview preview)
    {
        VolumeRoiDraftCanvas.Children.Clear();
        if (preview.Contours.Count == 0)
        {
            return;
        }

        List<SpatialVector3D> allPoints = preview.Contours.SelectMany(contour => contour.PatientPoints).ToList();
        if (allPoints.Count == 0)
        {
            return;
        }

        SpatialVector3D center = new(
            allPoints.Average(point => point.X),
            allPoints.Average(point => point.Y),
            allPoints.Average(point => point.Z));

        List<PreviewContourGeometry> contourGeometry = preview.Contours
            .Select(contour => new PreviewContourGeometry(
                contour,
                contour.PatientPoints.Select(point => RotateVolumeRoiPoint(point - center)).ToArray()))
            .Where(geometry => geometry.Points.Length > 0)
            .OrderBy(geometry => geometry.Contour.PlanePosition)
            .ToList();
        if (contourGeometry.Count == 0)
        {
            return;
        }

        List<PreviewTriangle> triangles = BuildPreviewTriangles(contourGeometry);
        IEnumerable<Point> projectedPoints = contourGeometry.SelectMany(contour => contour.Points.Select(ProjectVolumeRoiPoint));
        double minX = projectedPoints.Min(point => point.X);
        double maxX = projectedPoints.Max(point => point.X);
        double minY = projectedPoints.Min(point => point.Y);
        double maxY = projectedPoints.Max(point => point.Y);
        double width = Math.Max(1, maxX - minX);
        double height = Math.Max(1, maxY - minY);
        double canvasWidth = VolumeRoiDraftCanvas.Width;
        double canvasHeight = VolumeRoiDraftCanvas.Height;
        double scale = Math.Min((canvasWidth - 24) / width, (canvasHeight - 24) / height);
        scale = double.IsFinite(scale) && scale > 0 ? scale : 1;
        double offsetX = 12 + (((canvasWidth - 24) - (width * scale)) * 0.5);
        double offsetY = 12 + (((canvasHeight - 24) - (height * scale)) * 0.5);

        foreach (PreviewTriangle triangle in triangles.OrderBy(triangle => triangle.Depth))
        {
            Point[] transformed =
            [
                TransformPreviewPoint(ProjectVolumeRoiPoint(triangle.A), minX, minY, scale, offsetX, offsetY),
                TransformPreviewPoint(ProjectVolumeRoiPoint(triangle.B), minX, minY, scale, offsetX, offsetY),
                TransformPreviewPoint(ProjectVolumeRoiPoint(triangle.C), minX, minY, scale, offsetX, offsetY),
            ];

            Color fillColor = GetVolumeRoiPreviewColor(preview.FirstPlanePosition, triangle.PlanePosition, triangle.IsCurrentSlice, triangle.IsInterpolated);
            fillColor = ApplyPreviewLighting(fillColor, triangle.Shading);

            var polygon = new Polygon
            {
                Points = new Points(transformed),
                Fill = new SolidColorBrush(fillColor),
                Stroke = new SolidColorBrush(Color.FromArgb(110, fillColor.R, fillColor.G, fillColor.B)),
                StrokeThickness = triangle.IsCurrentSlice ? 1.1 : 0.7,
            };
            VolumeRoiDraftCanvas.Children.Add(polygon);
        }

        foreach (PreviewContourGeometry contour in contourGeometry)
        {
            Color strokeColor = GetVolumeRoiPreviewColor(preview.FirstPlanePosition, contour.Contour.PlanePosition, contour.Contour.IsCurrentSlice, contour.Contour.IsInterpolated);
            var polyline = new Polyline
            {
                Points = new Points(contour.Points.Select(point =>
                    TransformPreviewPoint(ProjectVolumeRoiPoint(point), minX, minY, scale, offsetX, offsetY))),
                Stroke = new SolidColorBrush(Color.FromArgb(
                    contour.Contour.IsInterpolated ? (byte)80 : (byte)180,
                    strokeColor.R,
                    strokeColor.G,
                    strokeColor.B)),
                StrokeThickness = contour.Contour.IsCurrentSlice ? 2.2 : contour.Contour.IsInterpolated ? 1.0 : 1.4,
            };
            VolumeRoiDraftCanvas.Children.Add(polyline);

            if (contour.Contour.IsClosed && polyline.Points.Count >= 3)
            {
                VolumeRoiDraftCanvas.Children.Add(new Line
                {
                    StartPoint = polyline.Points[^1],
                    EndPoint = polyline.Points[0],
                    Stroke = polyline.Stroke,
                    StrokeThickness = polyline.StrokeThickness,
                });
            }
        }
    }

    private SpatialVector3D RotateVolumeRoiPoint(SpatialVector3D point)
    {
        double cosYaw = Math.Cos(_volumeRoiPreviewYaw);
        double sinYaw = Math.Sin(_volumeRoiPreviewYaw);
        double cosPitch = Math.Cos(_volumeRoiPreviewPitch);
        double sinPitch = Math.Sin(_volumeRoiPreviewPitch);

        double x1 = (point.X * cosYaw) - (point.Z * sinYaw);
        double z1 = (point.X * sinYaw) + (point.Z * cosYaw);
        double y1 = point.Y;
        double y2 = (y1 * cosPitch) - (z1 * sinPitch);
        double z2 = (y1 * sinPitch) + (z1 * cosPitch);

        return new SpatialVector3D(x1, y2, z2);
    }

    private static Point ProjectVolumeRoiPoint(SpatialVector3D point) => new(point.X, point.Y);

    private static Point TransformPreviewPoint(Point point, double minX, double minY, double scale, double offsetX, double offsetY) =>
        new(
            offsetX + ((point.X - minX) * scale),
            offsetY + ((point.Y - minY) * scale));

    private static List<PreviewTriangle> BuildPreviewTriangles(IReadOnlyList<PreviewContourGeometry> contours)
    {
        List<PreviewTriangle> triangles = [];

        List<PreviewContourGeometry> closedContours = contours
            .Where(contour => contour.Contour.IsClosed && contour.Points.Length >= 3)
            .OrderBy(contour => contour.Contour.PlanePosition)
            .ToList();

        for (int contourIndex = 0; contourIndex < closedContours.Count - 1; contourIndex++)
        {
            PreviewContourGeometry first = closedContours[contourIndex];
            PreviewContourGeometry second = closedContours[contourIndex + 1];
            int pointCount = Math.Min(first.Points.Length, second.Points.Length);
            for (int pointIndex = 0; pointIndex < pointCount; pointIndex++)
            {
                SpatialVector3D a0 = first.Points[pointIndex];
                SpatialVector3D a1 = first.Points[(pointIndex + 1) % pointCount];
                SpatialVector3D b0 = second.Points[pointIndex];
                SpatialVector3D b1 = second.Points[(pointIndex + 1) % pointCount];
                double planePosition = (first.Contour.PlanePosition + second.Contour.PlanePosition) * 0.5;
                bool isCurrentSlice = first.Contour.IsCurrentSlice || second.Contour.IsCurrentSlice;
                bool isInterpolated = first.Contour.IsInterpolated || second.Contour.IsInterpolated;

                triangles.Add(CreatePreviewTriangle(a0, a1, b1, planePosition, isCurrentSlice, isInterpolated));
                triangles.Add(CreatePreviewTriangle(a0, b1, b0, planePosition, isCurrentSlice, isInterpolated));
            }
        }

        if (closedContours.Count > 0)
        {
            AddContourCapTriangles(triangles, closedContours[0]);
            if (closedContours.Count > 1)
            {
                AddContourCapTriangles(triangles, closedContours[^1]);
            }
        }

        return triangles;
    }

    private static void AddContourCapTriangles(List<PreviewTriangle> triangles, PreviewContourGeometry contour)
    {
        if (contour.Points.Length < 3)
        {
            return;
        }

        SpatialVector3D center = new(
            contour.Points.Average(point => point.X),
            contour.Points.Average(point => point.Y),
            contour.Points.Average(point => point.Z));

        for (int index = 0; index < contour.Points.Length; index++)
        {
            SpatialVector3D first = contour.Points[index];
            SpatialVector3D second = contour.Points[(index + 1) % contour.Points.Length];
            triangles.Add(CreatePreviewTriangle(center, first, second, contour.Contour.PlanePosition, contour.Contour.IsCurrentSlice, contour.Contour.IsInterpolated));
        }
    }

    private static PreviewTriangle CreatePreviewTriangle(SpatialVector3D a, SpatialVector3D b, SpatialVector3D c, double planePosition, bool isCurrentSlice, bool isInterpolated)
    {
        SpatialVector3D normal = (b - a).Cross(c - a).Normalize();
        double shading = Math.Clamp(0.45 + (0.4 * Math.Abs(normal.Z)) + (0.15 * Math.Max(0, normal.Y)), 0.2, 1.0);
        return new PreviewTriangle(a, b, c, planePosition, isCurrentSlice, isInterpolated, (a.Z + b.Z + c.Z) / 3.0, shading);
    }

    private static Color GetVolumeRoiPreviewColor(double firstPlanePosition, double planePosition, bool isCurrentSlice, bool isInterpolated)
    {
        Color color = Math.Abs(planePosition - firstPlanePosition) <= 0.25
            ? Color.Parse("#FFFFD54F")
            : planePosition > firstPlanePosition
                ? Color.Parse("#FFFF8A8A")
                : Color.Parse("#FF7FB7FF");

        byte alpha = isCurrentSlice
            ? (byte)220
            : isInterpolated
                ? (byte)88
                : (byte)145;

        return Color.FromArgb(alpha, color.R, color.G, color.B);
    }

    private static Color ApplyPreviewLighting(Color color, double factor)
    {
        factor = Math.Clamp(factor, 0, 1.25);
        return Color.FromArgb(
            color.A,
            (byte)Math.Clamp(Math.Round(color.R * factor), 0, 255),
            (byte)Math.Clamp(Math.Round(color.G * factor), 0, 255),
            (byte)Math.Clamp(Math.Round(color.B * factor), 0, 255));
    }

    private sealed record PreviewContourGeometry(
        DicomViewPanel.VolumeRoiDraftPreviewContour Contour,
        SpatialVector3D[] Points);

    private sealed record PreviewTriangle(
        SpatialVector3D A,
        SpatialVector3D B,
        SpatialVector3D C,
        double PlanePosition,
        bool IsCurrentSlice,
        bool IsInterpolated,
        double Depth,
        double Shading);
}

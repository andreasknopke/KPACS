using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using KPACS.Viewer.Controls;
using KPACS.Viewer.Models;

namespace KPACS.Viewer;

public partial class StudyViewerWindow
{
    private const int PolygonAutoOutlineMinSensitivityLevel = -6;
    private const int PolygonAutoOutlineMaxSensitivityLevel = 6;
    private Point _measurementInsightOffset;
    private bool _measurementInsightPinned;
    private bool _measurementInsightCollapsed;
    private MeasurementInsightSnapshot? _lastMeasurementInsightSnapshot;
    private IPointer? _measurementInsightDragPointer;
    private Point _measurementInsightDragStart;
    private Point _measurementInsightDragStartOffset;

    private void RefreshMeasurementInsightPanel()
    {
        StudyMeasurement? measurement = GetSelectedMeasurement();
        if (measurement is null || !IsInsightRelevantMeasurement(measurement))
        {
            if (_measurementInsightPinned && _lastMeasurementInsightSnapshot is not null)
            {
                ApplyMeasurementInsightSnapshot(_lastMeasurementInsightSnapshot);
                return;
            }

            HideMeasurementInsightPanel();
            return;
        }

        if (!TryResolveMeasurementInsightContext(measurement, out ViewportSlot? slot, out DicomViewPanel.RoiDistributionDetails distribution))
        {
            if (_measurementInsightPinned && _lastMeasurementInsightSnapshot is not null)
            {
                ApplyMeasurementInsightSnapshot(_lastMeasurementInsightSnapshot);
                return;
            }

            HideMeasurementInsightPanel();
            return;
        }

        string supplement = GetMeasurementTextSupplement(measurement, []) ?? string.Empty;
        bool supportsCorrection = TryGetPolygonAutoOutlineState(measurement, out PolygonAutoOutlineState? autoOutlineState);
        MeasurementInsightSnapshot snapshot = new(
            measurement.Id,
            BuildMeasurementInsightTitle(measurement, distribution),
            BuildMeasurementInsightSeriesText(slot),
            $"{distribution.QuantityLabel} mean {distribution.Mean:F1}  med {distribution.Median:F1}  σ {distribution.StandardDeviation:F1}",
            $"p10/p90 {distribution.Percentile10:F1}/{distribution.Percentile90:F1}   min/max {distribution.Minimum:F1}/{distribution.Maximum:F1}   px {distribution.PixelCount}   area {distribution.AreaSquareMillimeters:F1} mm²",
            supplement,
            supportsCorrection,
            autoOutlineState?.SensitivityLevel ?? 0,
            distribution);

        _lastMeasurementInsightSnapshot = snapshot;
        ApplyMeasurementInsightSnapshot(snapshot);
    }

    private void ApplyMeasurementInsightSnapshot(MeasurementInsightSnapshot snapshot)
    {
        MeasurementInsightTitleText.Text = snapshot.Title;
        MeasurementInsightSeriesText.Text = snapshot.SeriesText;
        MeasurementInsightStatsText.Text = snapshot.StatsText;
        MeasurementInsightRangeText.Text = snapshot.RangeText;
        MeasurementInsightSupplementText.Text = snapshot.SupplementText;
        MeasurementInsightSupplementText.IsVisible = !string.IsNullOrWhiteSpace(snapshot.SupplementText);
        UpdateMeasurementInsightCorrectionControls(snapshot);
        MeasurementInsightPinButton.IsChecked = _measurementInsightPinned;
        MeasurementInsightCollapseButton.IsChecked = _measurementInsightCollapsed;
        UpdateMeasurementInsightButtonIcons();
        MeasurementInsightBodyPanel.IsVisible = !_measurementInsightCollapsed;
        MeasurementInsightAxisMinText.Text = snapshot.Distribution.HistogramMinValue.ToString("F1");
        MeasurementInsightAxisMaxText.Text = snapshot.Distribution.HistogramMaxValue.ToString("F1");
        RenderMeasurementHistogram(snapshot.Distribution);
        MeasurementInsightPanel.IsVisible = true;
        ApplyMeasurementInsightPanelOffset();
    }

    private bool TryResolveMeasurementInsightContext(StudyMeasurement measurement, out ViewportSlot? resolvedSlot, out DicomViewPanel.RoiDistributionDetails distribution)
    {
        distribution = default!;
        resolvedSlot = null;

        IEnumerable<ViewportSlot> candidateSlots = _slots
            .Where(slot => slot.Panel.IsImageLoaded)
            .OrderByDescending(slot => ReferenceEquals(slot, _activeSlot));

        foreach (ViewportSlot slot in candidateSlots)
        {
            if (slot.Panel.TryGetMeasurementDistribution(measurement, out distribution))
            {
                resolvedSlot = slot;
                return true;
            }
        }

        return false;
    }

    private static string BuildMeasurementInsightTitle(StudyMeasurement measurement, DicomViewPanel.RoiDistributionDetails distribution)
    {
        string roiLabel = measurement.Kind switch
        {
            MeasurementKind.RectangleRoi => "Rectangle ROI",
            MeasurementKind.EllipseRoi => "Ellipse ROI",
            MeasurementKind.PolygonRoi => "Polygon ROI",
            _ => "ROI",
        };

        return string.IsNullOrWhiteSpace(distribution.Modality)
            ? $"{roiLabel} distribution"
            : $"{roiLabel} distribution · {distribution.Modality}";
    }

    private static string BuildMeasurementInsightSeriesText(ViewportSlot? slot)
    {
        if (slot?.Series is null)
        {
            return string.Empty;
        }

        string description = slot.Series.SeriesDescription?.Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(description)
            ? $"Series {slot.Series.SeriesNumber}"
            : $"Series {slot.Series.SeriesNumber} · {description}";
    }

    private void HideMeasurementInsightPanel()
    {
        if (_measurementInsightPinned && _lastMeasurementInsightSnapshot is not null)
        {
            ApplyMeasurementInsightSnapshot(_lastMeasurementInsightSnapshot);
            return;
        }

        MeasurementInsightPanel.IsVisible = false;
        MeasurementInsightHistogramCanvas.Children.Clear();
        MeasurementInsightTitleText.Text = string.Empty;
        MeasurementInsightSeriesText.Text = string.Empty;
        MeasurementInsightStatsText.Text = string.Empty;
        MeasurementInsightRangeText.Text = string.Empty;
        MeasurementInsightSupplementText.Text = string.Empty;
        MeasurementInsightSupplementText.IsVisible = false;
        MeasurementInsightCorrectionRow.IsVisible = false;
        MeasurementInsightShrinkButton.IsEnabled = false;
        MeasurementInsightGrowButton.IsEnabled = false;
        MeasurementInsightCorrectionText.Text = "Sensitivity: default";
        MeasurementInsightAxisMinText.Text = string.Empty;
        MeasurementInsightAxisMaxText.Text = string.Empty;
    }

    private bool TryGetPolygonAutoOutlineState(StudyMeasurement measurement, out PolygonAutoOutlineState? state)
    {
        state = null;
        return measurement.Kind == MeasurementKind.PolygonRoi &&
            _polygonAutoOutlineStates.TryGetValue(measurement.Id, out state);
    }

    private void UpdateMeasurementInsightCorrectionControls(MeasurementInsightSnapshot snapshot)
    {
        bool enabled = snapshot.SupportsAutoOutlineCorrection && _selectedMeasurementId == snapshot.MeasurementId;
        MeasurementInsightCorrectionRow.IsVisible = snapshot.SupportsAutoOutlineCorrection;
        MeasurementInsightShrinkButton.IsEnabled = enabled;
        MeasurementInsightGrowButton.IsEnabled = enabled;

        string levelText = snapshot.AutoOutlineSensitivityLevel switch
        {
            > 0 => $"grow +{snapshot.AutoOutlineSensitivityLevel}",
            < 0 => $"shrink {snapshot.AutoOutlineSensitivityLevel}",
            _ => "default"
        };

        MeasurementInsightCorrectionText.Text = snapshot.SupportsAutoOutlineCorrection
            ? $"Sensitivity: {levelText}"
            : "Sensitivity: default";
    }

    private void OnMeasurementInsightShrinkClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        AdjustSelectedPolygonAutoOutlineSensitivity(-1);
        e.Handled = true;
    }

    private void OnMeasurementInsightGrowClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        AdjustSelectedPolygonAutoOutlineSensitivity(1);
        e.Handled = true;
    }

    private void AdjustSelectedPolygonAutoOutlineSensitivity(int delta)
    {
        StudyMeasurement? measurement = GetSelectedMeasurement();
        if (measurement is null || !TryGetPolygonAutoOutlineState(measurement, out PolygonAutoOutlineState? state))
        {
            return;
        }

        PolygonAutoOutlineState autoOutlineState = state!;

        int nextLevel = Math.Clamp(autoOutlineState.SensitivityLevel + delta, PolygonAutoOutlineMinSensitivityLevel, PolygonAutoOutlineMaxSensitivityLevel);
        if (nextLevel == autoOutlineState.SensitivityLevel)
        {
            return;
        }

        if (!TryResolveMeasurementInsightContext(measurement, out ViewportSlot? slot, out _ ) || slot?.Panel is null)
        {
            return;
        }

        if (!slot.Panel.TryRefineAutoOutlinedPolygonMeasurement(measurement, autoOutlineState.SeedPoint, nextLevel, out StudyMeasurement updatedMeasurement))
        {
            return;
        }

        _polygonAutoOutlineStates[measurement.Id] = autoOutlineState with { SensitivityLevel = nextLevel };
        _isApplyingPolygonAutoOutlineCorrection = true;
        try
        {
            OnPanelMeasurementUpdated(updatedMeasurement);
        }
        finally
        {
            _isApplyingPolygonAutoOutlineCorrection = false;
        }

        UpdateStatus();
    }

    private void UpdateMeasurementInsightButtonIcons()
    {
        MeasurementInsightPinButton.Content = CreatePinPanelIcon();
        MeasurementInsightCollapseButton.Content = _measurementInsightCollapsed
            ? CreateChevronRightIcon()
            : CreateChevronDownIcon();
    }

    private void OnMeasurementInsightHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!MeasurementInsightPanel.IsVisible || !e.GetCurrentPoint(MeasurementInsightDragHandle).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _measurementInsightDragPointer = e.Pointer;
        _measurementInsightDragPointer.Capture(MeasurementInsightDragHandle);
        _measurementInsightDragStart = e.GetPosition(ViewerContentHost);
        _measurementInsightDragStartOffset = _measurementInsightOffset;
        e.Handled = true;
    }

    private void OnMeasurementInsightHeaderPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!ReferenceEquals(_measurementInsightDragPointer, e.Pointer))
        {
            return;
        }

        Point current = e.GetPosition(ViewerContentHost);
        Vector delta = current - _measurementInsightDragStart;
        _measurementInsightOffset = new Point(
            _measurementInsightDragStartOffset.X + delta.X,
            _measurementInsightDragStartOffset.Y + delta.Y);
        ApplyMeasurementInsightPanelOffset();
        e.Handled = true;
    }

    private void OnMeasurementInsightHeaderPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!ReferenceEquals(_measurementInsightDragPointer, e.Pointer))
        {
            return;
        }

        _measurementInsightDragPointer.Capture(null);
        _measurementInsightDragPointer = null;
        ApplyMeasurementInsightPanelOffset();
        SaveViewerSettings();
        e.Handled = true;
    }

    private void ApplyMeasurementInsightPanelOffset()
    {
        if (MeasurementInsightPanel is null || ViewerContentHost is null)
        {
            return;
        }

        TranslateTransform transform = EnsureMeasurementInsightPanelTransform();

        double panelWidth = MeasurementInsightPanel.Bounds.Width;
        double panelHeight = MeasurementInsightPanel.Bounds.Height;
        double hostWidth = ViewerContentHost.Bounds.Width;
        double hostHeight = ViewerContentHost.Bounds.Height;
        Thickness margin = MeasurementInsightPanel.Margin;

        if (hostWidth <= 0 || hostHeight <= 0 || panelWidth <= 0 || panelHeight <= 0)
        {
            transform.X = _measurementInsightOffset.X;
            transform.Y = _measurementInsightOffset.Y;
            return;
        }

        double defaultLeft = Math.Max(0, hostWidth - panelWidth - margin.Right);
        double defaultTop = Math.Max(0, hostHeight - panelHeight - margin.Bottom);
        double clampedX = Math.Clamp(_measurementInsightOffset.X, -defaultLeft, 0);
        double clampedY = Math.Clamp(_measurementInsightOffset.Y, -defaultTop, 0);
        _measurementInsightOffset = new Point(clampedX, clampedY);
        transform.X = clampedX;
        transform.Y = clampedY;
    }

    private TranslateTransform EnsureMeasurementInsightPanelTransform()
    {
        if (MeasurementInsightPanel.RenderTransform is TranslateTransform transform)
        {
            return transform;
        }

        transform = new TranslateTransform();
        MeasurementInsightPanel.RenderTransform = transform;
        return transform;
    }

    private void RenderMeasurementHistogram(DicomViewPanel.RoiDistributionDetails distribution)
    {
        MeasurementInsightHistogramCanvas.Children.Clear();

        IReadOnlyList<int> bins = distribution.HistogramBins;
        if (bins.Count == 0)
        {
            return;
        }

        double width = MeasurementInsightHistogramCanvas.Width;
        double height = MeasurementInsightHistogramCanvas.Height;
        double maxCount = Math.Max(1, bins.Max());
        double barWidth = Math.Max(2, width / bins.Count);

        MeasurementInsightHistogramCanvas.Children.Add(new Line
        {
            StartPoint = new Point(0, height - 0.5),
            EndPoint = new Point(width, height - 0.5),
            Stroke = new SolidColorBrush(Color.Parse("#667AA6D9")),
            StrokeThickness = 1,
        });

        for (int index = 0; index < bins.Count; index++)
        {
            double normalizedHeight = bins[index] / maxCount;
            double barHeight = Math.Max(1, normalizedHeight * (height - 10));
            var rectangle = new Rectangle
            {
                Width = Math.Max(1, barWidth - 1),
                Height = barHeight,
                Fill = new SolidColorBrush(Color.Parse("#CC4EC8FF")),
            };

            Canvas.SetLeft(rectangle, index * barWidth);
            Canvas.SetTop(rectangle, height - barHeight - 1);
            MeasurementInsightHistogramCanvas.Children.Add(rectangle);
        }

        AddHistogramTrendCurve(distribution, maxCount);

        AddHistogramMarker(distribution, distribution.Percentile10, Color.Parse("#FF7FDFA2"));
        AddHistogramMarker(distribution, distribution.Median, Color.Parse("#FFFFD86B"));
        AddHistogramMarker(distribution, distribution.Percentile90, Color.Parse("#FFFF8A8A"));
    }

    private void AddHistogramTrendCurve(DicomViewPanel.RoiDistributionDetails distribution, double maxCount)
    {
        IReadOnlyList<int> bins = distribution.HistogramBins;
        if (bins.Count < 2)
        {
            return;
        }

        double width = MeasurementInsightHistogramCanvas.Width;
        double height = MeasurementInsightHistogramCanvas.Height;
        double stepX = width / Math.Max(1, bins.Count - 1);
        var points = new List<Point>(bins.Count);

        for (int index = 0; index < bins.Count; index++)
        {
            double smoothed = GetSmoothedBinValue(bins, index);
            double y = height - Math.Max(1, (smoothed / Math.Max(1, maxCount)) * (height - 10)) - 1;
            points.Add(new Point(index * stepX, y));
        }

        MeasurementInsightHistogramCanvas.Children.Add(new Polyline
        {
            Points = new Points(points),
            Stroke = new SolidColorBrush(Color.Parse("#CCFFD56B")),
            StrokeThickness = 2,
        });
    }

    private static double GetSmoothedBinValue(IReadOnlyList<int> bins, int index)
    {
        int start = Math.Max(0, index - 1);
        int end = Math.Min(bins.Count - 1, index + 1);
        double total = 0;
        int count = 0;

        for (int current = start; current <= end; current++)
        {
            total += bins[current];
            count++;
        }

        return count == 0 ? 0 : total / count;
    }

    private void AddHistogramMarker(DicomViewPanel.RoiDistributionDetails distribution, double value, Color color)
    {
        double width = MeasurementInsightHistogramCanvas.Width;
        double height = MeasurementInsightHistogramCanvas.Height;
        double min = distribution.HistogramMinValue;
        double max = distribution.HistogramMaxValue;
        double x = Math.Abs(max - min) < 1e-6
            ? width * 0.5
            : Math.Clamp((value - min) / (max - min), 0, 1) * width;

        MeasurementInsightHistogramCanvas.Children.Add(new Line
        {
            StartPoint = new Point(x, 0),
            EndPoint = new Point(x, height),
            Stroke = new SolidColorBrush(color),
            StrokeThickness = 1.5,
        });
    }

    private void OnMeasurementInsightPinClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _measurementInsightPinned = MeasurementInsightPinButton.IsChecked == true;
        if (!_measurementInsightPinned && _selectedMeasurementId is null)
        {
            _lastMeasurementInsightSnapshot = null;
        }

        SaveViewerSettings();
        RefreshMeasurementInsightPanel();
    }

    private void OnMeasurementInsightCollapseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _measurementInsightCollapsed = MeasurementInsightCollapseButton.IsChecked == true;
        SaveViewerSettings();
        RefreshMeasurementInsightPanel();
    }

    private sealed record MeasurementInsightSnapshot(
        Guid MeasurementId,
        string Title,
        string SeriesText,
        string StatsText,
        string RangeText,
        string SupplementText,
        bool SupportsAutoOutlineCorrection,
        int AutoOutlineSensitivityLevel,
        DicomViewPanel.RoiDistributionDetails Distribution);
}
using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using FellowOakDicom;
using KPACS.Viewer.Controls;
using KPACS.Viewer.Models;
using KPACS.Viewer.Services;
using SpatialVector3D = KPACS.Viewer.Models.Vector3D;

namespace KPACS.Viewer;

public partial class StudyViewerWindow : Window
{
    private readonly ViewerStudyContext _context;
    private readonly List<ViewportSlot> _slots = [];
    private readonly Dictionary<string, DicomSpatialMetadata?> _spatialMetadataCache = new(StringComparer.OrdinalIgnoreCase);
    private ViewportSlot? _activeSlot;
    private Border? _dragGhost;

    public StudyViewerWindow(ViewerStudyContext context)
    {
        InitializeComponent();
        InitializeMeasurementsUi();
        _context = context;
        StudyTitleText.Text = context.StudyDetails.Study.PatientName;
        StudySubtitleText.Text = $"{context.StudyDetails.Study.StudyDescription}   {context.StudyDetails.Study.StudyDate}   {context.StudyDetails.Study.Modalities}";
        KeyUp += OnWindowKeyUp;
        Deactivated += (_, _) => Clear3DCursor();
        ApplyLayout(context.LayoutRows, context.LayoutColumns);
    }

    private void ApplyLayout(int rows, int columns)
    {
        ViewportGrid.RowDefinitions.Clear();
        ViewportGrid.ColumnDefinitions.Clear();
        ViewportGrid.Children.Clear();
        _slots.Clear();

        for (int row = 0; row < rows; row++)
        {
            ViewportGrid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        }

        for (int column = 0; column < columns; column++)
        {
            ViewportGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        }

        int slotCount = rows * columns;
        for (int index = 0; index < slotCount; index++)
        {
            var slot = new ViewportSlot();

            var border = new Border
            {
                Margin = new Thickness(2),
                Background = Brushes.Black,
                BorderBrush = new SolidColorBrush(Color.Parse("#FF383838")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(2),
                MinWidth = 80,
                MinHeight = 80,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
            };

            var panel = new DicomViewPanel
            {
                WheelMode = ScrollToggle.IsChecked == true ? MouseWheelMode.StackScroll : MouseWheelMode.Zoom,
                StackItemCount = slot.Series?.Instances.Count ?? 0,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
            };
            slot.Border = border;
            slot.Panel = panel;
            slot.Series = index < _context.StudyDetails.Series.Count ? _context.StudyDetails.Series[index] : null;
            slot.InstanceIndex = 0;

            ConfigureMeasurementPanel(slot, panel);
            panel.StackScrollRequested += delta => OnStackScroll(border, delta);
            panel.PointerPressed += (_, e) => OnViewportPressed(border, e);
            panel.HoveredImagePointChanged += hover => OnPanelHovered(slot, hover);
            panel.ViewStateChanged += () =>
            {
                if (panel.IsImageLoaded)
                {
                    slot.ViewState = panel.CaptureDisplayState();
                }
            };
            border.Child = panel;
            border.PointerPressed += OnViewportPressed;
            _slots.Add(slot);

            Grid.SetRow(border, index / columns);
            Grid.SetColumn(border, index % columns);
            ViewportGrid.Children.Add(border);
        }

        Dispatcher.UIThread.Post(() =>
        {
            foreach (ViewportSlot slot in _slots)
            {
                LoadSlot(slot);
            }

            SetActiveSlot(_slots.FirstOrDefault());
            UpdateStatus();
        }, DispatcherPriority.Loaded);

        SetActiveSlot(_slots.FirstOrDefault());
        UpdateStatus();
    }

    private void LoadSlot(ViewportSlot slot)
    {
        if (slot.Series is null || slot.Series.Instances.Count == 0)
        {
            return;
        }

        slot.InstanceIndex = Math.Clamp(slot.InstanceIndex, 0, slot.Series.Instances.Count - 1);
        slot.Panel.StackItemCount = slot.Series.Instances.Count;
        string filePath = slot.Series.Instances[slot.InstanceIndex].FilePath;
        DicomViewPanel.DisplayState? previousState = slot.ViewState;
        slot.Panel.LoadFile(filePath);
        slot.CurrentSpatialMetadata = slot.Panel.SpatialMetadata;
        _spatialMetadataCache[filePath] = slot.CurrentSpatialMetadata;
        ApplyMeasurementContext(slot);

        if (previousState is not null)
        {
            slot.Panel.ApplyDisplayState(previousState);
        }
        else if (slot.Panel.IsImageLoaded)
        {
            slot.ViewState = slot.Panel.CaptureDisplayState();
        }

        if (ReferenceEquals(slot, _activeSlot))
        {
            RefreshThumbnailStrip(slot.Series);
        }
    }

    private void OnViewportPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border border)
        {
            SetActiveSlot(_slots.FirstOrDefault(slot => ReferenceEquals(slot.Border, border)));
        }
    }

    private void SetActiveSlot(ViewportSlot? slot)
    {
        _activeSlot = slot;
        UpdateSlotVisualStates();
        RefreshThumbnailStrip(slot?.Series);
        UpdateStatus();
    }

    private void UpdateSlotVisualStates()
    {
        foreach (ViewportSlot current in _slots)
        {
            current.Border.BorderBrush = current.IsDropTarget
                ? new SolidColorBrush(Color.Parse("#FF35C7FF"))
                : current == _activeSlot
                    ? new SolidColorBrush(Color.Parse("#FFD9A03C"))
                    : new SolidColorBrush(Color.Parse("#FF383838"));

            current.Border.BorderThickness = current.IsDropTarget
                ? new Thickness(2)
                : new Thickness(1);
        }
    }

    private void OnStackScroll(Border sourceBorder, int delta)
    {
        ViewportSlot? slot = _slots.FirstOrDefault(candidate => ReferenceEquals(candidate.Border, sourceBorder));
        if (slot is null || slot.Series is null || slot.Series.Instances.Count == 0)
        {
            return;
        }

        SetActiveSlot(slot);
        slot.InstanceIndex = Math.Clamp(slot.InstanceIndex + delta, 0, slot.Series.Instances.Count - 1);
        LoadSlot(slot);
        UpdateStatus();
    }

    private void OnLayoutChanged(object? sender, SelectionChangedEventArgs e)
    {
        Clear3DCursor();

        if (LayoutBox.SelectedItem is not ComboBoxItem item || item.Tag is not string tag)
        {
            return;
        }

        string[] parts = tag.Split('x');
        if (parts.Length == 2 && int.TryParse(parts[0], out int rows) && int.TryParse(parts[1], out int columns))
        {
            ApplyLayout(rows, columns);
        }
    }

    private void OnLutChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (LutBox.SelectedItem is not ComboBoxItem item || item.Tag is not string tag || !int.TryParse(tag, out int scheme))
        {
            return;
        }

        foreach (ViewportSlot slot in _slots)
        {
            if (slot.Panel.IsImageLoaded)
            {
                slot.Panel.SetColorScheme(scheme);
            }
        }
    }

    private void OnWheelModeChanged(object? sender, RoutedEventArgs e)
    {
        MouseWheelMode mode = ScrollToggle.IsChecked == true ? MouseWheelMode.StackScroll : MouseWheelMode.Zoom;
        foreach (ViewportSlot slot in _slots)
        {
            slot.Panel.WheelMode = mode;
        }
        UpdateStatus();
    }

    private void OnFitClick(object? sender, RoutedEventArgs e) => ApplyToActiveOrAll(panel => panel.ApplyFitToWindow());
    private void OnOriginalClick(object? sender, RoutedEventArgs e) => ApplyToActiveOrAll(panel => panel.ZoomToOriginal());
    private void OnResetWindowClick(object? sender, RoutedEventArgs e) => ApplyToActiveOrAll(panel => panel.ResetWindowLevel());
    private void OnPrevImageClick(object? sender, RoutedEventArgs e) => StepActiveImage(-1);
    private void OnNextImageClick(object? sender, RoutedEventArgs e) => StepActiveImage(1);

    private void StepActiveImage(int delta)
    {
        if (_activeSlot is null || _activeSlot.Series is null)
        {
            return;
        }

        _activeSlot.InstanceIndex = Math.Clamp(_activeSlot.InstanceIndex + delta, 0, _activeSlot.Series.Instances.Count - 1);
        LoadSlot(_activeSlot);
        UpdateStatus();
    }

    private void ApplyToActiveOrAll(Action<DicomViewPanel> action)
    {
        if (_activeSlot is not null)
        {
            action(_activeSlot.Panel);
            UpdateStatus();
            return;
        }

        foreach (ViewportSlot slot in _slots)
        {
            action(slot.Panel);
        }
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (_activeSlot?.Series is null)
        {
            ViewerStatusText.Text = BuildStatusText(null);
            return;
        }

        ViewerStatusText.Text = BuildStatusText(_activeSlot);
    }

    private void RefreshThumbnailStrip(SeriesRecord? activeSeries)
    {
        ThumbnailStripPanel.Children.Clear();

        List<SeriesRecord> seriesList = _context.StudyDetails.Series
            .Where(series => series.Instances.Count > 0)
            .OrderBy(series => series.SeriesNumber)
            .ThenBy(series => series.SeriesDescription)
            .ToList();

        if (seriesList.Count == 0)
        {
            return;
        }

        int maxThumbs = Math.Min(seriesList.Count, 40);
        for (int index = 0; index < maxThumbs; index++)
        {
            SeriesRecord series = seriesList[index];
            int representativeIndex = GetRepresentativeInstanceIndex(series);
            InstanceRecord instance = series.Instances[representativeIndex];
            bool isActiveSeries = activeSeries is not null && string.Equals(activeSeries.SeriesInstanceUid, series.SeriesInstanceUid, StringComparison.Ordinal);

            var border = new Border
            {
                Width = 108,
                Height = 86,
                Padding = new Thickness(4),
                Background = Brushes.Black,
                BorderBrush = isActiveSeries ? new SolidColorBrush(Color.Parse("#FFF1E000")) : new SolidColorBrush(Color.Parse("#FF4B4B4B")),
                BorderThickness = isActiveSeries ? new Thickness(2) : new Thickness(1),
            };

            var grid = new Grid
            {
                RowDefinitions = new RowDefinitions("*,Auto"),
            };

            var thumbPanel = new DicomViewPanel
            {
                Width = 98,
                Height = 58,
                ShowOverlay = false,
                IsHitTestVisible = false,
            };
            thumbPanel.LoadFile(instance.FilePath);

            var label = new TextBlock
            {
                Text = $"S{Math.Max(series.SeriesNumber, index + 1)}",
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 0),
            };

            grid.Children.Add(thumbPanel);
            Grid.SetRow(label, 1);
            grid.Children.Add(label);
            border.Child = grid;

            SeriesRecord capturedSeries = series;
            border.PointerPressed += (_, e) => OnThumbnailPointerPressed(capturedSeries, border, e);
            border.PointerMoved += async (_, e) => await OnThumbnailPointerMovedAsync(capturedSeries, border, e);
            border.PointerReleased += (_, e) => OnThumbnailPointerReleased(capturedSeries, border, e);
            border.PointerCaptureLost += (_, _) => CancelThumbnailDrag(border);
            ThumbnailStripPanel.Children.Add(border);
        }

        if (seriesList.Count > maxThumbs)
        {
            ThumbnailStripPanel.Children.Add(new TextBlock
            {
                Text = $"+{seriesList.Count - maxThumbs} more series",
                Foreground = new SolidColorBrush(Color.Parse("#FFBDBDBD")),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
            });
        }
    }

    private void JumpToSeries(SeriesRecord series)
    {
        if (_activeSlot is null || series.Instances.Count == 0)
        {
            return;
        }

        _activeSlot.Series = series;
        _activeSlot.InstanceIndex = GetRepresentativeInstanceIndex(series);
        LoadSlot(_activeSlot);
        UpdateStatus();
    }

    private static int GetRepresentativeInstanceIndex(SeriesRecord series)
    {
        if (series.Instances.Count == 0)
        {
            return 0;
        }

        return series.Instances.Count / 2;
    }

    private void OnThumbnailPointerPressed(SeriesRecord series, Border border, PointerPressedEventArgs e)
    {
        PointerPoint point = e.GetCurrentPoint(border);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        border.Tag = new PendingThumbnailDrag(series, e.GetPosition(border));
    }

    private void OnThumbnailPointerReleased(SeriesRecord series, Border border, PointerReleasedEventArgs e)
    {
        if (border.Tag is not PendingThumbnailDrag pending)
        {
            return;
        }

        if (!pending.IsStarted)
        {
            JumpToSeries(series);
            CancelThumbnailDrag(border);
            return;
        }

        Point overlayPoint = e.GetPosition(DragGhostOverlay);
        ViewportSlot? slot = FindSlotAtOverlayPoint(overlayPoint);
        if (slot is not null)
        {
            AssignSeriesToSlot(slot, series);
            SetActiveSlot(slot);
        }

        CancelThumbnailDrag(border);
    }

    private Task OnThumbnailPointerMovedAsync(SeriesRecord series, Border border, PointerEventArgs e)
    {
        if (border.Tag is not PendingThumbnailDrag pending)
        {
            return Task.CompletedTask;
        }

        PointerPoint point = e.GetCurrentPoint(border);
        if (!point.Properties.IsLeftButtonPressed)
        {
            ClearPendingThumbnailDrag(border);
            return Task.CompletedTask;
        }

        Point current = e.GetPosition(border);
        Vector delta = current - pending.StartPoint;
        if (Math.Abs(delta.X) < 6 && Math.Abs(delta.Y) < 6)
        {
            return Task.CompletedTask;
        }

        if (!pending.IsStarted)
        {
            pending.IsStarted = true;
            e.Pointer.Capture(border);
            ShowDragGhost(series, pending.ThumbnailPath, e.GetPosition(DragGhostOverlay));
        }

        Point overlayPosition = e.GetPosition(DragGhostOverlay);
        UpdateDragGhostPosition(overlayPosition);
        SetDropTargetAtPoint(overlayPosition);
        return Task.CompletedTask;
    }

    private static void ClearPendingThumbnailDrag(Border border)
    {
        border.Tag = null;
    }

    private void CancelThumbnailDrag(Border border)
    {
        ClearPendingThumbnailDrag(border);
        HideDragGhost();
        ClearDropTargets();
    }

    private void AssignSeriesToSlot(ViewportSlot slot, SeriesRecord series)
    {
        slot.Series = series;
        slot.InstanceIndex = GetRepresentativeInstanceIndex(series);
        slot.ViewState = null;
        LoadSlot(slot);
        UpdateStatus();
    }

    private ViewportSlot? GetSlotFromSender(object? sender)
    {
        if (sender is not Border border)
        {
            return null;
        }

        return _slots.FirstOrDefault(slot => ReferenceEquals(slot.Border, border));
    }

    private void SetDropTarget(ViewportSlot slot)
    {
        foreach (ViewportSlot current in _slots)
        {
            current.IsDropTarget = ReferenceEquals(current, slot);
        }

        UpdateSlotVisualStates();
    }

    private void ClearDropTargets()
    {
        bool hadDropTarget = false;
        foreach (ViewportSlot current in _slots)
        {
            hadDropTarget |= current.IsDropTarget;
            current.IsDropTarget = false;
        }

        if (hadDropTarget)
        {
            UpdateSlotVisualStates();
        }
    }

    private void ShowDragGhost(SeriesRecord series, string thumbnailPath, Point overlayPosition)
    {
        HideDragGhost();

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
            IsHitTestVisible = false,
        };
        thumbPanel.LoadFile(thumbnailPath);

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

        _dragGhost = ghostBorder;
        DragGhostOverlay.Children.Add(ghostBorder);
        UpdateDragGhostPosition(overlayPosition);
    }

    private void UpdateDragGhostPosition(Point overlayPosition)
    {
        if (_dragGhost is null)
        {
            return;
        }

        Canvas.SetLeft(_dragGhost, overlayPosition.X + 14);
        Canvas.SetTop(_dragGhost, overlayPosition.Y + 14);
    }

    private void HideDragGhost()
    {
        if (_dragGhost is not null)
        {
            DragGhostOverlay.Children.Remove(_dragGhost);
            _dragGhost = null;
        }
    }

    private void SetDropTargetAtPoint(Point overlayPoint)
    {
        ViewportSlot? slot = FindSlotAtOverlayPoint(overlayPoint);
        if (slot is null)
        {
            ClearDropTargets();
            return;
        }

        SetDropTarget(slot);
    }

    private ViewportSlot? FindSlotAtOverlayPoint(Point overlayPoint)
    {
        foreach (ViewportSlot slot in _slots)
        {
            Point? topLeft = slot.Border.TranslatePoint(new Point(0, 0), DragGhostOverlay);
            if (topLeft is null)
            {
                continue;
            }

            Rect rect = new(topLeft.Value, slot.Border.Bounds.Size);
            if (rect.Contains(overlayPoint))
            {
                return slot;
            }
        }

        return null;
    }

    private void OnPanelHovered(ViewportSlot sourceSlot, DicomHoverInfo? hover)
    {
        if (sourceSlot.Series is null || sourceSlot.CurrentSpatialMetadata is null)
        {
            if (hover is null)
            {
                Clear3DCursor();
            }

            return;
        }

        if (hover is null)
        {
            Clear3DCursor();

            return;
        }

        if (!Is3DCursorRequested(hover.Modifiers))
        {
            Clear3DCursor();
            return;
        }

        Apply3DCursor(sourceSlot, hover.ImagePoint);
    }

    private void Apply3DCursor(ViewportSlot sourceSlot, Point sourceImagePoint)
    {
        DicomSpatialMetadata? sourceMetadata = sourceSlot.CurrentSpatialMetadata;
        if (sourceMetadata is null || !sourceMetadata.ContainsImagePoint(sourceImagePoint))
        {
            Clear3DCursor();
            return;
        }

        SpatialVector3D patientPoint = sourceMetadata.PatientPointFromPixel(sourceImagePoint);

        foreach (ViewportSlot slot in _slots)
        {
            if (slot.Series is null || slot.Series.Instances.Count == 0)
            {
                slot.Panel.Set3DCursorOverlay(null);
                continue;
            }

            if (ReferenceEquals(slot, sourceSlot))
            {
                slot.Panel.Set3DCursorOverlay(sourceImagePoint);
                continue;
            }

            SliceProjection? projection = FindBestProjection(slot, sourceMetadata, patientPoint);
            if (projection is null)
            {
                slot.Panel.Set3DCursorOverlay(null);
                continue;
            }

            if (slot.InstanceIndex != projection.InstanceIndex)
            {
                slot.InstanceIndex = projection.InstanceIndex;
                LoadSlot(slot);
            }

            slot.CurrentSpatialMetadata = GetSpatialMetadata(slot.Series.Instances[slot.InstanceIndex]);
            slot.Panel.Set3DCursorOverlay(projection.ImagePoint);
        }

        UpdateStatus();
    }

    private SliceProjection? FindBestProjection(ViewportSlot slot, DicomSpatialMetadata sourceMetadata, SpatialVector3D patientPoint)
    {
        if (slot.Series is null)
        {
            return null;
        }

        SliceProjection? best = null;

        for (int index = 0; index < slot.Series.Instances.Count; index++)
        {
            DicomSpatialMetadata? metadata = GetSpatialMetadata(slot.Series.Instances[index]);
            if (metadata is null || !metadata.IsCompatibleWith(sourceMetadata))
            {
                continue;
            }

            Point imagePoint = metadata.PixelPointFromPatient(patientPoint);
            double distance = metadata.DistanceToPlane(patientPoint);
            bool containsPoint = metadata.ContainsImagePoint(imagePoint, tolerance: 1.0);

            if (best is null ||
                distance < best.DistanceToPlane - 1e-6 ||
                (Math.Abs(distance - best.DistanceToPlane) <= 1e-6 && containsPoint && !best.ContainsImagePoint))
            {
                best = new SliceProjection(index, imagePoint, distance, containsPoint);
            }
        }

        return best;
    }

    private DicomSpatialMetadata? GetSpatialMetadata(InstanceRecord instance)
    {
        if (_spatialMetadataCache.TryGetValue(instance.FilePath, out DicomSpatialMetadata? cached))
        {
            return cached;
        }

        try
        {
            DicomDataset dataset = DicomFile.Open(instance.FilePath, FellowOakDicom.FileReadOption.ReadAll).Dataset;
            cached = DicomSpatialMetadata.FromDataset(dataset, instance.FilePath);
        }
        catch
        {
            cached = null;
        }

        _spatialMetadataCache[instance.FilePath] = cached;
        return cached;
    }

    private void Clear3DCursor()
    {
        foreach (ViewportSlot slot in _slots)
        {
            slot.Panel.Set3DCursorOverlay(null);
        }
    }

    private void OnWindowKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.LeftShift || e.Key == Key.RightShift)
        {
            Clear3DCursor();
            UpdateStatus();
        }
    }

    private bool Is3DCursorRequested(KeyModifiers modifiers) =>
        modifiers.HasFlag(KeyModifiers.Shift);

    private string BuildStatusText(ViewportSlot? slot)
    {
        string toolText = ScrollToggle.IsChecked == true ? "Stack tool" : "Zoom tool";
        string cursorText = "Hold SHIFT for 3D cursor";
        string measurementText = $"Measure: {GetMeasurementToolLabel()}";

        if (slot?.Series is null)
        {
            return $"{toolText}   {measurementText}   {cursorText}";
        }

        return $"{slot.Series.Modality}   Series {slot.Series.SeriesNumber}   Image {slot.InstanceIndex + 1}/{slot.Series.Instances.Count}   {toolText}   {measurementText}   {cursorText}";
    }

    private sealed class ViewportSlot
    {
        public Border Border { get; set; } = null!;
        public DicomViewPanel Panel { get; set; } = null!;
        public SeriesRecord? Series { get; set; }
        public int InstanceIndex { get; set; }
        public DicomViewPanel.DisplayState? ViewState { get; set; }
        public DicomSpatialMetadata? CurrentSpatialMetadata { get; set; }
        public bool IsDropTarget { get; set; }
    }

    private sealed record SliceProjection(int InstanceIndex, Point ImagePoint, double DistanceToPlane, bool ContainsImagePoint);
    private sealed class PendingThumbnailDrag(SeriesRecord series, Point startPoint)
    {
        public SeriesRecord Series { get; } = series;
        public Point StartPoint { get; } = startPoint;
        public string ThumbnailPath { get; } = series.Instances.Count > 0
            ? series.Instances[GetRepresentativeInstanceIndex(series)].FilePath
            : string.Empty;
        public bool IsStarted { get; set; }
    }
}

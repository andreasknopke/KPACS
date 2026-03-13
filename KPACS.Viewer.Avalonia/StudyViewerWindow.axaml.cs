using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using FellowOakDicom;
using KPACS.Viewer.Controls;
using KPACS.Viewer.Models;
using KPACS.Viewer.Rendering;
using KPACS.Viewer.Services;
using KPACS.Viewer.Windows;
using SpatialVector3D = KPACS.Viewer.Models.Vector3D;

namespace KPACS.Viewer;

public partial class StudyViewerWindow : Window
{
    private static readonly Lock s_openViewerSyncLock = new();
    private static readonly HashSet<StudyViewerWindow> s_openViewerWindows = [];

    private readonly ViewerStudyContext _context;
    private readonly RemoteStudyRetrievalSession? _remoteRetrievalSession;
    private readonly bool _startBlank;
    private readonly int _viewerNumber;
    private readonly List<ViewportSlot> _slots = [];
    private readonly Dictionary<string, DicomSpatialMetadata?> _spatialMetadataCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SeriesVolume?> _volumeCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ObservableCollection<ToastNotificationItem> _toastNotifications = [];
    private readonly CancellationTokenSource _priorLookupCancellation = new();
    private readonly DispatcherTimer _actionToolbarHideTimer = new();
    private readonly DispatcherTimer _workspaceDockHideTimer = new();
    private readonly DispatcherTimer _viewportLayoutResetTimer = new();
    private CancellationTokenSource? _adjacentPriorityDebounceCancellation;
    private readonly string? _viewerSettingsPath;
    private IReadOnlyList<PriorStudySummary> _priorStudies = [];
    private PriorStudySummary? _selectedPriorStudy;
    private StudyDetails? _thumbnailStripStudy;
    private CancellationTokenSource? _priorPreviewCancellation;
    private ViewportSlot? _activeSlot;
    private SeriesDragGhostWindow? _dragGhostWindow;
    private ActionToolbarMode _actionToolbarMode = ActionToolbarMode.ScrollStack;
    private int _selectedColorScheme = (int)ColorScheme.Grayscale;
    private bool _overlayEnabled = true;
    private bool _linkedViewSyncEnabled = true;
    private List<int> _currentLayoutSpec = [1];
    private List<string> _savedCustomLayouts = [];
    private ViewerLayoutState? _layoutBeforeFocusedView;
    private bool _isShowingCurrentStudy;
    private bool _isActionToolbarPointerOver;
    private bool _isWorkspaceDockPointerOver;
    private bool _isWorkspaceDockVisible = true;
    private bool _isPriorPreviewLoading;
    private bool _isSynchronizingLinkedViews;
    private bool _is3DCursorToolArmed;
    private bool _assignedPriorStudyLoadAttempted;
    private int _linkedSyncSuspendCount;
    private ViewportSlot? _linkedReferenceSourceSlot;
    private ViewportSlot? _openToolboxSlot;
    private SeriesVolume? _linkedReferenceSourceVolume;
    private DicomSpatialMetadata? _linkedReferenceSourceMetadata;
    private PendingLinkedSyncContext? _pendingLinkedSyncContext;
    private string _thumbnailStripMessage = string.Empty;
    private const int AdjacentPriorityDebounceMs = 180;
    private const int WorkspaceDockAutoHideMs = 1000;
    private const int ViewportLayoutResetDebounceMs = 140;
    private const int MaxLayoutRows = 6;
    private const int MaxLayoutColumnsPerRow = 6;
    private const int MaxLayoutSlots = 12;
    private const double ParallelPlaneDotThreshold = 0.985;
    private const double CutlineEdgeTolerance = 1e-3;

    public StudyViewerWindow(ViewerStudyContext context, string placementKey, int viewerNumber)
    {
        InitializeComponent();
        InitializeToolboxIcons();
        InitializeMeasurementsUi();
        _context = context;
        _remoteRetrievalSession = context.RemoteRetrievalSession;
        _startBlank = context.StartBlank;
        _viewerNumber = viewerNumber;
        _isShowingCurrentStudy = !context.StartBlank;
        Title = $"K-PACS Viewer {viewerNumber}";
        if (Application.Current is App app)
        {
            _viewerSettingsPath = Path.Combine(app.Paths.ApplicationDirectory, "study-viewer-settings.json");
            app.WindowPlacementService.Register(this, placementKey);
        }
        RegisterForLinkedViewSync();
        List<int> defaultLayout = BuildUniformLayoutSpec(context.LayoutRows, context.LayoutColumns);
        _currentLayoutSpec = [.. defaultLayout];
        LoadViewerSettings(defaultLayout);
        if (_remoteRetrievalSession is not null)
        {
            _remoteRetrievalSession.StudyChanged += OnRemoteStudyChanged;
        }
        InitializeWorkspaceDock();
        InitializeActionToolbar();
        InitializeViewportLayoutReset();
        ToastItemsControl.ItemsSource = _toastNotifications;
        ViewerIdentityText.Text = $"Viewer {viewerNumber}";
        StudyTitleText.Text = context.StudyDetails.Study.PatientName;
        StudySubtitleText.Text = BuildSubtitle();
        KeyUp += OnWindowKeyUp;
        SizeChanged += OnWindowSizeChanged;
        Deactivated += (_, _) => Clear3DCursor();
        Opened += OnViewerOpened;
        ApplyLayout(_currentLayoutSpec, persistLayout: false);
        InitializeSecondaryCaptureUi();
        Closed += OnViewerClosed;
    }

    private void InitializeActionToolbar()
    {
        OverlayToggleButton.IsChecked = _overlayEnabled;
        LinkedSyncToggleButton.IsChecked = _linkedViewSyncEnabled;
        _actionToolbarHideTimer.Interval = TimeSpan.FromSeconds(2.2);
        _actionToolbarHideTimer.Tick += OnActionToolbarHideTimerTick;
        SetActionToolbarMode(ActionToolbarMode.ZoomPan);
        ApplyOverlayState();
        RefreshSavedCustomLayoutsUi();
        ShowActionToolbar();
        RestartActionToolbarHideTimer();
    }

    private void InitializeWorkspaceDock()
    {
        _workspaceDockHideTimer.Interval = TimeSpan.FromMilliseconds(WorkspaceDockAutoHideMs);
        _workspaceDockHideTimer.Tick += OnWorkspaceDockHideTimerTick;
        ShowWorkspaceDock(restartHideTimer: true);
    }

    private void InitializeViewportLayoutReset()
    {
        _viewportLayoutResetTimer.Interval = TimeSpan.FromMilliseconds(ViewportLayoutResetDebounceMs);
        _viewportLayoutResetTimer.Tick += OnViewportLayoutResetTimerTick;
    }

    private void ApplyLayout(int rows, int columns)
    {
        ApplyLayout(BuildUniformLayoutSpec(rows, columns));
    }

    private void ApplyLayout(IReadOnlyList<int> rowLayout, IReadOnlyList<ViewportSlotState>? slotStates = null, bool persistLayout = true, bool clearFocusedSingleView = true)
    {
        CloseViewportToolbox();
        List<int> normalizedLayout = NormalizeLayoutSpec(rowLayout);
        List<ViewportSlotState> states = slotStates?.ToList() ?? CaptureViewportSlotStates();
        int activeSlotIndex = states.FindIndex(state => state.IsActive);

        _currentLayoutSpec = [.. normalizedLayout];
        ViewportGrid.RowDefinitions.Clear();
        ViewportGrid.ColumnDefinitions.Clear();
        ViewportGrid.Children.Clear();
        _slots.Clear();

        ViewportGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

        for (int row = 0; row < normalizedLayout.Count; row++)
        {
            ViewportGrid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
            var rowGrid = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
            };

            for (int column = 0; column < normalizedLayout[row]; column++)
            {
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            }

            Grid.SetRow(rowGrid, row);
            Grid.SetColumn(rowGrid, 0);
            ViewportGrid.Children.Add(rowGrid);

            for (int column = 0; column < normalizedLayout[row]; column++)
            {
                int index = _slots.Count;
                ViewportSlotState? state = index < states.Count ? states[index] : null;
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
                    WheelMode = GetMouseWheelModeForAction(),
                    ActionMode = _actionToolbarMode,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    ShowOverlay = _overlayEnabled,
                };

                slot.Border = border;
                slot.Panel = panel;
                slot.Series = state?.Series ?? (!_startBlank && index < _context.StudyDetails.Series.Count ? _context.StudyDetails.Series[index] : null);
                slot.InstanceIndex = state?.InstanceIndex ?? 0;
                slot.ViewState = state?.ViewState;
                slot.CurrentSpatialMetadata = state?.CurrentSpatialMetadata;
                slot.Volume = state?.Volume;
                panel.StackItemCount = slot.Series?.Instances.Count ?? 0;

                ConfigureMeasurementPanel(slot, panel);
                ConfigureSecondaryCapturePanel(slot, panel);
                panel.StackScrollRequested += delta => OnStackScroll(border, delta);
                panel.PointerPressed += (_, e) => OnViewportPressed(border, e);
                panel.HoveredImagePointChanged += info => OnPanelHoveredImagePointChanged(slot, info);
                panel.ImagePointPressed += info => OnPanelImagePointPressed(slot, info);
                panel.ViewStateChanged += () => OnPanelViewStateChanged(slot, panel);
                panel.ImageDoubleClicked += () => OnPanelImageDoubleClicked(slot);
                panel.ToolboxRequested += () => OnPanelToolboxRequested(slot);
                border.Child = panel;
                border.PointerPressed += OnViewportPressed;
                _slots.Add(slot);

                Grid.SetColumn(border, column);
                rowGrid.Children.Add(border);
            }
        }

        Dispatcher.UIThread.Post(() =>
        {
            foreach (ViewportSlot slot in _slots)
            {
                LoadSlot(slot);
            }

            SetActiveSlot(GetSlotAtIndex(activeSlotIndex));
            SynchronizeLinkedViews(_activeSlot);
            UpdateStatus();
        }, DispatcherPriority.Loaded);

        SetActiveSlot(GetSlotAtIndex(activeSlotIndex));
        if (CustomLayoutTextBox is not null)
        {
            CustomLayoutTextBox.Text = LayoutSpecToString(_currentLayoutSpec);
        }

        if (clearFocusedSingleView)
        {
            _layoutBeforeFocusedView = null;
        }

        if (persistLayout)
        {
            SaveViewerSettings();
        }

        UpdateStatus();
    }

    private static List<int> BuildUniformLayoutSpec(int rows, int columns)
    {
        rows = Math.Clamp(rows, 1, MaxLayoutRows);
        columns = Math.Clamp(columns, 1, MaxLayoutColumnsPerRow);
        return Enumerable.Repeat(columns, rows).ToList();
    }

    private static List<int> NormalizeLayoutSpec(IReadOnlyList<int> rowLayout)
    {
        List<int> normalized = rowLayout
            .Where(value => value > 0)
            .Take(MaxLayoutRows)
            .Select(value => Math.Clamp(value, 1, MaxLayoutColumnsPerRow))
            .ToList();

        if (normalized.Count == 0)
        {
            normalized.Add(1);
        }

        int totalSlots = 0;
        for (int index = 0; index < normalized.Count; index++)
        {
            int remaining = MaxLayoutSlots - totalSlots;
            if (remaining <= 0)
            {
                normalized.RemoveRange(index, normalized.Count - index);
                break;
            }

            normalized[index] = Math.Min(normalized[index], remaining);
            totalSlots += normalized[index];
        }

        return normalized;
    }

    private List<ViewportSlotState> CaptureViewportSlotStates()
    {
        return _slots.Select(slot => new ViewportSlotState(
            slot.Series,
            slot.InstanceIndex,
            slot.ViewState,
            slot.CurrentSpatialMetadata,
            slot.Volume,
            ReferenceEquals(slot, _activeSlot))).ToList();
    }

    private ViewportSlot? GetSlotAtIndex(int index)
    {
        if (_slots.Count == 0)
        {
            return null;
        }

        if (index < 0 || index >= _slots.Count)
        {
            return _slots[0];
        }

        return _slots[index];
    }

    private static bool TryParseLayoutSpec(string? input, out List<int> rowLayout)
    {
        rowLayout = [];
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        string normalized = input.Trim();
        char[] separators = [',', ';', '/', '\\', ' '];

        if (normalized.Contains(':') || normalized.Contains('x', StringComparison.OrdinalIgnoreCase))
        {
            string compact = normalized.Replace(" ", string.Empty);
            string[] gridParts = compact.Split([':', 'x', 'X'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (gridParts.Length == 2 &&
                int.TryParse(gridParts[0], out int rows) &&
                int.TryParse(gridParts[1], out int columns) &&
                rows > 0 &&
                columns > 0)
            {
                rowLayout = BuildUniformLayoutSpec(rows, columns);
                return rowLayout.Sum() <= MaxLayoutSlots;
            }
        }

        string[] rowParts = normalized.Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (rowParts.Length == 0 || rowParts.Length > MaxLayoutRows)
        {
            return false;
        }

        foreach (string rowPart in rowParts)
        {
            if (!int.TryParse(rowPart, out int columns) || columns <= 0 || columns > MaxLayoutColumnsPerRow)
            {
                rowLayout.Clear();
                return false;
            }

            rowLayout.Add(columns);
        }

        if (rowLayout.Sum() > MaxLayoutSlots)
        {
            rowLayout.Clear();
            return false;
        }

        return true;
    }

    private static string LayoutSpecToString(IReadOnlyList<int> rowLayout)
    {
        if (rowLayout.Count == 0)
        {
            return "1:1";
        }

        bool uniform = rowLayout.All(value => value == rowLayout[0]);
        return uniform
            ? $"{rowLayout.Count}:{rowLayout[0]}"
            : string.Join(",", rowLayout);
    }

    private static string LayoutSpecToDisplayText(IReadOnlyList<int> rowLayout)
    {
        if (rowLayout.Count == 0)
        {
            return "1:1";
        }

        bool uniform = rowLayout.All(value => value == rowLayout[0]);
        return uniform
            ? $"{rowLayout.Count}:{rowLayout[0]}"
            : string.Join(" | ", rowLayout.Select((value, index) => $"R{index + 1}={value}"));
    }

    private void RefreshSavedCustomLayoutsUi()
    {
        if (SavedCustomLayoutsPanel is null)
        {
            return;
        }

        SavedCustomLayoutsPanel.Children.Clear();
        if (_savedCustomLayouts.Count == 0)
        {
            SavedCustomLayoutsPanel.Children.Add(new TextBlock
            {
                Text = "No saved layouts yet.",
                Foreground = new SolidColorBrush(Color.Parse("#FFBDBDBD")),
                FontSize = 11,
                Margin = new Thickness(0, 2, 0, 0),
            });
            return;
        }

        foreach (string layoutSpec in _savedCustomLayouts)
        {
            if (!TryParseLayoutSpec(layoutSpec, out List<int> parsedLayout))
            {
                continue;
            }

            var button = new Button
            {
                Content = LayoutSpecToDisplayText(parsedLayout),
                Tag = layoutSpec,
                MinWidth = 120,
            };
            button.Classes.Add("popupAction");
            button.Click += OnSavedCustomLayoutClick;
            SavedCustomLayoutsPanel.Children.Add(button);
        }
    }

    private void ApplyCustomLayoutInput(bool savePreset)
    {
        string layoutSpec = CustomLayoutTextBox.Text?.Trim() ?? string.Empty;
        if (!TryParseLayoutSpec(layoutSpec, out List<int> rowLayout))
        {
            ShowToast("Invalid layout. Examples: 2:3 or 1,2", ToastSeverity.Warning);
            return;
        }

        string normalizedSpec = LayoutSpecToString(rowLayout);
        Clear3DCursor();
        ApplyLayout(rowLayout);
        ResetViewportsForLayoutChange();

        if (savePreset)
        {
            _savedCustomLayouts.RemoveAll(existing => string.Equals(existing, normalizedSpec, StringComparison.OrdinalIgnoreCase));
            _savedCustomLayouts.Insert(0, normalizedSpec);
            if (_savedCustomLayouts.Count > 10)
            {
                _savedCustomLayouts.RemoveRange(10, _savedCustomLayouts.Count - 10);
            }

            RefreshSavedCustomLayoutsUi();
            SaveViewerSettings();
            ShowToast($"Layout {normalizedSpec} saved.", ToastSeverity.Success);
        }

        CloseAllActionPopups();
        RestartActionToolbarHideTimer();
    }

    private void OnPanelImageDoubleClicked(ViewportSlot slot)
    {
        SetActiveSlot(slot);

        if (_layoutBeforeFocusedView is null)
        {
            if (_currentLayoutSpec.Count == 1 && _currentLayoutSpec[0] == 1)
            {
                return;
            }

            _layoutBeforeFocusedView = new ViewerLayoutState([.. _currentLayoutSpec], CaptureViewportSlotStates());
            ApplyLayout([1], [new ViewportSlotState(slot.Series, slot.InstanceIndex, slot.ViewState, slot.CurrentSpatialMetadata, slot.Volume, true)], persistLayout: false, clearFocusedSingleView: false);
            ResetViewportsForLayoutChange();
            return;
        }

        ViewerLayoutState previousLayout = _layoutBeforeFocusedView;
        _layoutBeforeFocusedView = null;
        ApplyLayout(previousLayout.RowLayout, previousLayout.SlotStates, persistLayout: false, clearFocusedSingleView: false);
        ResetViewportsForLayoutChange();
        SaveViewerSettings();
    }

    private void LoadSlot(ViewportSlot slot, bool refreshThumbnailStrip = true, bool preferExactRemoteFocus = false)
    {
        if (slot.Series is null || slot.Series.Instances.Count == 0)
        {
            slot.Panel.ClearImage();
            return;
        }

        // Volume path: if a volume is loaded for this series, use it
        if (slot.Volume is not null)
        {
            ApplySlotOverlayStudyInfo(slot);
            SliceOrientation orientation = slot.Panel.BoundVolume == slot.Volume
                ? slot.Panel.VolumeOrientation
                : SliceOrientation.Axial;
            int sliceCount = VolumeReslicer.GetSliceCount(slot.Volume, orientation);
            slot.InstanceIndex = Math.Clamp(slot.InstanceIndex, 0, Math.Max(0, sliceCount - 1));
            slot.Panel.StackItemCount = sliceCount;

            DicomViewPanel.DisplayState? previousState = slot.ViewState;
            if (slot.Panel.BoundVolume != slot.Volume)
            {
                slot.Panel.BindVolume(slot.Volume, orientation, slot.InstanceIndex);
            }
            else
            {
                slot.Panel.ShowVolumeSlice(slot.InstanceIndex);
            }

            slot.CurrentSpatialMetadata = slot.Panel.SpatialMetadata;
            if (slot.CurrentSpatialMetadata?.FilePath is { Length: > 0 } fp)
                _spatialMetadataCache[fp] = slot.CurrentSpatialMetadata;
            ApplyMeasurementContext(slot);

            if (previousState is not null)
                slot.Panel.ApplyDisplayState(previousState);
            else if (slot.Panel.CurrentColorScheme != _selectedColorScheme)
                slot.Panel.SetColorScheme(_selectedColorScheme);
            else if (slot.Panel.IsImageLoaded)
                slot.ViewState = slot.Panel.CaptureDisplayState();

            if (refreshThumbnailStrip && ReferenceEquals(slot, _activeSlot))
                RefreshThumbnailStrip(slot.Series);

            UpdateSecondaryCaptureIndicator(slot);
            return;
        }

        // Legacy file-based path
        int totalCount = GetSeriesTotalCount(slot.Series);
        slot.InstanceIndex = Math.Clamp(slot.InstanceIndex, 0, Math.Max(0, totalCount - 1));
        slot.Panel.StackItemCount = totalCount;
        InstanceRecord instance = slot.Series.Instances[Math.Clamp(slot.InstanceIndex, 0, slot.Series.Instances.Count - 1)];
        if (!IsLocalInstance(instance))
        {
            slot.CurrentSpatialMetadata = null;
            slot.Panel.ClearImage();
            RequestSlotPriority(slot, exactFocusOnly: preferExactRemoteFocus);
            if (refreshThumbnailStrip && ReferenceEquals(slot, _activeSlot))
            {
                RefreshThumbnailStrip(slot.Series);
            }

            UpdateSecondaryCaptureIndicator(slot);
            return;
        }

        string filePath = instance.FilePath;
        DicomViewPanel.DisplayState? legacyPreviousState = slot.ViewState;
        slot.Panel.LoadFile(filePath);
        slot.CurrentSpatialMetadata = slot.Panel.SpatialMetadata;
        _spatialMetadataCache[filePath] = slot.CurrentSpatialMetadata;
        ApplyMeasurementContext(slot);

        if (legacyPreviousState is not null)
        {
            slot.Panel.ApplyDisplayState(legacyPreviousState);
        }
        else if (slot.Panel.CurrentColorScheme != _selectedColorScheme)
        {
            slot.Panel.SetColorScheme(_selectedColorScheme);
        }
        else if (slot.Panel.IsImageLoaded)
        {
            slot.ViewState = slot.Panel.CaptureDisplayState();
        }

        if (refreshThumbnailStrip && ReferenceEquals(slot, _activeSlot))
        {
            RefreshThumbnailStrip(slot.Series);
        }

        UpdateSecondaryCaptureIndicator(slot);
    }

    private void ApplySlotOverlayStudyInfo(ViewportSlot slot)
    {
        StudyDetails? displayedStudy = _isShowingCurrentStudy ? _context.StudyDetails : _thumbnailStripStudy;
        if (displayedStudy is null)
        {
            return;
        }

        string modality = !string.IsNullOrWhiteSpace(slot.Series?.Modality)
            ? slot.Series!.Modality
            : displayedStudy.Study.Modalities;

        slot.Panel.SetOverlayStudyInfo(
            displayedStudy.Study.PatientName,
            displayedStudy.Study.PatientId,
            displayedStudy.Study.StudyDate,
            displayedStudy.Study.StudyDescription,
            displayedStudy.LegacyStudy?.InstitutionName ?? string.Empty,
            modality);
    }

    private void OnViewportPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border border)
        {
            ViewportSlot? slot = _slots.FirstOrDefault(candidate => ReferenceEquals(candidate.Border, border));
            SetActiveSlot(slot);
            RequestSlotPriority(slot);
        }
    }

    private void SetActiveSlot(ViewportSlot? slot, bool requestPriority = true)
    {
        if (ReferenceEquals(_activeSlot, slot))
        {
            return;
        }

        _activeSlot = slot;
        UpdateSlotVisualStates();
        RefreshThumbnailStrip(slot?.Series);
        if (requestPriority)
        {
            RequestSlotPriority(slot);
        }

        if (_linkedViewSyncEnabled)
        {
            SetLinkedReferenceSource(slot, slot?.Volume, slot?.CurrentSpatialMetadata);
        }
        else
        {
            ClearLinkedReferenceLines();
        }

        UpdateStatus();
    }

    private void OnPanelViewStateChanged(ViewportSlot slot, DicomViewPanel panel)
    {
        if (!panel.IsImageLoaded)
        {
            return;
        }

        if (panel.IsVolumeBound)
        {
            slot.InstanceIndex = panel.VolumeSliceIndex;
            panel.StackItemCount = panel.VolumeSliceCount;
        }

        slot.ViewState = panel.CaptureDisplayState();
        string syncSignature = BuildLinkedSyncSignature(slot, panel);
        bool syncAnchorChanged = !string.Equals(slot.LastLinkedSyncSignature, syncSignature, StringComparison.Ordinal);
        slot.LastLinkedSyncSignature = syncSignature;

        if (ReferenceEquals(slot, _linkedReferenceSourceSlot))
        {
            _linkedReferenceSourceMetadata = slot.CurrentSpatialMetadata;
        }

        RefreshLinkedReferenceLines();

        if (_isSynchronizingLinkedViews || IsLinkedSyncSuspended())
        {
            return;
        }

        if (!syncAnchorChanged)
        {
            return;
        }

        SynchronizeLinkedViews(slot);
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

        if (!ReferenceEquals(_activeSlot, slot))
        {
            SetActiveSlot(slot, requestPriority: false);
        }

        int maxIndex = slot.Volume is not null
            ? Math.Max(0, slot.Panel.VolumeSliceCount - 1)
            : GetSeriesTotalCount(slot.Series) - 1;
        slot.InstanceIndex = Math.Clamp(slot.InstanceIndex + delta, 0, maxIndex);
        if (slot.Volume is null)
            RequestSlotPriority(slot, Math.Sign(delta));
        LoadSlot(slot, refreshThumbnailStrip: false);
        UpdateStatus();
    }

    private void OnLayoutChanged(object? sender, SelectionChangedEventArgs e)
    {
        Clear3DCursor();
    }

    private void OnWindowSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width <= 0 || e.NewSize.Height <= 0)
        {
            return;
        }

        if (_slots.Count == 0 || !_slots.Any(slot => slot.Panel.IsImageLoaded))
        {
            return;
        }

        QueueViewportResetForLayoutChange();
    }

    private void QueueViewportResetForLayoutChange()
    {
        _viewportLayoutResetTimer.Stop();
        _viewportLayoutResetTimer.Start();
    }

    private void OnViewportLayoutResetTimerTick(object? sender, EventArgs e)
    {
        _viewportLayoutResetTimer.Stop();
        ResetViewportsForLayoutChange();
    }

    private void ApplyColorScheme(int scheme)
    {
        _selectedColorScheme = scheme;

        foreach (ViewportSlot slot in _slots)
        {
            if (slot.Panel.IsImageLoaded)
            {
                slot.Panel.SetColorScheme(scheme);
            }
        }

        UpdateStatus();
    }

    private void OnWheelModeChanged(object? sender, RoutedEventArgs e)
    {
        ApplyActionModeToPanels();
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

        int maxIndex = _activeSlot.Volume is not null
            ? Math.Max(0, _activeSlot.Panel.VolumeSliceCount - 1)
            : GetSeriesTotalCount(_activeSlot.Series) - 1;
        _activeSlot.InstanceIndex = Math.Clamp(_activeSlot.InstanceIndex + delta, 0, maxIndex);
        if (_activeSlot.Volume is null)
            RequestSlotPriority(_activeSlot, Math.Sign(delta));
        LoadSlot(_activeSlot, refreshThumbnailStrip: false);
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

    private async void OnViewerOpened(object? sender, EventArgs e)
    {
        Opened -= OnViewerOpened;
        try
        {
            await LoadPriorStudiesAsync();
        }
        finally
        {
            _remoteRetrievalSession?.StartBackgroundRetrieval();
        }

        // Load volumes in the background for all visible slots
        _ = LoadVolumesForSlotsAsync();
    }

    private async Task LoadVolumesForSlotsAsync()
    {
        var volumeLoader = new VolumeLoaderService();
        var slotsToLoad = _slots
            .Where(s => s.Series is not null && s.Volume is null)
            .ToList();

        foreach (var slot in slotsToLoad)
        {
            if (slot.Series is null)
                continue;

            string seriesUid = slot.Series.SeriesInstanceUid;

            // Check cache first
            if (_volumeCache.TryGetValue(seriesUid, out var cachedVolume))
            {
                if (cachedVolume is not null)
                {
                    await Dispatcher.UIThread.InvokeAsync(() => ApplyVolumeToSlot(slot, cachedVolume));
                }
                continue;
            }

            try
            {
                var volume = await volumeLoader.TryLoadVolumeAsync(slot.Series);
                _volumeCache[seriesUid] = volume;

                if (volume is not null)
                {
                    await Dispatcher.UIThread.InvokeAsync(() => ApplyVolumeToSlot(slot, volume));
                }
            }
            catch
            {
                _volumeCache[seriesUid] = null;
            }
        }
    }

    private void ApplyVolumeToSlot(ViewportSlot slot, SeriesVolume volume)
    {
        // Preserve current view state and approximate slice position
        var viewState = slot.Panel.IsImageLoaded ? slot.Panel.CaptureDisplayState() : null;
        int previousIndex = slot.InstanceIndex;

        slot.Volume = volume;

        // Map the file-based instance index to the closest volume slice index
        int sliceCount = VolumeReslicer.GetSliceCount(volume, SliceOrientation.Axial);
        int totalInstances = slot.Series?.Instances.Count ?? 1;
        int mappedIndex = totalInstances > 1
            ? (int)((long)previousIndex * (sliceCount - 1) / (totalInstances - 1))
            : sliceCount / 2;
        slot.InstanceIndex = Math.Clamp(mappedIndex, 0, sliceCount - 1);

        LoadSlot(slot, refreshThumbnailStrip: ReferenceEquals(slot, _activeSlot));

        if (viewState is not null)
            slot.Panel.ApplyDisplayState(viewState);

        if (_linkedViewSyncEnabled)
        {
            if (ReferenceEquals(slot, _linkedReferenceSourceSlot))
            {
                SetLinkedReferenceSource(slot, slot.Volume, slot.CurrentSpatialMetadata);
            }

            ReplayPendingLinkedSyncContext();
            RefreshLinkedReferenceLines();
        }

        UpdateStatus();
    }

    private async Task LoadPriorStudiesAsync()
    {
        if (_context.InitialPriorStudies is not null)
        {
            UpdatePriorStudies(_context.InitialPriorStudies);
            await TryAutoLoadAssignedPriorStudyAsync(_context.InitialPriorStudies);
            if (_startBlank)
            {
                if (!_isShowingCurrentStudy)
                {
                    RefreshThumbnailStrip(null);
                }
                else
                {
                    _thumbnailStripMessage = _context.InitialPriorStudies.Count == 0
                        ? "No other studies are available. Select the current study thumbnails to populate this viewer."
                        : "Select the current study or one of the patient-study chips to populate this viewer.";
                    RefreshThumbnailStrip(null);
                }
            }

            return;
        }

        if (_context.LoadPriorStudiesAsync is null)
        {
            UpdatePriorStudies(Array.Empty<PriorStudySummary>());
            return;
        }

        try
        {
            IReadOnlyList<PriorStudySummary> relatedStudies = await _context.LoadPriorStudiesAsync(_priorLookupCancellation.Token);
            if (_priorLookupCancellation.IsCancellationRequested)
            {
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                UpdatePriorStudies(relatedStudies);
                if (relatedStudies.Count > 0)
                {
                    int newerCount = relatedStudies.Count(study => study.IsNewerThanCurrentStudy);
                    ShowToast(relatedStudies.Count == 1
                        ? "1 additional patient study is available for comparison."
                        : $"{relatedStudies.Count} additional patient studies are available for comparison." +
                          (newerCount > 0 ? $" ({newerCount} newer)" : string.Empty), ToastSeverity.Info);
                }
            });

            await TryAutoLoadAssignedPriorStudyAsync(relatedStudies);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            await Dispatcher.UIThread.InvokeAsync(() => UpdatePriorStudies(Array.Empty<PriorStudySummary>()));
        }
    }

    private async Task TryAutoLoadAssignedPriorStudyAsync(IReadOnlyList<PriorStudySummary> availablePriors)
    {
        if (_assignedPriorStudyLoadAttempted)
        {
            return;
        }

        _assignedPriorStudyLoadAttempted = true;

        if (_context.InitialAssignedPriorStudy is not PriorStudySummary assignedPriorStudy)
        {
            return;
        }

        PriorStudySummary? matchedPrior = availablePriors.FirstOrDefault(prior =>
            string.Equals(prior.StudyInstanceUid, assignedPriorStudy.StudyInstanceUid, StringComparison.Ordinal));

        if (matchedPrior is null)
        {
            return;
        }

        await LoadPriorStudyAsync(matchedPrior, populateViewer: true);
    }

    private void UpdatePriorStudies(IReadOnlyList<PriorStudySummary> priors)
    {
        _priorStudies = priors.ToList();
        if (_selectedPriorStudy is not null
            && !_priorStudies.Any(prior => string.Equals(prior.StudyInstanceUid, _selectedPriorStudy.StudyInstanceUid, StringComparison.Ordinal)))
        {
            ShowCurrentStudyThumbnails();
        }

        RenderPriorStudyChips();
    }

    private void RefreshThumbnailStrip(SeriesRecord? activeSeries)
    {
        ThumbnailStripPanel.Children.Clear();

        StudyDetails? thumbnailStudy = _isShowingCurrentStudy ? _context.StudyDetails : _thumbnailStripStudy;
        bool showingCurrentStudy = _isShowingCurrentStudy;

        if (thumbnailStudy is null)
        {
            ShowThumbnailStripMessage(_thumbnailStripMessage);
            return;
        }

        List<SeriesRecord> seriesList = thumbnailStudy.Series
            .Where(series => GetSeriesTotalCount(series) > 0)
            .OrderBy(series => series.SeriesNumber)
            .ThenBy(series => series.SeriesDescription)
            .ToList();

        if (seriesList.Count == 0)
        {
            ShowThumbnailStripMessage(_thumbnailStripMessage);
            return;
        }

        int maxThumbs = Math.Min(seriesList.Count, 40);
        for (int index = 0; index < maxThumbs; index++)
        {
            SeriesRecord series = seriesList[index];
            int representativeIndex = GetRepresentativeInstanceIndex(series);
            InstanceRecord instance = series.Instances[Math.Clamp(representativeIndex, 0, series.Instances.Count - 1)];
            InstanceRecord? localRepresentative = GetBestLocalRepresentativeInstance(series);
            bool isActiveSeries = activeSeries is not null && string.Equals(activeSeries.SeriesInstanceUid, series.SeriesInstanceUid, StringComparison.Ordinal);
            bool isSecondaryCaptureSeries = HasManagedSecondaryCapture(series);

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
                ShowToolboxButton = false,
                IsHitTestVisible = false,
            };
            if (localRepresentative is not null)
            {
                thumbPanel.LoadFile(localRepresentative.FilePath);
            }
            else
            {
                RequestSeriesThumbnail(series, representativeIndex);
            }

            var label = new TextBlock
            {
                Text = $"S{Math.Max(series.SeriesNumber, index + 1)}  {GetSeriesLoadedCount(series)}/{GetSeriesTotalCount(series)}",
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 0),
            };

            grid.Children.Add(thumbPanel);

            if (isSecondaryCaptureSeries)
            {
                var keyBadge = new Border
                {
                    Background = new SolidColorBrush(Color.Parse("#E6F6D04D")),
                    BorderBrush = new SolidColorBrush(Color.Parse("#FFFFF4B3")),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(9),
                    Padding = new Thickness(4, 1),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(4, 4, 0, 0),
                    Child = new TextBlock
                    {
                        Text = "🔑",
                        FontSize = 12,
                        Foreground = Brushes.Black,
                    },
                };

                ToolTip.SetTip(keyBadge, "K-PACS key image series");
                grid.Children.Add(keyBadge);
            }

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

    private void RenderPriorStudyChips()
    {
        PriorStudiesPanel.Children.Clear();
        PriorStudiesScrollViewer.IsVisible = _priorStudies.Count > 0;
        if (_priorStudies.Count == 0)
        {
            return;
        }

        PriorStudiesPanel.Children.Add(CreateChipButton(
            label: _isShowingCurrentStudy ? "Current study" : "Load current study",
            toolTip: "Show and load the current study in this viewer.",
            isSelected: _isShowingCurrentStudy,
            isRemote: false,
            isNewerStudy: false,
            isLoading: false,
            onClick: (_, _) => ShowCurrentStudyThumbnails()));

        foreach (PriorStudySummary prior in _priorStudies)
        {
            bool isSelected = _selectedPriorStudy is not null
                && string.Equals(_selectedPriorStudy.StudyInstanceUid, prior.StudyInstanceUid, StringComparison.Ordinal);
            string label = prior.IsRemote ? $"☁ {prior.DisplayLabel}" : prior.DisplayLabel;
            if (isSelected && _isPriorPreviewLoading)
            {
                label += " • loading";
            }

            PriorStudiesPanel.Children.Add(CreateChipButton(
                label,
                prior.ToolTipText,
                isSelected,
                prior.IsRemote,
                prior.IsNewerThanCurrentStudy,
                isSelected && _isPriorPreviewLoading,
                async (_, _) => await ShowPriorStudyThumbnailsAsync(prior)));
        }
    }

    private Button CreateChipButton(string label, string toolTip, bool isSelected, bool isRemote, bool isNewerStudy, bool isLoading, EventHandler<RoutedEventArgs> onClick)
    {
        Color background = isSelected
            ? Color.Parse(isNewerStudy ? "#FF3D3A1A" : isRemote ? "#FF224C69" : "#FF5B4A20")
            : Color.Parse(isNewerStudy ? "#FF2E2B15" : isRemote ? "#FF202D36" : "#FF2B2B2B");
        Color border = isSelected
            ? Color.Parse(isNewerStudy ? "#FFFFE26D" : isRemote ? "#FF76C8FF" : "#FFFFD66C")
            : Color.Parse(isNewerStudy ? "#FFC3A948" : isRemote ? "#FF3A647B" : "#FF565656");
        Color foreground = isLoading ? Color.Parse("#FFFFF1C1") : Colors.White;

        var button = new Button
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Content = new Border
            {
                Background = new SolidColorBrush(background),
                BorderBrush = new SolidColorBrush(border),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(12, 5),
                Child = new TextBlock
                {
                    Text = label,
                    Foreground = new SolidColorBrush(foreground),
                    FontSize = 12,
                },
            },
        };

        ToolTip.SetTip(button, toolTip);
        button.Click += onClick;
        return button;
    }

    private void ShowCurrentStudyThumbnails()
    {
        CancelPriorPreviewLoad();
        _isShowingCurrentStudy = true;
        _selectedPriorStudy = null;
        _thumbnailStripStudy = null;
        _thumbnailStripMessage = string.Empty;
        _isPriorPreviewLoading = false;
        _remoteRetrievalSession?.StartBackgroundRetrieval();
        LoadStudyIntoSlots(_context.StudyDetails);
        RenderPriorStudyChips();
        RefreshThumbnailStrip(_activeSlot?.Series);
        RequestSlotPriority(_activeSlot);
        StudySubtitleText.Text = BuildSubtitle();
    }

    private async Task ShowPriorStudyThumbnailsAsync(PriorStudySummary priorStudy)
    {
        await LoadPriorStudyAsync(priorStudy, populateViewer: false);
    }

    private async Task LoadPriorStudyAsync(PriorStudySummary priorStudy, bool populateViewer)
    {
        if (_context.LoadPriorStudyPreviewAsync is null)
        {
            return;
        }

        CancelPriorPreviewLoad();
        _remoteRetrievalSession?.StartBackgroundRetrieval();
        _isShowingCurrentStudy = false;
        _selectedPriorStudy = priorStudy;
        _thumbnailStripStudy = null;
        _isPriorPreviewLoading = true;
        _thumbnailStripMessage = priorStudy.IsRemote
            ? "Loading remote study previews…"
            : "Loading study previews…";
        RenderPriorStudyChips();
        RefreshThumbnailStrip(null);
        StudySubtitleText.Text = BuildSubtitle();

        _priorPreviewCancellation = CancellationTokenSource.CreateLinkedTokenSource(_priorLookupCancellation.Token);
        try
        {
            await _context.LoadPriorStudyPreviewAsync(priorStudy, details =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (_selectedPriorStudy is null || !string.Equals(_selectedPriorStudy.StudyInstanceUid, priorStudy.StudyInstanceUid, StringComparison.Ordinal))
                    {
                        return;
                    }

                    _thumbnailStripStudy = details;
                    _thumbnailStripMessage = string.Empty;
                    if (populateViewer)
                    {
                        LoadStudyIntoSlots(details);
                    }
                    RefreshThumbnailStrip(null);
                    StudySubtitleText.Text = BuildSubtitle();
                });
            }, _priorPreviewCancellation.Token);

            if (_selectedPriorStudy is not null && string.Equals(_selectedPriorStudy.StudyInstanceUid, priorStudy.StudyInstanceUid, StringComparison.Ordinal))
            {
                _isPriorPreviewLoading = false;
                if (_thumbnailStripStudy is null || _thumbnailStripStudy.Series.Count == 0)
                {
                    _thumbnailStripMessage = priorStudy.IsRemote
                        ? "No remote study preview images arrived."
                        : "No study preview images are available.";
                    RefreshThumbnailStrip(null);
                }

                RenderPriorStudyChips();
                StudySubtitleText.Text = BuildSubtitle();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (_selectedPriorStudy is not null && string.Equals(_selectedPriorStudy.StudyInstanceUid, priorStudy.StudyInstanceUid, StringComparison.Ordinal))
            {
                _isPriorPreviewLoading = false;
                _thumbnailStripMessage = $"Prior preview failed: {ex.Message}";
                RefreshThumbnailStrip(null);
                RenderPriorStudyChips();
                StudySubtitleText.Text = BuildSubtitle();
                ShowToast(_thumbnailStripMessage, ToastSeverity.Warning, TimeSpan.FromSeconds(6));
            }
        }
    }

    private void LoadStudyIntoSlots(StudyDetails study)
    {
        for (int index = 0; index < _slots.Count; index++)
        {
            ViewportSlot slot = _slots[index];
            slot.Series = index < study.Series.Count ? study.Series[index] : null;
            slot.Volume = slot.Series is not null && _volumeCache.TryGetValue(slot.Series.SeriesInstanceUid, out var vol) ? vol : null;
            slot.InstanceIndex = 0;
            slot.ViewState = null;
            LoadSlot(slot, refreshThumbnailStrip: false);

            if (slot.Series is not null && slot.Volume is null)
            {
                _ = EnsureVolumeLoadedForSlotAsync(slot, slot.Series);
            }
        }

        SetActiveSlot(_slots.FirstOrDefault());
        SynchronizeLinkedViews(_activeSlot);
        UpdateStatus();
    }

    private string BuildSubtitle()
    {
        string baseSubtitle = $"{_context.StudyDetails.Study.StudyDescription}   {_context.StudyDetails.Study.StudyDate}   {_context.StudyDetails.Study.Modalities}".Trim();
        if (_isShowingCurrentStudy)
        {
            return $"Viewer {_viewerNumber}   {baseSubtitle}".Trim();
        }

        if (_selectedPriorStudy is not null)
        {
            return $"Viewer {_viewerNumber}   Comparison mode   {_selectedPriorStudy.DisplayLabel}";
        }

        return $"Viewer {_viewerNumber}   Ready for comparison";
    }

    private void CancelPriorPreviewLoad()
    {
        if (_priorPreviewCancellation is not null)
        {
            _priorPreviewCancellation.Cancel();
            _priorPreviewCancellation.Dispose();
            _priorPreviewCancellation = null;
        }
    }

    private void ShowThumbnailStripMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        ThumbnailStripPanel.Children.Add(new TextBlock
        {
            Text = message,
            Foreground = new SolidColorBrush(Color.Parse("#FFBDBDBD")),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 4, 0, 4),
        });
    }

    private void JumpToSeries(SeriesRecord series)
    {
        if (_activeSlot is null || GetSeriesTotalCount(series) == 0)
        {
            return;
        }

        _activeSlot.Series = series;
        _activeSlot.Volume = _volumeCache.TryGetValue(series.SeriesInstanceUid, out var vol) ? vol : null;
        _activeSlot.InstanceIndex = _activeSlot.Volume is not null
            ? VolumeReslicer.GetSliceCount(_activeSlot.Volume, SliceOrientation.Axial) / 2
            : GetRepresentativeInstanceIndex(series);
        if (_activeSlot.Volume is null)
            RequestSeriesPriority(series, _activeSlot.InstanceIndex);
        LoadSlot(_activeSlot);
        if (_activeSlot.Volume is null)
            _ = EnsureVolumeLoadedForSlotAsync(_activeSlot, series);
        UpdateStatus();
    }

    private static int GetRepresentativeInstanceIndex(SeriesRecord series)
    {
        int total = GetSeriesTotalCount(series);
        if (total == 0)
        {
            return 0;
        }

        return total / 2;
    }

    private void OnThumbnailPointerPressed(SeriesRecord series, Border border, PointerPressedEventArgs e)
    {
        PointerPoint point = e.GetCurrentPoint(border);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        border.Tag = new PendingThumbnailDrag(series, e.GetPosition(border), GetBestThumbnailPath(series));
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

        Point screenPoint = GetPointerScreenPoint(e);
        ViewportDropTarget? dropTarget = FindDropTargetAtScreenPoint(screenPoint);
        if (dropTarget is not null)
        {
            dropTarget.Window.AssignSeriesToSlot(dropTarget.Slot, series);
            dropTarget.Window.SetActiveSlot(dropTarget.Slot);
            dropTarget.Window.Activate();
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
            ShowDragGhost(series, pending.ThumbnailPath, GetPointerScreenPoint(e));
        }

        UpdateDragGhostPosition(GetPointerScreenPoint(e));
        SetDropTargetAtScreenPoint(GetPointerScreenPoint(e));
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
        ClearDropTargetsAcrossViewers();
    }

    private void AssignSeriesToSlot(ViewportSlot slot, SeriesRecord series)
    {
        slot.Series = series;
        slot.Volume = _volumeCache.TryGetValue(series.SeriesInstanceUid, out var vol) ? vol : null;
        slot.InstanceIndex = slot.Volume is not null
            ? VolumeReslicer.GetSliceCount(slot.Volume, SliceOrientation.Axial) / 2
            : GetRepresentativeInstanceIndex(series);
        slot.ViewState = null;
        if (slot.Volume is null)
            RequestSeriesPriority(series, slot.InstanceIndex);
        LoadSlot(slot);
        if (slot.Volume is null)
            _ = EnsureVolumeLoadedForSlotAsync(slot, series);
        SynchronizeLinkedViews(_activeSlot ?? slot);
        UpdateStatus();
    }

    private async Task EnsureVolumeLoadedForSlotAsync(ViewportSlot slot, SeriesRecord series)
    {
        string seriesUid = series.SeriesInstanceUid;

        if (_volumeCache.TryGetValue(seriesUid, out SeriesVolume? cachedVolume))
        {
            if (cachedVolume is not null && ReferenceEquals(slot.Series, series))
            {
                await Dispatcher.UIThread.InvokeAsync(() => ApplyVolumeToSlot(slot, cachedVolume));
            }

            return;
        }

        var volumeLoader = new VolumeLoaderService();

        try
        {
            SeriesVolume? volume = await volumeLoader.TryLoadVolumeAsync(series);
            _volumeCache[seriesUid] = volume;

            if (volume is not null && ReferenceEquals(slot.Series, series))
            {
                await Dispatcher.UIThread.InvokeAsync(() => ApplyVolumeToSlot(slot, volume));
            }
        }
        catch
        {
            _volumeCache[seriesUid] = null;
        }
    }

    private void SynchronizeLinkedViews(ViewportSlot? sourceSlot)
    {
        if (!_linkedViewSyncEnabled || _isSynchronizingLinkedViews || IsLinkedSyncSuspended() || sourceSlot?.Series is null || sourceSlot.CurrentSpatialMetadata is null)
        {
            return;
        }

        if (!sourceSlot.Panel.TryCaptureNavigationState(out DicomViewPanel.NavigationState navigationState))
        {
            return;
        }

        DicomSpatialMetadata sourceMetadata = sourceSlot.CurrentSpatialMetadata;
        SetPendingLinkedSyncContext(sourceSlot, sourceSlot.Volume, sourceMetadata, navigationState);
        SetLinkedReferenceSource(sourceSlot, sourceSlot.Volume, sourceMetadata);
        SpatialVector3D patientPoint = sourceMetadata.PatientPointFromPixel(navigationState.CenterImagePoint);

        ApplyLinkedViewSync(sourceSlot, sourceSlot?.Volume, sourceMetadata, patientPoint, navigationState, broadcastToPeerViewers: true);
    }

    private void ApplyProjectionToSlot(ViewportSlot slot, SliceProjection projection, bool preferExactRemoteFocus)
    {
        if (slot.Series is null || slot.Series.Instances.Count == 0)
        {
            return;
        }

        int targetIndex = Math.Clamp(projection.InstanceIndex, 0, slot.Series.Instances.Count - 1);
        bool targetIsRemote = slot.Volume is null && !IsLocalInstance(slot.Series.Instances[targetIndex]);
        bool needsLoad = slot.InstanceIndex != targetIndex || !slot.Panel.IsImageLoaded || targetIsRemote;
        if (!needsLoad)
        {
            return;
        }

        slot.InstanceIndex = targetIndex;
        LoadSlot(slot, refreshThumbnailStrip: false, preferExactRemoteFocus: preferExactRemoteFocus);
    }

    private void ApplyLinkedViewSync(
        ViewportSlot? sourceSlot,
        SeriesVolume? sourceVolume,
        DicomSpatialMetadata sourceMetadata,
        SpatialVector3D patientPoint,
        DicomViewPanel.NavigationState navigationState,
        bool broadcastToPeerViewers)
    {
        if (!_linkedViewSyncEnabled || _isSynchronizingLinkedViews)
        {
            return;
        }

        try
        {
            _isSynchronizingLinkedViews = true;

            foreach (ViewportSlot targetSlot in _slots)
            {
                if (ReferenceEquals(targetSlot, sourceSlot) || targetSlot.Series is null || targetSlot.Series.Instances.Count == 0)
                {
                    continue;
                }

                if (targetSlot.CurrentSpatialMetadata is not DicomSpatialMetadata targetMetadata ||
                    !ArePlanesParallel(sourceMetadata, targetMetadata))
                {
                    continue;
                }

                SliceProjection? projection = FindBestProjection(targetSlot, sourceMetadata, patientPoint, sourceVolume);
                if (projection is null)
                {
                    continue;
                }

                ApplyProjectionToSlot(targetSlot, projection, preferExactRemoteFocus: true);

                targetSlot.Panel.ApplyNavigationState(navigationState with { CenterImagePoint = projection.ImagePoint });
                if (targetSlot.Panel.IsImageLoaded)
                {
                    targetSlot.ViewState = targetSlot.Panel.CaptureDisplayState();
                }
            }

            if (broadcastToPeerViewers)
            {
                BroadcastLinkedViewSync(sourceVolume, sourceMetadata, patientPoint, navigationState);
            }
        }
        finally
        {
            _isSynchronizingLinkedViews = false;
        }

        RefreshLinkedReferenceLines();
    }

    private void BroadcastLinkedViewSync(
        SeriesVolume? sourceVolume,
        DicomSpatialMetadata sourceMetadata,
        SpatialVector3D patientPoint,
        DicomViewPanel.NavigationState navigationState)
    {
        List<StudyViewerWindow> peers;
        lock (s_openViewerSyncLock)
        {
            peers = s_openViewerWindows
                .Where(window => !ReferenceEquals(window, this)
                    && window._linkedViewSyncEnabled
                    && HasLinkedSyncAffinityTo(window))
                .ToList();
        }

        foreach (StudyViewerWindow peer in peers)
        {
            peer.ApplyExternalLinkedViewSync(sourceVolume, sourceMetadata, patientPoint, navigationState);
        }
    }

    private void ApplyExternalLinkedViewSync(
        SeriesVolume? sourceVolume,
        DicomSpatialMetadata sourceMetadata,
        SpatialVector3D patientPoint,
        DicomViewPanel.NavigationState navigationState)
    {
        if (!_linkedViewSyncEnabled || IsLinkedSyncSuspended())
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            SetPendingLinkedSyncContext(null, sourceVolume, sourceMetadata, navigationState);
            SetLinkedReferenceSource(null, sourceVolume, sourceMetadata);
            ApplyLinkedViewSync(
                sourceSlot: null,
                sourceVolume,
                sourceMetadata,
                patientPoint,
                navigationState,
                broadcastToPeerViewers: false);
        }, DispatcherPriority.Input);
    }

    private void SetPendingLinkedSyncContext(
        ViewportSlot? sourceSlot,
        SeriesVolume? sourceVolume,
        DicomSpatialMetadata sourceMetadata,
        DicomViewPanel.NavigationState navigationState)
    {
        SpatialVector3D patientPoint = sourceMetadata.PatientPointFromPixel(navigationState.CenterImagePoint);
        _pendingLinkedSyncContext = new PendingLinkedSyncContext(sourceSlot, sourceVolume, sourceMetadata, patientPoint, navigationState);
    }

    private void ReplayPendingLinkedSyncContext()
    {
        if (!_linkedViewSyncEnabled || _pendingLinkedSyncContext is null || _isSynchronizingLinkedViews || IsLinkedSyncSuspended())
        {
            return;
        }

        PendingLinkedSyncContext context = _pendingLinkedSyncContext;
        ApplyLinkedViewSync(
            context.SourceSlot,
            context.SourceVolume,
            context.SourceMetadata,
            context.PatientPoint,
            context.NavigationState,
            broadcastToPeerViewers: false);
    }

    private void SetLinkedReferenceSource(ViewportSlot? sourceSlot, SeriesVolume? sourceVolume, DicomSpatialMetadata? sourceMetadata)
    {
        _linkedReferenceSourceSlot = sourceMetadata is null ? null : sourceSlot;
        _linkedReferenceSourceVolume = sourceMetadata is null ? null : sourceVolume;
        _linkedReferenceSourceMetadata = sourceMetadata;
        RefreshLinkedReferenceLines();
    }

    private void ClearLinkedReferenceLines()
    {
        _linkedReferenceSourceSlot = null;
        _linkedReferenceSourceVolume = null;
        _linkedReferenceSourceMetadata = null;

        foreach (ViewportSlot slot in _slots)
        {
            slot.Panel.SetReferenceLineOverlay(null, null);
        }
    }

    private void RefreshLinkedReferenceLines()
    {
        if (!_linkedViewSyncEnabled || _linkedReferenceSourceMetadata is null)
        {
            foreach (ViewportSlot slot in _slots)
            {
                slot.Panel.SetReferenceLineOverlay(null, null);
            }

            return;
        }

        DicomSpatialMetadata sourceMetadata = _linkedReferenceSourceMetadata;
        foreach (ViewportSlot slot in _slots)
        {
            if (ReferenceEquals(slot, _linkedReferenceSourceSlot) ||
                slot.Series is null ||
                slot.CurrentSpatialMetadata is not DicomSpatialMetadata targetMetadata)
            {
                slot.Panel.SetReferenceLineOverlay(null, null);
                continue;
            }

            DicomSpatialMetadata? effectiveSourceMetadata = GetReferenceMetadataForTarget(
                sourceMetadata,
                _linkedReferenceSourceVolume,
                slot,
                targetMetadata);

            if (effectiveSourceMetadata is null ||
                ArePlanesParallel(effectiveSourceMetadata, targetMetadata))
            {
                slot.Panel.SetReferenceLineOverlay(null, null);
                continue;
            }

            if (TryBuildCutlineSegment(effectiveSourceMetadata, targetMetadata, out Point start, out Point end))
            {
                slot.Panel.SetReferenceLineOverlay(start, end);
            }
            else
            {
                slot.Panel.SetReferenceLineOverlay(null, null);
            }
        }
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

    private void ShowDragGhost(SeriesRecord series, string thumbnailPath, Point screenPoint)
    {
        HideDragGhost();

        _dragGhostWindow = new SeriesDragGhostWindow(series, thumbnailPath)
        {
            ShowActivated = false,
        };
        _dragGhostWindow.Show();
        UpdateDragGhostPosition(screenPoint);
    }

    private void UpdateDragGhostPosition(Point screenPoint)
    {
        if (_dragGhostWindow is null)
        {
            return;
        }

        _dragGhostWindow.MoveTo(screenPoint, 14, 14);
    }

    private void HideDragGhost()
    {
        if (_dragGhostWindow is not null)
        {
            _dragGhostWindow.Close();
            _dragGhostWindow = null;
        }
    }

    private void SetDropTargetAtScreenPoint(Point screenPoint)
    {
        ViewportDropTarget? dropTarget = FindDropTargetAtScreenPoint(screenPoint);
        foreach (StudyViewerWindow window in GetOpenViewerWindowsSnapshot())
        {
            if (dropTarget is not null && ReferenceEquals(window, dropTarget.Window))
            {
                window.SetDropTarget(dropTarget.Slot);
            }
            else
            {
                window.ClearDropTargets();
            }
        }
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

    private ViewportDropTarget? FindDropTargetAtScreenPoint(Point screenPoint)
    {
        foreach (StudyViewerWindow window in GetOpenViewerWindowsSnapshot())
        {
            ViewportSlot? slot = window.FindSlotAtScreenPoint(screenPoint);
            if (slot is not null)
            {
                return new ViewportDropTarget(window, slot);
            }
        }

        return null;
    }

    private ViewportSlot? FindSlotAtScreenPoint(Point screenPoint)
    {
        foreach (ViewportSlot slot in _slots)
        {
            Point? topLeft = slot.Border.TranslatePoint(new Point(0, 0), this);
            if (topLeft is null)
            {
                continue;
            }

            Point windowOrigin = new(Position.X, Position.Y);
            Rect rect = new(windowOrigin + topLeft.Value, slot.Border.Bounds.Size);
            if (rect.Contains(screenPoint))
            {
                return slot;
            }
        }

        return null;
    }

    private Point GetPointerScreenPoint(PointerEventArgs e)
    {
        Point localPoint = e.GetPosition(this);
        return new Point(Position.X + localPoint.X, Position.Y + localPoint.Y);
    }

    private void ClearDropTargetsAcrossViewers()
    {
        foreach (StudyViewerWindow window in GetOpenViewerWindowsSnapshot())
        {
            window.ClearDropTargets();
        }
    }

    private static List<StudyViewerWindow> GetOpenViewerWindowsSnapshot()
    {
        lock (s_openViewerSyncLock)
        {
            return [.. s_openViewerWindows];
        }
    }

    private void OnPanelImagePointPressed(ViewportSlot sourceSlot, DicomImagePointerInfo info)
    {
        if (sourceSlot.Series is null || sourceSlot.CurrentSpatialMetadata is null)
        {
            return;
        }

        if (!Is3DCursorRequested(info.Modifiers))
        {
            return;
        }

        SetActiveSlot(sourceSlot, requestPriority: false);
        Apply3DCursor(sourceSlot, info.ImagePoint);
        UpdateStatus();
    }

    private void OnPanelHoveredImagePointChanged(ViewportSlot sourceSlot, DicomHoverInfo? info)
    {
        if (sourceSlot.Series is null || sourceSlot.CurrentSpatialMetadata is null || info is null)
        {
            return;
        }

        if (!Is3DCursorRequested(info.Modifiers))
        {
            return;
        }

        SetActiveSlot(sourceSlot, requestPriority: false);
        Apply3DCursor(sourceSlot, info.ImagePoint);
        UpdateStatus();
    }

    private static string BuildLinkedSyncSignature(ViewportSlot slot, DicomViewPanel panel)
    {
        string seriesUid = slot.Series?.SeriesInstanceUid ?? string.Empty;
        if (panel.IsVolumeBound)
        {
            return $"vol|{seriesUid}|{panel.VolumeOrientation}|{panel.VolumeSliceIndex}";
        }

        return $"img|{seriesUid}|{slot.InstanceIndex}";
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

        try
        {
            _isSynchronizingLinkedViews = true;

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

                SliceProjection? projection = FindBestProjection(slot, sourceMetadata, patientPoint, sourceSlot.Volume);
                if (projection is null)
                {
                    slot.Panel.Set3DCursorOverlay(null);
                    continue;
                }

                ApplyProjectionToSlot(slot, projection, preferExactRemoteFocus: true);

                slot.CurrentSpatialMetadata = slot.Volume is not null
                    ? slot.Panel.SpatialMetadata
                    : GetSpatialMetadata(slot.Series.Instances[slot.InstanceIndex]);
                slot.Panel.Set3DCursorOverlay(projection.ImagePoint);
            }
        }
        finally
        {
            _isSynchronizingLinkedViews = false;
        }

        Broadcast3DCursor(sourceSlot.Volume, sourceMetadata, patientPoint);

        UpdateStatus();
    }

    private void Broadcast3DCursor(SeriesVolume? sourceVolume, DicomSpatialMetadata sourceMetadata, SpatialVector3D patientPoint)
    {
        if (!_linkedViewSyncEnabled)
        {
            return;
        }

        List<StudyViewerWindow> peers;
        lock (s_openViewerSyncLock)
        {
            peers = s_openViewerWindows
                .Where(window => !ReferenceEquals(window, this)
                    && window._linkedViewSyncEnabled
                    && HasLinkedSyncAffinityTo(window))
                .ToList();
        }

        foreach (StudyViewerWindow peer in peers)
        {
            peer.ApplyExternal3DCursor(sourceVolume, sourceMetadata, patientPoint);
        }
    }

    private void ApplyExternal3DCursor(SeriesVolume? sourceVolume, DicomSpatialMetadata sourceMetadata, SpatialVector3D patientPoint)
    {
        if (!_linkedViewSyncEnabled)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                _isSynchronizingLinkedViews = true;

                foreach (ViewportSlot slot in _slots)
                {
                    if (slot.Series is null || slot.Series.Instances.Count == 0)
                    {
                        slot.Panel.Set3DCursorOverlay(null);
                        continue;
                    }

                    SliceProjection? projection = FindBestProjection(slot, sourceMetadata, patientPoint, sourceVolume);
                    if (projection is null)
                    {
                        slot.Panel.Set3DCursorOverlay(null);
                        continue;
                    }

                    ApplyProjectionToSlot(slot, projection, preferExactRemoteFocus: true);

                    slot.CurrentSpatialMetadata = slot.Volume is not null
                        ? slot.Panel.SpatialMetadata
                        : GetSpatialMetadata(slot.Series.Instances[slot.InstanceIndex]);
                    slot.Panel.Set3DCursorOverlay(projection.ImagePoint);
                }
            }
            finally
            {
                _isSynchronizingLinkedViews = false;
            }

            UpdateStatus();
        }, DispatcherPriority.Input);
    }

    private SliceProjection? FindBestProjection(ViewportSlot slot, DicomSpatialMetadata sourceMetadata, SpatialVector3D patientPoint, SeriesVolume? sourceVolume)
    {
        if (slot.Series is null)
        {
            return null;
        }

        if (slot.Volume is not null)
        {
            SliceProjection? directVolumeProjection = FindBestVolumeProjection(slot, sourceMetadata, patientPoint);
            if (directVolumeProjection is not null)
            {
                return directVolumeProjection;
            }
        }

        if (sourceVolume is not null && slot.Volume is not null &&
            VolumeRegistrationService.TryTransformPatientPoint(sourceVolume, slot.Volume, patientPoint, out SpatialVector3D registeredPatientPoint, out _))
        {
            if (slot.Volume is not null)
            {
                SliceProjection? registeredVolumeProjection = FindBestVolumeProjection(slot, sourceMetadata, registeredPatientPoint, allowUnrelatedMetadata: true);
                if (registeredVolumeProjection is not null)
                {
                    return registeredVolumeProjection;
                }
            }

            SliceProjection? registeredLegacyProjection = FindBestLegacyProjection(slot, sourceMetadata, registeredPatientPoint, allowUnrelatedMetadata: true);
            if (registeredLegacyProjection is not null)
            {
                return registeredLegacyProjection;
            }
        }

        return FindBestLegacyProjection(slot, sourceMetadata, patientPoint, allowUnrelatedMetadata: false);
    }

    private SliceProjection? FindBestLegacyProjection(ViewportSlot slot, DicomSpatialMetadata sourceMetadata, SpatialVector3D patientPoint, bool allowUnrelatedMetadata)
    {
        if (slot.Series is null)
        {
            return null;
        }

        SliceProjection? best = null;

        for (int index = 0; index < slot.Series.Instances.Count; index++)
        {
            DicomSpatialMetadata? metadata = GetSpatialMetadata(slot.Series.Instances[index]);
            if (metadata is null || (!allowUnrelatedMetadata && !metadata.HasSameFrameOfReference(sourceMetadata)))
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

    private static SliceProjection? FindBestVolumeProjection(ViewportSlot slot, DicomSpatialMetadata sourceMetadata, SpatialVector3D patientPoint, bool allowUnrelatedMetadata = false)
    {
        if (slot.Volume is null)
        {
            return null;
        }

        int sliceIndex = GetVolumeSliceIndexForPatientPoint(slot.Volume, slot.Panel.VolumeOrientation, patientPoint);
        DicomSpatialMetadata metadata = VolumeReslicer.GetSliceSpatialMetadata(slot.Volume, slot.Panel.VolumeOrientation, sliceIndex);
        if (!allowUnrelatedMetadata && !metadata.HasSameFrameOfReference(sourceMetadata))
        {
            return null;
        }

        Point imagePoint = metadata.PixelPointFromPatient(patientPoint);
        double distance = metadata.DistanceToPlane(patientPoint);
        bool containsPoint = metadata.ContainsImagePoint(imagePoint, tolerance: 1.0);
        return new SliceProjection(sliceIndex, imagePoint, distance, containsPoint);
    }

    private static int GetVolumeSliceIndexForPatientPoint(SeriesVolume volume, SliceOrientation orientation, SpatialVector3D patientPoint)
    {
        SpatialVector3D relative = patientPoint - volume.Origin;

        double rawIndex = orientation switch
        {
            SliceOrientation.Axial => relative.Dot(volume.Normal) / volume.SpacingZ,
            SliceOrientation.Coronal => relative.Dot(volume.ColumnDirection) / volume.SpacingY,
            SliceOrientation.Sagittal => relative.Dot(volume.RowDirection) / volume.SpacingX,
            _ => relative.Dot(volume.Normal) / volume.SpacingZ,
        };

        int maxIndex = VolumeReslicer.GetSliceCount(volume, orientation) - 1;
        return Math.Clamp((int)Math.Round(rawIndex), 0, maxIndex);
    }

    private static DicomSpatialMetadata? GetReferenceMetadataForTarget(
        DicomSpatialMetadata sourceMetadata,
        SeriesVolume? sourceVolume,
        ViewportSlot targetSlot,
        DicomSpatialMetadata targetMetadata)
    {
        if (sourceMetadata.HasSameFrameOfReference(targetMetadata))
        {
            return sourceMetadata;
        }

        if (sourceVolume is null || targetSlot.Volume is null ||
            !VolumeRegistrationService.TryGetRegistration(sourceVolume, targetSlot.Volume, out VolumeTranslationRegistration registration))
        {
            return null;
        }

        return sourceMetadata with
        {
            Origin = sourceMetadata.Origin + registration.Translation,
            FrameOfReferenceUid = targetMetadata.FrameOfReferenceUid,
            AcquisitionNumber = targetMetadata.AcquisitionNumber,
        };
    }

    private static bool ArePlanesParallel(DicomSpatialMetadata first, DicomSpatialMetadata second) =>
        Math.Abs(first.Normal.Dot(second.Normal)) >= ParallelPlaneDotThreshold;

    private static bool TryBuildCutlineSegment(DicomSpatialMetadata sourcePlane, DicomSpatialMetadata targetPlane, out Point start, out Point end)
    {
        start = default;
        end = default;

        double width = Math.Max(0, targetPlane.Width - 1);
        double height = Math.Max(0, targetPlane.Height - 1);
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        double a = sourcePlane.Normal.Dot(targetPlane.RowDirection) * targetPlane.ColumnSpacing;
        double b = sourcePlane.Normal.Dot(targetPlane.ColumnDirection) * targetPlane.RowSpacing;
        double c = sourcePlane.Normal.Dot(targetPlane.Origin - sourcePlane.Origin);

        if (Math.Abs(a) <= CutlineEdgeTolerance && Math.Abs(b) <= CutlineEdgeTolerance)
        {
            return false;
        }

        var candidates = new List<Point>(4);

        if (Math.Abs(b) > CutlineEdgeTolerance)
        {
            AddCutlineCandidate(candidates, 0, (-c) / b, width, height);
            AddCutlineCandidate(candidates, width, (-(a * width) - c) / b, width, height);
        }

        if (Math.Abs(a) > CutlineEdgeTolerance)
        {
            AddCutlineCandidate(candidates, (-c) / a, 0, width, height);
            AddCutlineCandidate(candidates, (-(b * height) - c) / a, height, width, height);
        }

        if (candidates.Count < 2)
        {
            return false;
        }

        double maxDistanceSquared = double.MinValue;
        for (int i = 0; i < candidates.Count - 1; i++)
        {
            for (int j = i + 1; j < candidates.Count; j++)
            {
                Point candidateStart = candidates[i];
                Point candidateEnd = candidates[j];
                double dx = candidateEnd.X - candidateStart.X;
                double dy = candidateEnd.Y - candidateStart.Y;
                double distanceSquared = (dx * dx) + (dy * dy);
                if (distanceSquared > maxDistanceSquared)
                {
                    maxDistanceSquared = distanceSquared;
                    start = candidateStart;
                    end = candidateEnd;
                }
            }
        }

        return maxDistanceSquared > 1.0;
    }

    private static void AddCutlineCandidate(List<Point> candidates, double x, double y, double width, double height)
    {
        if (double.IsNaN(x) || double.IsInfinity(x) || double.IsNaN(y) || double.IsInfinity(y))
        {
            return;
        }

        Point candidate = new(x, y);
        if (candidate.X < -CutlineEdgeTolerance || candidate.X > width + CutlineEdgeTolerance ||
            candidate.Y < -CutlineEdgeTolerance || candidate.Y > height + CutlineEdgeTolerance)
        {
            return;
        }

        candidate = new Point(
            Math.Clamp(candidate.X, 0, width),
            Math.Clamp(candidate.Y, 0, height));

        if (candidates.Any(existing => Math.Abs(existing.X - candidate.X) <= CutlineEdgeTolerance && Math.Abs(existing.Y - candidate.Y) <= CutlineEdgeTolerance))
        {
            return;
        }

        candidates.Add(candidate);
    }

    private DicomSpatialMetadata? GetSpatialMetadata(InstanceRecord instance)
    {
        if (!IsLocalInstance(instance))
        {
            return null;
        }

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

    private void Clear3DCursor(bool broadcastToPeerViewers = true)
    {
        foreach (ViewportSlot slot in _slots)
        {
            slot.Panel.Set3DCursorOverlay(null);
        }

        if (!broadcastToPeerViewers || !_linkedViewSyncEnabled)
        {
            return;
        }

        List<StudyViewerWindow> peers;
        lock (s_openViewerSyncLock)
        {
            peers = s_openViewerWindows
                .Where(window => !ReferenceEquals(window, this)
                    && window._linkedViewSyncEnabled
                    && HasLinkedSyncAffinityTo(window))
                .ToList();
        }

        foreach (StudyViewerWindow peer in peers)
        {
            peer.Clear3DCursor(broadcastToPeerViewers: false);
            peer.UpdateStatus();
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
        modifiers.HasFlag(KeyModifiers.Shift) || _is3DCursorToolArmed;

    private string BuildStatusText(ViewportSlot? slot)
    {
        string toolText = $"Action: {GetActionToolbarModeLabel()}";
        string cursorText = _is3DCursorToolArmed
            ? "3D cursor: click a viewport"
            : "Hold SHIFT for 3D cursor";
        string measurementText = $"Measure: {GetMeasurementToolLabel()}";
        string linkedText = _linkedViewSyncEnabled && slot?.CurrentSpatialMetadata is not null
            ? $"Linked: {GetLinkedViewCount(slot)}"
            : "Linked: off";

        if (slot?.Series is null)
        {
            return $"{toolText}   {measurementText}   {linkedText}   {cursorText}";
        }

        int total = GetSeriesTotalCount(slot.Series);
        int loaded = GetSeriesLoadedCount(slot.Series);
        bool currentAvailable = slot.Series.Instances.Count > 0
            && slot.InstanceIndex >= 0
            && slot.InstanceIndex < slot.Series.Instances.Count
            && IsLocalInstance(slot.Series.Instances[slot.InstanceIndex]);
        string retrievalText = _remoteRetrievalSession is null
            ? string.Empty
            : currentAvailable
                ? $"   Loaded {loaded}/{total}"
                : $"   Retrieving image {slot.InstanceIndex + 1}... ({loaded}/{total} local)";

        string projectionText = slot.Panel.IsVolumeBound
            ? $"   {slot.Panel.OrientationLabel}   {slot.Panel.ProjectionModeLabel} {slot.Panel.ProjectionThicknessMm:F1} mm"
            : string.Empty;

        return $"{slot.Series.Modality}   Series {slot.Series.SeriesNumber}   Image {slot.InstanceIndex + 1}/{total}{projectionText}   {toolText}   {measurementText}{retrievalText}   {linkedText}   {cursorText}";
    }

    private int GetLinkedViewCount(ViewportSlot sourceSlot)
    {
        DicomSpatialMetadata? sourceMetadata = sourceSlot.CurrentSpatialMetadata;
        if (sourceMetadata is null)
        {
            return 0;
        }

        int count = _slots.Count(slot =>
            slot.Series is not null
            && slot.CurrentSpatialMetadata is not null
            && (slot.CurrentSpatialMetadata.IsCompatibleWith(sourceMetadata)
                || (sourceSlot.Volume is not null && slot.Volume is not null && VolumeRegistrationService.TryGetRegistration(sourceSlot.Volume, slot.Volume, out _))));

        lock (s_openViewerSyncLock)
        {
            count += s_openViewerWindows
                .Where(window => !ReferenceEquals(window, this)
                    && window._linkedViewSyncEnabled
                    && HasLinkedSyncAffinityTo(window))
                .Sum(window => window._slots.Count(slot =>
                    slot.Series is not null
                    && slot.CurrentSpatialMetadata is not null
                    && (slot.CurrentSpatialMetadata.IsCompatibleWith(sourceMetadata)
                        || (sourceSlot.Volume is not null && slot.Volume is not null && VolumeRegistrationService.TryGetRegistration(sourceSlot.Volume, slot.Volume, out _)))));
        }

        return Math.Max(0, count - 1);
    }

    private bool HasLinkedSyncAffinityTo(StudyViewerWindow other)
    {
        if (string.Equals(_context.StudyDetails.Study.StudyInstanceUid, other._context.StudyDetails.Study.StudyInstanceUid, StringComparison.Ordinal))
        {
            return true;
        }

        string patientId = _context.StudyDetails.Study.PatientId?.Trim() ?? string.Empty;
        string otherPatientId = other._context.StudyDetails.Study.PatientId?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(patientId) && !string.IsNullOrWhiteSpace(otherPatientId))
        {
            return string.Equals(patientId, otherPatientId, StringComparison.OrdinalIgnoreCase);
        }

        string patientName = _context.StudyDetails.Study.PatientName?.Trim() ?? string.Empty;
        string otherPatientName = other._context.StudyDetails.Study.PatientName?.Trim() ?? string.Empty;
        string patientBirthDate = _context.StudyDetails.Study.PatientBirthDate?.Trim() ?? string.Empty;
        string otherPatientBirthDate = other._context.StudyDetails.Study.PatientBirthDate?.Trim() ?? string.Empty;

        return !string.IsNullOrWhiteSpace(patientName)
            && string.Equals(patientName, otherPatientName, StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(patientBirthDate)
                || string.IsNullOrWhiteSpace(otherPatientBirthDate)
                || string.Equals(patientBirthDate, otherPatientBirthDate, StringComparison.Ordinal));
    }

    private void RequestSlotPriority(ViewportSlot? slot, int direction = 0, bool exactFocusOnly = false)
    {
        if (slot?.Series is null || _remoteRetrievalSession is null)
        {
            return;
        }

        if (exactFocusOnly)
        {
            _ = _remoteRetrievalSession.RequestFocusedImageAsync(slot.Series.SeriesInstanceUid, slot.InstanceIndex, direction);
            _ = _remoteRetrievalSession.PrioritizeSeriesAsync(slot.Series.SeriesInstanceUid, slot.InstanceIndex, 0, direction);
            return;
        }

        if (!_remoteRetrievalSession.IsBackgroundRetrievalEnabled)
        {
            _ = _remoteRetrievalSession.RequestFocusedImageAsync(slot.Series.SeriesInstanceUid, slot.InstanceIndex, direction);
            return;
        }

        _ = _remoteRetrievalSession.PrioritizeSeriesAsync(slot.Series.SeriesInstanceUid, slot.InstanceIndex, 6, direction);
        if (direction == 0)
        {
            _ = _remoteRetrievalSession.PrioritizeAdjacentSeriesAsync(slot.Series.SeriesInstanceUid, 1);
            return;
        }

        QueueAdjacentSeriesPriority(slot.Series.SeriesInstanceUid);
    }

    private void QueueAdjacentSeriesPriority(string seriesInstanceUid)
    {
        if (_remoteRetrievalSession is null || string.IsNullOrWhiteSpace(seriesInstanceUid))
        {
            return;
        }

        _adjacentPriorityDebounceCancellation?.Cancel();
        _adjacentPriorityDebounceCancellation?.Dispose();

        var cancellation = new CancellationTokenSource();
        _adjacentPriorityDebounceCancellation = cancellation;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(AdjacentPriorityDebounceMs, cancellation.Token);
                if (cancellation.IsCancellationRequested || _remoteRetrievalSession is null)
                {
                    return;
                }

                _ = _remoteRetrievalSession.PrioritizeAdjacentSeriesAsync(seriesInstanceUid, 1, cancellation.Token);
            }
            catch (OperationCanceledException)
            {
            }
        }, cancellation.Token);
    }

    private void RequestSeriesThumbnail(SeriesRecord series, int focusIndex)
    {
        if (_remoteRetrievalSession is null)
        {
            return;
        }

        _ = _remoteRetrievalSession.RequestFocusedImageAsync(series.SeriesInstanceUid, focusIndex);
    }

    private void RequestSeriesPriority(SeriesRecord series, int focusIndex, int direction = 0)
    {
        if (_remoteRetrievalSession is null)
        {
            return;
        }

        _ = _remoteRetrievalSession.PrioritizeSeriesAsync(series.SeriesInstanceUid, focusIndex, 6, direction);
        _ = _remoteRetrievalSession.PrioritizeAdjacentSeriesAsync(series.SeriesInstanceUid, 1);
    }

    private void OnRemoteStudyChanged()
    {
        Dispatcher.UIThread.Post(RefreshRemoteStudyState);
    }

    private void RefreshRemoteStudyState()
    {
        foreach (ViewportSlot slot in _slots)
        {
            if (slot.Series is null)
            {
                continue;
            }

            slot.Panel.StackItemCount = GetSeriesTotalCount(slot.Series);
            if (slot.Series.Instances.Count == 0)
            {
                slot.Panel.ClearImage();
                continue;
            }

            slot.InstanceIndex = Math.Clamp(slot.InstanceIndex, 0, GetSeriesTotalCount(slot.Series) - 1);
            InstanceRecord current = slot.Series.Instances[Math.Clamp(slot.InstanceIndex, 0, slot.Series.Instances.Count - 1)];
            if (!IsLocalInstance(current))
            {
                slot.Panel.ClearImage();
                continue;
            }

            if (!string.Equals(slot.Panel.FilePath, current.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                LoadSlot(slot);
            }
        }

        RefreshThumbnailStrip(_selectedPriorStudy is null ? _activeSlot?.Series : null);
        UpdateStatus();
    }

    private void OnViewerClosed(object? sender, EventArgs e)
    {
        UnregisterForLinkedViewSync();
        CancelPriorPreviewLoad();
        _priorLookupCancellation.Cancel();
        _priorLookupCancellation.Dispose();
        _adjacentPriorityDebounceCancellation?.Cancel();
        _adjacentPriorityDebounceCancellation?.Dispose();
        _adjacentPriorityDebounceCancellation = null;
        Opened -= OnViewerOpened;

        if (_remoteRetrievalSession is not null)
        {
            _remoteRetrievalSession.StudyChanged -= OnRemoteStudyChanged;
        }

        Closed -= OnViewerClosed;
    }

    private void ShowToast(string message, ToastSeverity severity, TimeSpan? duration = null)
    {
        if (string.IsNullOrWhiteSpace(message) || ToastItemsControl is null)
        {
            return;
        }

        if (_toastNotifications.Any(toast => string.Equals(toast.Message, message, StringComparison.Ordinal)))
        {
            return;
        }

        ToastNotificationItem toast = CreateToast(message, severity);
        _toastNotifications.Add(toast);
        while (_toastNotifications.Count > 4)
        {
            _toastNotifications.RemoveAt(0);
        }

        _ = DismissToastAsync(toast.Id, duration ?? GetToastDuration(severity));
    }

    private async Task DismissToastAsync(Guid toastId, TimeSpan duration)
    {
        try
        {
            await Task.Delay(duration, _priorLookupCancellation.Token);
        }
        catch
        {
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            ToastNotificationItem? toast = _toastNotifications.FirstOrDefault(item => item.Id == toastId);
            if (toast is not null)
            {
                _toastNotifications.Remove(toast);
            }
        });
    }

    private void RegisterForLinkedViewSync()
    {
        lock (s_openViewerSyncLock)
        {
            s_openViewerWindows.Add(this);
        }
    }

    private void UnregisterForLinkedViewSync()
    {
        lock (s_openViewerSyncLock)
        {
            s_openViewerWindows.Remove(this);
        }
    }

    private static TimeSpan GetToastDuration(ToastSeverity severity) => severity switch
    {
        ToastSeverity.Success => TimeSpan.FromSeconds(4),
        ToastSeverity.Warning => TimeSpan.FromSeconds(6),
        ToastSeverity.Error => TimeSpan.FromSeconds(8),
        _ => TimeSpan.FromSeconds(4),
    };

    private static ToastNotificationItem CreateToast(string message, ToastSeverity severity)
    {
        return severity switch
        {
            ToastSeverity.Success => new ToastNotificationItem
            {
                Icon = "✓",
                Message = message,
                Background = new SolidColorBrush(Color.Parse("#F0174D28")),
                BorderBrush = new SolidColorBrush(Color.Parse("#FF2F8F3A")),
                Foreground = Brushes.White,
            },
            ToastSeverity.Warning => new ToastNotificationItem
            {
                Icon = "⚠",
                Message = message,
                Background = new SolidColorBrush(Color.Parse("#F07A4E00")),
                BorderBrush = new SolidColorBrush(Color.Parse("#FFFFB14A")),
                Foreground = Brushes.White,
            },
            ToastSeverity.Error => new ToastNotificationItem
            {
                Icon = "✕",
                Message = message,
                Background = new SolidColorBrush(Color.Parse("#F07D2222")),
                BorderBrush = new SolidColorBrush(Color.Parse("#FFFF7575")),
                Foreground = Brushes.White,
            },
            _ => new ToastNotificationItem
            {
                Icon = "ℹ",
                Message = message,
                Background = new SolidColorBrush(Color.Parse("#F0215D8B")),
                BorderBrush = new SolidColorBrush(Color.Parse("#FF65B7F7")),
                Foreground = Brushes.White,
            },
        };
    }

    private static int GetSeriesTotalCount(SeriesRecord series) => Math.Max(series.InstanceCount, series.Instances.Count);

    private static int GetSeriesLoadedCount(SeriesRecord series) => series.Instances.Count(IsLocalInstance);

    private static bool IsLocalInstance(InstanceRecord instance) =>
        !string.IsNullOrWhiteSpace(instance.FilePath) && File.Exists(instance.FilePath);

    private static InstanceRecord? GetBestLocalRepresentativeInstance(SeriesRecord series)
    {
        if (series.Instances.Count == 0)
        {
            return null;
        }

        int targetIndex = GetRepresentativeInstanceIndex(series);
        List<InstanceRecord> locals = series.Instances.Where(IsLocalInstance).ToList();
        if (locals.Count == 0)
        {
            return null;
        }

        return locals
            .OrderBy(instance => Math.Abs(instance.InstanceNumber - targetIndex))
            .ThenBy(instance => instance.InstanceNumber)
            .FirstOrDefault();
    }

    private static string GetBestThumbnailPath(SeriesRecord series) => GetBestLocalRepresentativeInstance(series)?.FilePath ?? string.Empty;

    private MouseWheelMode GetMouseWheelModeForAction() => MouseWheelMode.StackScroll;

    private void SetActionToolbarMode(ActionToolbarMode mode)
    {
        bool leavingToolsMode = _actionToolbarMode == ActionToolbarMode.Tools && mode != ActionToolbarMode.Tools;
        _actionToolbarMode = mode;

        if (leavingToolsMode)
        {
            foreach (ViewportSlot slot in _slots)
            {
                slot.Panel.CancelMeasurementInteraction();
            }
        }

        UpdateActionModeButtons();
        ApplyActionModeToPanels();
        RefreshMeasurementPanels();
        UpdateStatus();
    }

    private void UpdateActionModeButtons()
    {
        ActionScrollButton.IsChecked = _actionToolbarMode == ActionToolbarMode.ScrollStack;
        ActionZoomPanButton.IsChecked = _actionToolbarMode == ActionToolbarMode.ZoomPan;
        ActionWindowButton.IsChecked = _actionToolbarMode == ActionToolbarMode.Window;
        ActionToolsButton.IsChecked = _actionToolbarMode == ActionToolbarMode.Tools;
        ActionLayoutButton.IsChecked = _actionToolbarMode == ActionToolbarMode.Layout;
        LinkedSyncToggleButton.IsChecked = _linkedViewSyncEnabled;
        OverlayToggleButton.IsChecked = _overlayEnabled;
        if (ToolboxOverlayToggleButton is not null)
        {
            ToolboxOverlayToggleButton.IsChecked = _overlayEnabled;
        }

        if (ToolboxLinkedSyncToggleButton is not null)
        {
            ToolboxLinkedSyncToggleButton.IsChecked = _linkedViewSyncEnabled;
        }
    }

    private void ApplyActionModeToPanels()
    {
        MouseWheelMode mode = GetMouseWheelModeForAction();
        foreach (ViewportSlot slot in _slots)
        {
            slot.Panel.WheelMode = mode;
            slot.Panel.ActionMode = _actionToolbarMode;
            slot.Panel.ShowOverlay = _overlayEnabled;
        }
    }

    private void ApplyOverlayState()
    {
        foreach (ViewportSlot slot in _slots)
        {
            slot.Panel.ShowOverlay = _overlayEnabled;
        }

        UpdateActionModeButtons();
        UpdateStatus();
    }

    private string GetActionToolbarModeLabel() => "Navigate";

    private void ShowActionToolbar()
    {
        ActionToolbarBorder.Opacity = 1;
        ActionToolbarBorder.IsHitTestVisible = true;
    }

    private void HideActionToolbar()
    {
        if (_isActionToolbarPointerOver || IsAnyActionPopupOpen())
        {
            return;
        }

        ActionToolbarBorder.Opacity = 0;
        ActionToolbarBorder.IsHitTestVisible = false;
    }

    private void RestartActionToolbarHideTimer()
    {
        ShowActionToolbar();
        _actionToolbarHideTimer.Stop();
        if (!_isActionToolbarPointerOver && !IsAnyActionPopupOpen())
        {
            _actionToolbarHideTimer.Start();
        }
    }

    private bool IsAnyActionPopupOpen() =>
        WindowPresetPopup.IsOpen || ToolsPopup.IsOpen || LayoutPopup.IsOpen || ViewportToolboxPopup.IsOpen;

    private void CloseAllActionPopups(Popup? except = null)
    {
        if (!ReferenceEquals(WindowPresetPopup, except))
        {
            WindowPresetPopup.IsOpen = false;
        }

        if (!ReferenceEquals(ToolsPopup, except))
        {
            ToolsPopup.IsOpen = false;
        }

        if (!ReferenceEquals(LayoutPopup, except))
        {
            LayoutPopup.IsOpen = false;
        }

        if (!ReferenceEquals(ViewportToolboxPopup, except))
        {
            ViewportToolboxPopup.IsOpen = false;
        }
    }

    private void TogglePopup(Popup popup, Control placementTarget)
    {
        bool shouldOpen = !popup.IsOpen;
        CloseAllActionPopups(popup);
        popup.PlacementTarget = placementTarget;
        popup.IsOpen = shouldOpen;
        ShowActionToolbar();

        if (shouldOpen)
        {
            _actionToolbarHideTimer.Stop();
        }
        else
        {
            RestartActionToolbarHideTimer();
        }
    }

    private void ApplyLayoutFromTag(string? tag)
    {
        if (!TryParseLayoutSpec(tag, out List<int> rowLayout))
        {
            return;
        }

        Clear3DCursor();
        ApplyLayout(rowLayout);
        ResetViewportsForLayoutChange();
    }

    private void ResetViewportsForLayoutChange(bool broadcastToPeerViewers = true)
    {
        _viewportLayoutResetTimer.Stop();

        List<StudyViewerWindow> peers = [];
        if (broadcastToPeerViewers && _linkedViewSyncEnabled)
        {
            lock (s_openViewerSyncLock)
            {
                peers = s_openViewerWindows
                    .Where(window => !ReferenceEquals(window, this)
                        && window._linkedViewSyncEnabled
                        && HasLinkedSyncAffinityTo(window))
                    .ToList();
            }
        }

        SuspendLinkedSync();
        try
        {
            _pendingLinkedSyncContext = null;
            SetLinkedReferenceSource(null, null, null);

            foreach (ViewportSlot slot in _slots)
            {
                if (!slot.Panel.IsImageLoaded)
                {
                    slot.LastLinkedSyncSignature = null;
                    continue;
                }

                slot.Panel.ApplyFitToWindow();
                slot.ViewState = slot.Panel.CaptureDisplayState();
                slot.LastLinkedSyncSignature = BuildLinkedSyncSignature(slot, slot.Panel);
            }
        }
        finally
        {
            ResumeLinkedSync();
        }

        Clear3DCursor(broadcastToPeerViewers: false);
        RefreshLinkedReferenceLines();
        UpdateStatus();

        foreach (StudyViewerWindow peer in peers)
        {
            peer.ResetViewportsForLayoutChange(broadcastToPeerViewers: false);
        }
    }

    private void SuspendLinkedSync()
    {
        _linkedSyncSuspendCount++;
    }

    private void ResumeLinkedSync()
    {
        if (_linkedSyncSuspendCount > 0)
        {
            _linkedSyncSuspendCount--;
        }
    }

    private bool IsLinkedSyncSuspended() => _linkedSyncSuspendCount > 0;

    private void ApplyWindowPreset(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return;
        }

        if (string.Equals(tag, "RESET", StringComparison.OrdinalIgnoreCase))
        {
            ApplyToActiveOrAll(panel => panel.ResetWindowLevel());
            return;
        }

        string[] parts = tag.Split('|');
        if (parts.Length != 2 ||
            !double.TryParse(parts[0], out double center) ||
            !double.TryParse(parts[1], out double width))
        {
            return;
        }

        ApplyToActiveOrAll(panel => panel.SetWindowLevel(center, width));
    }

    private void OnActionToolbarHideTimerTick(object? sender, EventArgs e)
    {
        _actionToolbarHideTimer.Stop();
        HideActionToolbar();
    }

    private void OnViewerContentPointerMoved(object? sender, PointerEventArgs e)
    {
        RestartActionToolbarHideTimer();

        Point position = e.GetPosition(ViewerContentHost);
        if (!_isWorkspaceDockVisible && IsPointerNearWorkspaceRevealArea(position))
        {
            ShowWorkspaceDock(restartHideTimer: true);
        }
    }

    private void OnViewerContentPointerEntered(object? sender, PointerEventArgs e)
    {
        RestartActionToolbarHideTimer();

        Point position = e.GetPosition(ViewerContentHost);
        if (!_isWorkspaceDockVisible && IsPointerNearWorkspaceRevealArea(position))
        {
            ShowWorkspaceDock(restartHideTimer: true);
        }
    }

    private void OnViewerContentPointerExited(object? sender, PointerEventArgs e)
    {
        if (!_isActionToolbarPointerOver && !IsAnyActionPopupOpen())
        {
            _actionToolbarHideTimer.Stop();
            _actionToolbarHideTimer.Start();
        }

        RestartWorkspaceDockHideTimer();
    }

    private void OnActionToolbarPointerEntered(object? sender, PointerEventArgs e)
    {
        _isActionToolbarPointerOver = true;
        ShowActionToolbar();
        _actionToolbarHideTimer.Stop();
    }

    private void OnActionToolbarPointerExited(object? sender, PointerEventArgs e)
    {
        _isActionToolbarPointerOver = false;
        RestartActionToolbarHideTimer();
    }

    private void OnActionScrollClick(object? sender, RoutedEventArgs e)
    {
        CloseAllActionPopups();
        SetActionToolbarMode(ActionToolbarMode.ScrollStack);
        RestartActionToolbarHideTimer();
    }

    private void OnActionZoomPanClick(object? sender, RoutedEventArgs e)
    {
        CloseAllActionPopups();
        SetActionToolbarMode(ActionToolbarMode.ZoomPan);
        RestartActionToolbarHideTimer();
    }

    private void OnActionWindowClick(object? sender, RoutedEventArgs e)
    {
        SetActionToolbarMode(ActionToolbarMode.Window);
        TogglePopup(WindowPresetPopup, ActionWindowButton);
    }

    private void OnActionToolsClick(object? sender, RoutedEventArgs e)
    {
        SetActionToolbarMode(ActionToolbarMode.Tools);
        TogglePopup(ToolsPopup, ActionToolsButton);
    }

    private void OnActionLayoutClick(object? sender, RoutedEventArgs e)
    {
        SetActionToolbarMode(ActionToolbarMode.Layout);
        TogglePopup(LayoutPopup, ActionLayoutButton);
    }

    private void OnOverlayToggleClick(object? sender, RoutedEventArgs e)
    {
        _overlayEnabled = OverlayToggleButton.IsChecked != false;
        ApplyOverlayState();
        RestartActionToolbarHideTimer();
    }

    private void OnLinkedSyncToggleClick(object? sender, RoutedEventArgs e)
    {
        _linkedViewSyncEnabled = LinkedSyncToggleButton.IsChecked != false;
        UpdateActionModeButtons();
        SaveViewerSettings();
        if (_linkedViewSyncEnabled)
        {
            SetLinkedReferenceSource(_activeSlot, _activeSlot?.Volume, _activeSlot?.CurrentSpatialMetadata);
            SynchronizeLinkedViews(_activeSlot);
        }
        else
        {
            ClearLinkedReferenceLines();
        }

        RestartActionToolbarHideTimer();
    }

    private void OnStudyBrowserClick(object? sender, RoutedEventArgs e)
    {
        CloseViewportToolbox();
        ShowWorkspaceDock(restartHideTimer: true);
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow is Window browserWindow)
        {
            if (browserWindow.WindowState == WindowState.Minimized)
            {
                browserWindow.WindowState = WindowState.Normal;
            }

            browserWindow.Show();
            browserWindow.Activate();
        }

        RestartActionToolbarHideTimer();
    }

    private void OnWorkspaceLayoutClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control control)
        {
            CloseViewportToolbox();
            ShowWorkspaceDock(restartHideTimer: false);
            _workspaceDockHideTimer.Stop();
            SetActionToolbarMode(ActionToolbarMode.Layout);
            TogglePopup(LayoutPopup, control);
        }
    }

    private void LoadViewerSettings(IReadOnlyList<int> defaultLayout)
    {
        _savedCustomLayouts = [];
        _currentLayoutSpec = [.. defaultLayout];

        try
        {
            if (string.IsNullOrWhiteSpace(_viewerSettingsPath) || !File.Exists(_viewerSettingsPath))
            {
                return;
            }

            StudyViewerSettings? settings = JsonSerializer.Deserialize<StudyViewerSettings>(File.ReadAllText(_viewerSettingsPath));
            if (settings is not null)
            {
                _linkedViewSyncEnabled = settings.LinkedViewSyncEnabled;
                _savedCustomLayouts = settings.SavedCustomLayouts?
                    .Where(layout => TryParseLayoutSpec(layout, out _))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(10)
                    .ToList() ?? [];

                if (TryParseLayoutSpec(settings.LastLayoutSpec, out List<int> persistedLayout))
                {
                    _currentLayoutSpec = persistedLayout;
                }
            }
        }
        catch
        {
            _linkedViewSyncEnabled = true;
            _savedCustomLayouts = [];
            _currentLayoutSpec = [.. defaultLayout];
        }
    }

    private void SaveViewerSettings()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_viewerSettingsPath))
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_viewerSettingsPath) ?? string.Empty);
            StudyViewerSettings settings = new()
            {
                LinkedViewSyncEnabled = _linkedViewSyncEnabled,
                SavedCustomLayouts = [.. _savedCustomLayouts],
                LastLayoutSpec = LayoutSpecToString(_currentLayoutSpec),
            };

            File.WriteAllText(_viewerSettingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
        }
    }

    private void OnWindowPresetSelected(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button)
        {
            ApplyWindowPreset(button.Tag as string);
        }

        CloseAllActionPopups();
        RestartActionToolbarHideTimer();
    }

    private void OnWindowLutSelected(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string tag && int.TryParse(tag, out int scheme))
        {
            ApplyColorScheme(scheme);
        }

        CloseAllActionPopups();
        RestartActionToolbarHideTimer();
    }

    private void OnLayoutPopupClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button)
        {
            ApplyLayoutFromTag(button.Tag as string);
        }

        CloseAllActionPopups();
        CloseViewportToolbox();
        RestartActionToolbarHideTimer();
    }

    private void OnApplyCustomLayoutClick(object? sender, RoutedEventArgs e) => ApplyCustomLayoutInput(savePreset: false);

    private void OnSaveCustomLayoutClick(object? sender, RoutedEventArgs e) => ApplyCustomLayoutInput(savePreset: true);

    private void OnSavedCustomLayoutClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
        {
            return;
        }

        ApplyLayoutFromTag(button.Tag as string);
        CloseAllActionPopups();
        CloseViewportToolbox();
        RestartActionToolbarHideTimer();
    }

    private void OnCustomLayoutTextBoxKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ApplyCustomLayoutInput(savePreset: false);
            e.Handled = true;
        }
    }

    private void OnAnyActionPopupClosed(object? sender, EventArgs e)
    {
        if (!IsAnyActionPopupOpen())
        {
            RestartActionToolbarHideTimer();
            RestartWorkspaceDockHideTimer();
        }
    }

    private void OnWorkspaceDockHideTimerTick(object? sender, EventArgs e)
    {
        _workspaceDockHideTimer.Stop();
        HideWorkspaceDock();
    }

    private void OnWorkspaceDockPointerEntered(object? sender, PointerEventArgs e)
    {
        _isWorkspaceDockPointerOver = true;
        ShowWorkspaceDock(restartHideTimer: false);
        _workspaceDockHideTimer.Stop();
    }

    private void OnWorkspaceDockPointerExited(object? sender, PointerEventArgs e)
    {
        _isWorkspaceDockPointerOver = false;
        RestartWorkspaceDockHideTimer();
    }

    private void OnWorkspaceDockRevealPointerEntered(object? sender, PointerEventArgs e)
    {
        ShowWorkspaceDock(restartHideTimer: true);
    }

    private void OnWorkspaceDockRevealClick(object? sender, RoutedEventArgs e)
    {
        ShowWorkspaceDock(restartHideTimer: true);
    }

    private void ShowWorkspaceDock(bool restartHideTimer)
    {
        _isWorkspaceDockVisible = true;
        WorkspaceDock.IsVisible = true;
        WorkspaceDockRevealButton.IsVisible = false;

        if (restartHideTimer)
        {
            RestartWorkspaceDockHideTimer();
        }
    }

    private void HideWorkspaceDock()
    {
        if (_isWorkspaceDockPointerOver || IsAnyActionPopupOpen())
        {
            return;
        }

        _isWorkspaceDockVisible = false;
        WorkspaceDock.IsVisible = false;
        WorkspaceDockRevealButton.IsVisible = true;
    }

    private void RestartWorkspaceDockHideTimer()
    {
        _workspaceDockHideTimer.Stop();
        if (_isWorkspaceDockVisible && !_isWorkspaceDockPointerOver && !IsAnyActionPopupOpen())
        {
            _workspaceDockHideTimer.Start();
        }
    }

    private bool IsPointerNearWorkspaceRevealArea(Point position)
    {
        double centerX = ViewerContentHost.Bounds.Width / 2.0;
        return position.Y <= 32 && Math.Abs(position.X - centerX) <= 220;
    }

    private void OnPanelToolboxRequested(ViewportSlot slot)
    {
        SetActiveSlot(slot);

        bool shouldClose = ViewportToolboxPopup.IsOpen && ReferenceEquals(_openToolboxSlot, slot);
        if (shouldClose)
        {
            CloseViewportToolbox();
            return;
        }

        CloseAllActionPopups(ViewportToolboxPopup);
        _openToolboxSlot = slot;
        ViewportToolboxPopup.PlacementTarget = slot.Panel.ToolboxPlacementTarget;
        SyncViewportToolboxState();
        ViewportToolboxPopup.IsOpen = true;
        _actionToolbarHideTimer.Stop();
    }

    private void SyncViewportToolboxState()
    {
        UpdateMeasurementToolButtons();
        UpdateActionModeButtons();
        Update3DCursorToolButton();
    }

    private void CloseViewportToolbox()
    {
        _openToolboxSlot = null;
        ViewportToolboxPopup.IsOpen = false;
    }

    private void OnViewportToolboxPopupClosed(object? sender, EventArgs e)
    {
        _openToolboxSlot = null;
        RestartActionToolbarHideTimer();
    }

    private void OnNavigateToolClick(object? sender, RoutedEventArgs e)
    {
        SetMeasurementTool(MeasurementTool.None);
        CloseViewportToolbox();
        UpdateStatus();
    }

    private void OnToolboxOverlayToggleClick(object? sender, RoutedEventArgs e)
    {
        _overlayEnabled = ToolboxOverlayToggleButton.IsChecked != false;
        ApplyOverlayState();
        SyncViewportToolboxState();
    }

    private void OnToolbox3DCursorClick(object? sender, RoutedEventArgs e)
    {
        _is3DCursorToolArmed = !_is3DCursorToolArmed;
        if (_is3DCursorToolArmed)
        {
            SetMeasurementTool(MeasurementTool.None);
        }
        else
        {
            Clear3DCursor();
        }

        Update3DCursorToolButton();
        UpdateStatus();
    }

    private void Update3DCursorToolButton()
    {
        if (Toolbox3DCursorButton is not null)
        {
            Toolbox3DCursorButton.IsChecked = _is3DCursorToolArmed;
        }
    }

    private void OnToolboxLinkedSyncToggleClick(object? sender, RoutedEventArgs e)
    {
        _linkedViewSyncEnabled = ToolboxLinkedSyncToggleButton.IsChecked != false;
        LinkedSyncToggleButton.IsChecked = _linkedViewSyncEnabled;
        UpdateActionModeButtons();
        SaveViewerSettings();
        if (_linkedViewSyncEnabled)
        {
            SetLinkedReferenceSource(_activeSlot, _activeSlot?.Volume, _activeSlot?.CurrentSpatialMetadata);
            SynchronizeLinkedViews(_activeSlot);
        }
        else
        {
            ClearLinkedReferenceLines();
        }

        UpdateStatus();
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
        public SeriesVolume? Volume { get; set; }
        public string? LastLinkedSyncSignature { get; set; }
    }

    private sealed record ViewportSlotState(
        SeriesRecord? Series,
        int InstanceIndex,
        DicomViewPanel.DisplayState? ViewState,
        DicomSpatialMetadata? CurrentSpatialMetadata,
        SeriesVolume? Volume,
        bool IsActive);

    private sealed record ViewerLayoutState(List<int> RowLayout, List<ViewportSlotState> SlotStates);

    private sealed record SliceProjection(int InstanceIndex, Point ImagePoint, double DistanceToPlane, bool ContainsImagePoint);
    private sealed record PendingLinkedSyncContext(
        ViewportSlot? SourceSlot,
        SeriesVolume? SourceVolume,
        DicomSpatialMetadata SourceMetadata,
        SpatialVector3D PatientPoint,
        DicomViewPanel.NavigationState NavigationState);

    private sealed record ViewportDropTarget(StudyViewerWindow Window, ViewportSlot Slot);

    private sealed class PendingThumbnailDrag(SeriesRecord series, Point startPoint, string thumbnailPath)
    {
        public SeriesRecord Series { get; } = series;
        public Point StartPoint { get; } = startPoint;
        public string ThumbnailPath { get; } = thumbnailPath;
        public bool IsStarted { get; set; }
    }

    private sealed class StudyViewerSettings
    {
        public bool LinkedViewSyncEnabled { get; init; } = true;
        public List<string>? SavedCustomLayouts { get; init; }
        public string? LastLayoutSpec { get; init; }
    }
}

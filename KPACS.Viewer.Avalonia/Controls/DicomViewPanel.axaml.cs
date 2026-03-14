// ------------------------------------------------------------------------------------------------
// KPACS.Viewer - Controls/DicomViewPanel.axaml.cs
// Ported from dview.pas (TdView) — the core DICOM viewer control.
// Avalonia cross-platform version.
//
// This is a basic port of the K-PACS dView component with:
//   - Zoom (mouse wheel, fit-to-window, 1:1, programmatic)
//   - Window/Level (right-click drag, ported from the original TdView contrast tool)
//   - Pan (left-click drag in center zone)
//   - Integrated edge-zoom (left-click drag in outer 1/6th zone)
//   - Color LUT support
//   - 4-corner DICOM overlay text
// ------------------------------------------------------------------------------------------------

using System.Runtime.InteropServices;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using FellowOakDicom;
using FellowOakDicom.Imaging;
using FellowOakDicom.Imaging.Codec;
using KPACS.Viewer.Models;
using KPACS.Viewer.Rendering;
using SpatialVector3D = KPACS.Viewer.Models.Vector3D;

namespace KPACS.Viewer.Controls;

/// <summary>
/// Basic DICOM viewer control with zoom, window/level, and pan.
/// Ported from TdView in dview.pas — Avalonia cross-platform version.
/// </summary>
public partial class DicomViewPanel : UserControl
{
    private static readonly DicomTag SharedFunctionalGroupsSequenceTag = new(0x5200, 0x9229);
    private static readonly DicomTag PerFrameFunctionalGroupsSequenceTag = new(0x5200, 0x9230);
    private static readonly DicomTag FrameVoILutSequenceTag = new(0x0028, 0x9132);

    private static Cursor? s_windowCursor;
    private static readonly object s_windowCursorLock = new();

    public sealed record DisplayState(
        double WindowCenter,
        double WindowWidth,
        double ZoomFactor,
        bool FitToWindow,
        double PanX,
        double PanY,
        int ColorScheme,
        SliceOrientation Orientation,
        VolumeProjectionMode ProjectionMode,
        double ProjectionThicknessMm);

    public sealed record NavigationState(
        double ZoomFactor,
        bool FitToWindow,
        Point CenterImagePoint);

    // ==============================================================================================
    // Image data (ported from TdView fields + DICOMdata record)
    // ==============================================================================================

    private byte[]? _rawPixelData;
    private int _imageWidth;
    private int _imageHeight;
    private int _bitsAllocated = 8;
    private int _bitsStored = 8;
    private bool _isSigned;
    private int _samplesPerPixel = 1;
    private int _planarConfiguration;
    private double _rescaleSlope = 1.0;
    private double _rescaleIntercept;
    private bool _isMonochrome1;
    private string _photometricInterpretation = "MONOCHROME2";
    private readonly List<BitmapOverlayPlane> _bitmapOverlays = [];

    // DICOM metadata for overlay
    private string _patientName = "";
    private string _patientId = "";
    private string _studyDate = "";
    private string _studyDescription = "";
    private string _institution = "";
    private string _modality = "";
    private string _fileName = "";
    private int _frameCount = 1;

    // ==============================================================================================
    // Display state (ported from TdView: FWindowCenter, FWindowWidth, FImageZoomPct, etc.)
    // ==============================================================================================

    private double _windowCenter;
    private double _windowWidth;
    private double _defaultWindowCenter;
    private double _defaultWindowWidth;
    private double _zoomFactor = 1.0;
    private bool _fitToWindow = true;
    private int _colorScheme = 1;
    private double _displayScaleX = 1.0;
    private double _displayScaleY = 1.0;

    // ==============================================================================================
    // Volume rendering state
    // ==============================================================================================

    private SeriesVolume? _volume;
    private SliceOrientation _volumeOrientation = SliceOrientation.Axial;
    private int _volumeSliceIndex;
    private short[]? _volumeSlicePixels;
    private VolumeProjectionMode _projectionMode = VolumeProjectionMode.Mpr;
    private double _projectionThicknessMm = 1.0;
    private bool _isProjectionThicknessDragging;
    private double _projectionDragStartThicknessMm;
    private double _projectionDragStartY;
    private IPointer? _projectionPointer;

    /// <summary>True when this panel is displaying a slice from a bound volume.</summary>
    public bool IsVolumeBound => _volume is not null;

    /// <summary>The currently bound volume, if any.</summary>
    public SeriesVolume? BoundVolume => _volume;

    /// <summary>The current slice orientation when volume-bound.</summary>
    public SliceOrientation VolumeOrientation => _volumeOrientation;

    /// <summary>The current slice index when volume-bound.</summary>
    public int VolumeSliceIndex => _volumeSliceIndex;
    public int VolumeSliceCount => _volume is null ? 0 : VolumeReslicer.GetSliceCount(_volume, _volumeOrientation);
    public VolumeProjectionMode ProjectionMode => _projectionMode;
    public double ProjectionThicknessMm => _projectionThicknessMm;
    public string ProjectionModeLabel => GetProjectionModeLabel(_projectionMode);
    public string OrientationLabel => GetOrientationLabel(_volumeOrientation);

    // ==============================================================================================
    // Rendering
    // ==============================================================================================

    private WriteableBitmap? _displayBitmap;
    private byte[] _lutR = new byte[256];
    private byte[] _lutG = new byte[256];
    private byte[] _lutB = new byte[256];

    // Transforms (created in code — Avalonia doesn't support x:Name on transforms)
    private readonly ScaleTransform _zoomTransform = new ScaleTransform(1, 1);
    private readonly TranslateTransform _panTransform = new TranslateTransform();

    // Shadow fields for pan position — avoids reading back from the Avalonia
    // transform object, which can introduce tiny precision drift that accumulates
    // during rapid incremental operations like edge-zoom.
    private double _panX;
    private double _panY;

    // ==============================================================================================
    // Mouse interaction state (ported from TdView: gXStart, gYStart, gFastSlope, gFastCen, etc.)
    // ==============================================================================================

    private Point _mouseDownPos;
    private bool _isRightDragging;  // window/level
    private bool _isLeftDragging;   // pan or edge-zoom
    private double _startWindowCenter, _startWindowWidth;
    private double _startPanX, _startPanY;
    private bool _isStackDragging;
    private int _lastStackMouseY;

    // Integrated zoom/pan (ported from TdView: IsCursorInZoomRegion, FZoomRegion)
    // Edge zone = outer 1/6th of each dimension → drag to zoom
    // Center zone = inner area → drag to pan
    private bool _isEdgeZoom;        // locked at mouse-down: true = zoom mode, false = pan mode
    private int _lastMouseY;         // for incremental edge-zoom tracking
    private Point? _cursor3DImagePoint;
    private Point? _referenceLineStartImagePoint;
    private Point? _referenceLineEndImagePoint;
    private ActionToolbarMode _actionMode = ActionToolbarMode.ZoomPan;

    // Pointer capture tracking
    private IPointer? _capturedPointer;

    /// <summary>Last error message from LoadFile, if any.</summary>
    public string? LastError { get; private set; }

    // ==============================================================================================
    // Public properties
    // ==============================================================================================

    public double WindowCenter
    {
        get => _windowCenter;
        set { _windowCenter = value; RenderImage(); UpdateOverlay(); WindowChanged?.Invoke(); NotifyViewStateChanged(); }
    }

    public double WindowWidth
    {
        get => _windowWidth;
        set { _windowWidth = Math.Max(1, value); RenderImage(); UpdateOverlay(); WindowChanged?.Invoke(); NotifyViewStateChanged(); }
    }

    public double ZoomFactor => _zoomFactor;
    public int ImageWidth => _imageWidth;
    public int ImageHeight => _imageHeight;
    public string Modality => _modality;
    public string PatientName => _patientName;
    public bool IsImageLoaded => _rawPixelData != null || _volumeSlicePixels != null;
    public int CurrentColorScheme => _colorScheme;
    public string FilePath => _fileName;
    public DicomSpatialMetadata? SpatialMetadata { get; private set; }

    private bool _showOverlay = true;
    public bool ShowOverlay
    {
        get => _showOverlay;
        set
        {
            _showOverlay = value;
            ApplyOverlayVisibility();
        }
    }

    public void SetOverlayStudyInfo(
        string patientName,
        string patientId,
        string studyDate,
        string studyDescription,
        string institution,
        string modality)
    {
        _patientName = patientName ?? string.Empty;
        _patientId = patientId ?? string.Empty;
        _studyDate = studyDate ?? string.Empty;
        _studyDescription = studyDescription ?? string.Empty;
        _institution = institution ?? string.Empty;
        _modality = modality ?? string.Empty;
        UpdateOverlay();
    }

    // ==============================================================================================
    // Events
    // ==============================================================================================

    public event Action? WindowChanged;
    public event Action? ZoomChanged;
    public event Action? ImageLoaded;
    public event Action<int>? StackScrollRequested;
    public event Action? ViewStateChanged;
    public event Action<DicomHoverInfo?>? HoveredImagePointChanged;
    public event Action<DicomImagePointerInfo>? ImagePointPressed;
    public event Action? ImageDoubleClicked;

    public MouseWheelMode WheelMode { get; set; } = MouseWheelMode.Zoom;
    public ActionToolbarMode ActionMode
    {
        get => _actionMode;
        set
        {
            _actionMode = value;
            UpdateInteractiveCursor();
        }
    }
    public int StackItemCount { get; set; } = 1;
    public bool StackSkipImages { get; set; } = true;

    // ==============================================================================================
    // Constructor
    // ==============================================================================================

    public DicomViewPanel()
    {
        InitializeComponent();
        SetColorLutInternal(1);

        // Set up image render transforms in code
        var transformGroup = new TransformGroup();
        transformGroup.Children.Add(_zoomTransform);
        transformGroup.Children.Add(_panTransform);
        DicomImage.RenderTransform = transformGroup;

        // Wire up pointer events (Avalonia uses Pointer* instead of Mouse*)
        RootGrid.PointerPressed += OnPointerPressed;
        RootGrid.PointerReleased += OnPointerReleased;
        RootGrid.PointerMoved += OnPointerMoved;
        RootGrid.PointerWheelChanged += OnPointerWheelChanged;
        RootGrid.PointerExited += OnPointerExited;
        OrientationBadge.PointerPressed += OnOrientationBadgePointerPressed;
        ProjectionBadge.PointerPressed += OnProjectionBadgePointerPressed;
        ProjectionBadge.PointerMoved += OnProjectionBadgePointerMoved;
        ProjectionBadge.PointerReleased += OnProjectionBadgePointerReleased;
        ProjectionBadge.PointerCaptureLost += OnProjectionBadgePointerCaptureLost;

        SizeChanged += OnSizeChanged;
        InitializeOrientationContextMenu();
        InitializeProjectionContextMenu();
        UpdateInteractiveCursor();
    }

    private void InitializeOrientationContextMenu()
    {
        OrientationBadge.ContextMenu = new ContextMenu
        {
            ItemsSource = new Control[]
            {
                CreateOrientationMenuItem("Axial", SliceOrientation.Axial),
                CreateOrientationMenuItem("Coronal", SliceOrientation.Coronal),
                CreateOrientationMenuItem("Sagittal", SliceOrientation.Sagittal),
            }
        };
    }

    private MenuItem CreateOrientationMenuItem(string header, SliceOrientation orientation)
    {
        var item = new MenuItem { Header = header, Tag = orientation };
        item.Click += OnOrientationMenuItemClick;
        return item;
    }

    private void OnOrientationMenuItemClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: SliceOrientation orientation })
        {
            SetVolumeOrientation(orientation);
        }
    }

    private void InitializeProjectionContextMenu()
    {
        ProjectionBadge.ContextMenu = new ContextMenu
        {
            ItemsSource = new Control[]
            {
                CreateProjectionMenuItem("MPR", VolumeProjectionMode.Mpr),
                CreateProjectionMenuItem("MipPR", VolumeProjectionMode.MipPr),
                CreateProjectionMenuItem("MinPR", VolumeProjectionMode.MinPr),
                CreateProjectionMenuItem("MPVRT", VolumeProjectionMode.MpVrt),
            }
        };
    }

    private MenuItem CreateProjectionMenuItem(string header, VolumeProjectionMode mode)
    {
        var item = new MenuItem { Header = header, Tag = mode };
        item.Click += OnProjectionMenuItemClick;
        return item;
    }

    private void OnProjectionMenuItemClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: VolumeProjectionMode mode })
        {
            SetProjectionMode(mode);
        }
    }

    private void UpdateInteractiveCursor(Point? pos = null)
    {
        if (_measurementTool != MeasurementTool.None)
        {
            Cursor = _measurementTool == MeasurementTool.Modify
                ? new Cursor(StandardCursorType.DragMove)
                : new Cursor(StandardCursorType.Cross);
            return;
        }

        if (IsScrollActionMode())
        {
            Cursor = new Cursor(StandardCursorType.SizeNorthSouth);
            return;
        }

        if (IsWindowActionMode())
        {
            Cursor = CreateWindowCursor();
            return;
        }

        Point pointerPosition = pos ?? _mouseDownPos;
        Cursor = IsCursorInZoomRegion(pointerPosition)
            ? new Cursor(StandardCursorType.SizeNorthSouth)
            : new Cursor(StandardCursorType.Hand);
    }

    // ==============================================================================================
    // File Loading (ported from TdView.LoadData)
    // ==============================================================================================

    /// <summary>
    /// Loads a DICOM file for display. Extracts pixel data, metadata, and window presets.
    /// </summary>
    public bool LoadFile(string filePath)
    {
        LastError = null;

        _volume = null;
        _volumeSlicePixels = null;
        _volumeSliceIndex = 0;
        _projectionPointer = null;
        _isProjectionThicknessDragging = false;

        try
        {
            var file = DicomFile.Open(filePath, FellowOakDicom.FileReadOption.ReadAll);
            var dataset = file.Dataset;
            _fileName = filePath;
            SpatialMetadata = DicomSpatialMetadata.FromDataset(dataset, filePath);
            UpdateDisplayGeometry(SpatialMetadata?.ColumnSpacing ?? 1.0, SpatialMetadata?.RowSpacing ?? 1.0);

            // --- Extract image metadata ---
            _imageWidth = dataset.GetSingleValue<int>(DicomTag.Columns);
            _imageHeight = dataset.GetSingleValue<int>(DicomTag.Rows);
            _bitsAllocated = dataset.GetSingleValueOrDefault(DicomTag.BitsAllocated, 8);
            _bitsStored = dataset.GetSingleValueOrDefault(DicomTag.BitsStored, _bitsAllocated);
            _samplesPerPixel = dataset.GetSingleValueOrDefault(DicomTag.SamplesPerPixel, 1);
            _planarConfiguration = dataset.GetSingleValueOrDefault(DicomTag.PlanarConfiguration, 0);
            _isSigned = dataset.GetSingleValueOrDefault(DicomTag.PixelRepresentation, 0) == 1;
            _rescaleSlope = dataset.GetSingleValueOrDefault<double>(DicomTag.RescaleSlope, 1.0);
            _rescaleIntercept = dataset.GetSingleValueOrDefault<double>(DicomTag.RescaleIntercept, 0.0);

            if (_rescaleSlope == 0) _rescaleSlope = 1.0;

            _photometricInterpretation = dataset.GetSingleValueOrDefault(DicomTag.PhotometricInterpretation, "MONOCHROME2").Trim().ToUpperInvariant();
            _isMonochrome1 = _photometricInterpretation.Contains("MONOCHROME1");

            // --- Patient / study metadata for overlay ---
            _patientName = dataset.GetSingleValueOrDefault(DicomTag.PatientName, "");
            _patientId = dataset.GetSingleValueOrDefault(DicomTag.PatientID, "");
            _studyDate = dataset.GetSingleValueOrDefault(DicomTag.StudyDate, "");
            _studyDescription = dataset.GetSingleValueOrDefault(DicomTag.StudyDescription, "");
            _institution = dataset.GetSingleValueOrDefault(DicomTag.InstitutionName, "");
            _modality = dataset.GetSingleValueOrDefault(DicomTag.Modality, "");

            // --- Extract pixel data ---
            if (!dataset.Contains(DicomTag.PixelData))
            {
                LastError = "This DICOM file contains no pixel data.";
                return false;
            }

            // Transcode compressed transfer syntaxes (JPEG, JPEG2000, RLE, etc.)
            var syntax = file.Dataset.InternalTransferSyntax;
            if (syntax.IsEncapsulated)
            {
                file = file.Clone(DicomTransferSyntax.ExplicitVRLittleEndian);
                dataset = file.Dataset;
            }

            var pixelData = DicomPixelData.Create(dataset);
            _frameCount = (int)pixelData.NumberOfFrames;
            var frame = pixelData.GetFrame(0);
            _rawPixelData = frame.Data;

            // Sanity check: verify decompressed buffer matches expected size
            int expectedBytes = ComputeExpectedPixelBytes(_imageWidth, _imageHeight, _samplesPerPixel, _bitsAllocated, _photometricInterpretation);
            if (_rawPixelData.Length < expectedBytes)
            {
                LastError = $"Pixel data size mismatch: got {_rawPixelData.Length} bytes, " +
                    $"expected {expectedBytes} ({_imageWidth}×{_imageHeight}, " +
                    $"{_bitsAllocated}-bit, {_samplesPerPixel} spp).";
                return false;
            }

            LoadBitmapOverlays(dataset);

            // --- Window Center / Width ---
            if (!TryGetDefaultWindowPreset(dataset, out double wc, out double ww))
            {
                (wc, ww) = DicomPixelRenderer.ComputeAutoWindow(
                    _rawPixelData, _imageWidth, _imageHeight,
                    _bitsAllocated, _bitsStored, _isSigned,
                    _samplesPerPixel, _rescaleSlope, _rescaleIntercept);
            }

            _windowCenter = wc;
            _windowWidth = Math.Max(1, ww);
            _defaultWindowCenter = _windowCenter;
            _defaultWindowWidth = _windowWidth;

            // --- Color LUT ---
            if (_isMonochrome1)
                SetColorLutInternal(1);
            else
                SetColorLutInternal(_colorScheme);

            // --- Create display bitmap at native resolution (Avalonia WriteableBitmap) ---
            _displayBitmap = new WriteableBitmap(
                new PixelSize(_imageWidth, _imageHeight),
                new Vector(96, 96),
                Avalonia.Platform.PixelFormat.Bgra8888,
                Avalonia.Platform.AlphaFormat.Opaque);
            DicomImage.Source = _displayBitmap;
            ApplyDisplayImageSize();
            DicomImage.InvalidateMeasure();

            // Hide placeholder
            PlaceholderText.IsVisible = false;

            // Render the image
            RenderImage();

            // Fit to window and center
            _fitToWindow = true;
            ApplyInitialFitToWindow();
            Set3DCursorOverlay(null);
            SetReferenceLineOverlay(null, null);
            ResetMeasurementStateForNewImage();

            UpdateOverlay();
            ImageLoaded?.Invoke();
            WindowChanged?.Invoke();
            ZoomChanged?.Invoke();

            return true;
        }
        catch (Exception ex)
        {
            LastError = $"Error loading DICOM file: {ex.Message}";
            SpatialMetadata = null;
            return false;
        }
    }

    private static bool TryGetDefaultWindowPreset(DicomDataset dataset, out double center, out double width)
    {
        if (TryReadWindowPreset(dataset, out center, out width))
        {
            return true;
        }

        if (TryReadWindowPresetFromFunctionalGroups(dataset, SharedFunctionalGroupsSequenceTag, out center, out width))
        {
            return true;
        }

        if (TryReadWindowPresetFromFunctionalGroups(dataset, PerFrameFunctionalGroupsSequenceTag, out center, out width))
        {
            return true;
        }

        center = 0;
        width = 0;
        return false;
    }

    private static bool TryReadWindowPresetFromFunctionalGroups(DicomDataset dataset, DicomTag groupSequenceTag, out double center, out double width)
    {
        center = 0;
        width = 0;

        if (!dataset.Contains(groupSequenceTag))
        {
            return false;
        }

        DicomSequence sequence = dataset.GetSequence(groupSequenceTag);
        foreach (DicomDataset item in sequence.Items)
        {
            if (!item.Contains(FrameVoILutSequenceTag))
            {
                continue;
            }

            DicomSequence voiSequence = item.GetSequence(FrameVoILutSequenceTag);
            foreach (DicomDataset voiItem in voiSequence.Items)
            {
                if (TryReadWindowPreset(voiItem, out center, out width))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryReadWindowPreset(DicomDataset dataset, out double center, out double width)
    {
        center = 0;
        width = 0;

        if (!TryReadFirstNumericValue(dataset, DicomTag.WindowCenter, out center) ||
            !TryReadFirstNumericValue(dataset, DicomTag.WindowWidth, out width))
        {
            return false;
        }

        return width > 0;
    }

    private static bool TryReadFirstNumericValue(DicomDataset dataset, DicomTag tag, out double value)
    {
        value = 0;

        if (!dataset.Contains(tag))
        {
            return false;
        }

        string? rawValue = dataset.GetString(tag);
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return false;
        }

        foreach (string part in rawValue.Split('\\', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (double.TryParse(part, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out value))
            {
                return true;
            }
        }

        return false;
    }

    public void ClearImage()
    {
        _rawPixelData = null;
        _volume = null;
        _volumeSlicePixels = null;
        _isProjectionThicknessDragging = false;
        _projectionPointer = null;
        _bitmapOverlays.Clear();
        _photometricInterpretation = "MONOCHROME2";
        _planarConfiguration = 0;
        _displayBitmap = null;
        _imageWidth = 0;
        _imageHeight = 0;
        _displayScaleX = 1.0;
        _displayScaleY = 1.0;
        _frameCount = 1;
        _fileName = string.Empty;
        SpatialMetadata = null;
        DicomImage.Source = null;
        DicomImage.Width = double.NaN;
        DicomImage.Height = double.NaN;
        PlaceholderText.IsVisible = true;
        Set3DCursorOverlay(null);
        SetReferenceLineOverlay(null, null);
        PixelLensPanel.IsVisible = false;
        MeasurementOverlay.Children.Clear();
        ResetMeasurementStateForNewImage();
        UpdateOverlay();
        UpdateSecondaryCaptureButton();
    }

    // ==============================================================================================
    // Volume binding — loads a slice from a pre-built SeriesVolume
    // ==============================================================================================

    /// <summary>
    /// Binds this panel to a <see cref="SeriesVolume"/> and displays the given slice.
    /// Replaces any previously loaded file or volume.
    /// </summary>
    public void BindVolume(SeriesVolume volume, SliceOrientation orientation, int sliceIndex)
    {
        _rawPixelData = null; // detach from legacy file path
        _volume = volume;
        _volumeOrientation = orientation;
        _projectionThicknessMm = Math.Max(GetMinimumProjectionThicknessMm(), VolumeReslicer.GetSliceSpacing(volume, orientation));
        _isMonochrome1 = volume.IsMonochrome1;
        _samplesPerPixel = 1;
        _bitsAllocated = 16;
        _bitsStored = 16;
        _planarConfiguration = 0;
        _isSigned = true;
        _rescaleSlope = 1.0;
        _rescaleIntercept = 0.0;
        _photometricInterpretation = volume.IsMonochrome1 ? "MONOCHROME1" : "MONOCHROME2";
        _bitmapOverlays.Clear();

        // Window defaults from volume
        _windowCenter = volume.DefaultWindowCenter;
        _windowWidth = Math.Max(1, volume.DefaultWindowWidth);
        _defaultWindowCenter = _windowCenter;
        _defaultWindowWidth = _windowWidth;

        if (_isMonochrome1)
            SetColorLutInternal(1);
        else
            SetColorLutInternal(_colorScheme);

        ShowVolumeSlice(sliceIndex);
    }

    /// <summary>
    /// Navigates to a different slice within the currently bound volume.
    /// Has no effect if no volume is bound.
    /// </summary>
    public bool ShowVolumeSlice(int sliceIndex)
    {
        if (_volume is null)
            return false;

        int maxSlice = VolumeReslicer.GetSliceCount(_volume, _volumeOrientation) - 1;
        sliceIndex = Math.Clamp(sliceIndex, 0, maxSlice);
        _volumeSliceIndex = sliceIndex;

        // Extract the 2D slice from the volume
        ReslicedImage resliced = VolumeReslicer.RenderSlab(
            _volume,
            _volumeOrientation,
            sliceIndex,
            _projectionThicknessMm,
            _projectionMode);
        _volumeSlicePixels = resliced.Pixels;
        _imageWidth = resliced.Width;
        _imageHeight = resliced.Height;
        _frameCount = VolumeReslicer.GetSliceCount(_volume, _volumeOrientation);
        StackItemCount = _frameCount;
        UpdateDisplayGeometry(resliced.PixelSpacingX, resliced.PixelSpacingY);

        SpatialMetadata = resliced.SpatialMetadata;
        _fileName = SpatialMetadata?.FilePath ?? "";
        // Create or resize the display bitmap if dimensions changed
        if (_displayBitmap is null ||
            _displayBitmap.PixelSize.Width != _imageWidth ||
            _displayBitmap.PixelSize.Height != _imageHeight)
        {
            _displayBitmap = new WriteableBitmap(
                new PixelSize(_imageWidth, _imageHeight),
                new Vector(96, 96),
                Avalonia.Platform.PixelFormat.Bgra8888,
                Avalonia.Platform.AlphaFormat.Opaque);
            DicomImage.Source = _displayBitmap;
            DicomImage.InvalidateMeasure();
        }

        ApplyDisplayImageSize();

        PlaceholderText.IsVisible = false;
        RenderImage();

        if (_fitToWindow)
            ApplyInitialFitToWindow();

        Set3DCursorOverlay(null);
        SetReferenceLineOverlay(null, null);
        ResetMeasurementStateForNewImage();
        UpdateOverlay();
        ImageLoaded?.Invoke();
        WindowChanged?.Invoke();
        ZoomChanged?.Invoke();

        return true;
    }

    /// <summary>
    /// Changes the slice orientation of the currently bound volume and resets
    /// the slice index to the middle of the new axis.
    /// </summary>
    public bool SetVolumeOrientation(SliceOrientation orientation)
    {
        if (_volume is null || _volumeOrientation == orientation)
            return false;

        _volumeOrientation = orientation;
        _projectionThicknessMm = Math.Max(_projectionThicknessMm, GetMinimumProjectionThicknessMm());
        int midSlice = VolumeReslicer.GetSliceCount(_volume, orientation) / 2;
        bool changed = ShowVolumeSlice(midSlice);
        if (changed)
        {
            UpdateOverlay();
            NotifyViewStateChanged();
        }

        return changed;
    }

    public bool SetProjectionMode(VolumeProjectionMode mode)
    {
        if (_volume is null || _projectionMode == mode)
        {
            return false;
        }

        _projectionMode = mode;
        ShowVolumeSlice(_volumeSliceIndex);
        UpdateOverlay();
        NotifyViewStateChanged();
        return true;
    }

    public bool SetProjectionThicknessMm(double thicknessMm)
    {
        if (_volume is null)
        {
            return false;
        }

        double clamped = Math.Clamp(thicknessMm, GetMinimumProjectionThicknessMm(), GetMaximumProjectionThicknessMm());
        if (Math.Abs(clamped - _projectionThicknessMm) < 0.05)
        {
            return false;
        }

        _projectionThicknessMm = clamped;
        ShowVolumeSlice(_volumeSliceIndex);
        UpdateOverlay();
        NotifyViewStateChanged();
        return true;
    }

    private double GetMinimumProjectionThicknessMm()
    {
        if (_volume is null)
        {
            return 1.0;
        }

        return Math.Max(0.1, VolumeReslicer.GetSliceSpacing(_volume, _volumeOrientation));
    }

    private double GetMaximumProjectionThicknessMm()
    {
        if (_volume is null)
        {
            return 500.0;
        }

        return Math.Max(
            GetMinimumProjectionThicknessMm(),
            VolumeReslicer.GetSliceSpacing(_volume, _volumeOrientation) * Math.Max(1, VolumeReslicer.GetSliceCount(_volume, _volumeOrientation)));
    }

    private static string GetProjectionModeLabel(VolumeProjectionMode mode) => mode switch
    {
        VolumeProjectionMode.Mpr => "MPR",
        VolumeProjectionMode.MipPr => "MipPR",
        VolumeProjectionMode.MinPr => "MinPR",
        VolumeProjectionMode.MpVrt => "MPVRT",
        _ => "MPR",
    };

    private static string GetOrientationLabel(SliceOrientation orientation) => orientation switch
    {
        SliceOrientation.Axial => "Axial",
        SliceOrientation.Coronal => "Coronal",
        SliceOrientation.Sagittal => "Sagittal",
        _ => "Axial",
    };

    // ==============================================================================================
    // Rendering (ported from TdView.SetDimension → TdcmImgObj pipeline)
    // ==============================================================================================

    private void RenderImage()
    {
        // Volume-based rendering path
        if (_volumeSlicePixels is not null && _displayBitmap is not null)
        {
            int pixelCount = _imageWidth * _imageHeight;
            byte[] outputBgra = new byte[pixelCount * 4];

            DicomPixelRenderer.RenderRescaled16Bit(
                _volumeSlicePixels,
                _imageWidth, _imageHeight,
                _windowCenter, _windowWidth,
                _lutR, _lutG, _lutB,
                _isMonochrome1,
                outputBgra);

            CopyToDisplayBitmap(outputBgra);
            return;
        }

        // Legacy file-based rendering path
        if (_rawPixelData == null || _displayBitmap == null) return;

        {
            int pixelCount = _imageWidth * _imageHeight;
            byte[] outputBgra = new byte[pixelCount * 4];

            DicomPixelRenderer.Render(
                _rawPixelData,
                _imageWidth, _imageHeight,
                _bitsAllocated, _bitsStored,
                _isSigned, _samplesPerPixel,
                _rescaleSlope, _rescaleIntercept,
                _windowCenter, _windowWidth,
                _lutR, _lutG, _lutB,
                _isMonochrome1,
                _photometricInterpretation,
                _planarConfiguration,
                outputBgra);

            ApplyBitmapOverlays(outputBgra);

            CopyToDisplayBitmap(outputBgra);
        }
    }

    private void CopyToDisplayBitmap(byte[] outputBgra)
    {
        if (_displayBitmap is null) return;

        using (var fb = _displayBitmap.Lock())
        {
            int stride = fb.RowBytes;
            int rowSize = _imageWidth * 4;
            IntPtr addr = fb.Address;

            if (stride == rowSize)
            {
                Marshal.Copy(outputBgra, 0, addr, outputBgra.Length);
            }
            else
            {
                for (int y = 0; y < _imageHeight; y++)
                    Marshal.Copy(outputBgra, y * rowSize, IntPtr.Add(addr, y * stride), rowSize);
            }
        }

        DicomImage.InvalidateVisual();
    }

    private void LoadBitmapOverlays(DicomDataset dataset)
    {
        _bitmapOverlays.Clear();

        for (ushort group = 0x6000; group <= 0x601E; group += 2)
        {
            var rowsTag = new DicomTag(group, 0x0010);
            var colsTag = new DicomTag(group, 0x0011);
            if (!dataset.Contains(rowsTag) || !dataset.Contains(colsTag))
            {
                continue;
            }

            int rows = dataset.GetSingleValueOrDefault(rowsTag, 0);
            int columns = dataset.GetSingleValueOrDefault(colsTag, 0);
            if (rows <= 0 || columns <= 0)
            {
                continue;
            }

            int originRow = 1;
            int originColumn = 1;
            var originTag = new DicomTag(group, 0x0050);
            if (dataset.Contains(originTag))
            {
                try
                {
                    short[] originValues = dataset.GetValues<short>(originTag);
                    if (originValues.Length >= 2)
                    {
                        originRow = originValues[0];
                        originColumn = originValues[1];
                    }
                }
                catch
                {
                }
            }

            byte[]? overlayBytes = ReadTagValueAsBytes(dataset, new DicomTag(group, 0x3000));
            bool[]? pixels = overlayBytes is { Length: > 0 }
                ? UnpackOverlayBits(overlayBytes, rows * columns)
                : TryExtractEmbeddedOverlay(dataset, group, rows, columns);

            if (pixels is null)
            {
                continue;
            }

            _bitmapOverlays.Add(new BitmapOverlayPlane(
                group,
                columns,
                rows,
                originColumn - 1,
                originRow - 1,
                pixels));
        }
    }

    private bool[]? TryExtractEmbeddedOverlay(DicomDataset dataset, ushort group, int rows, int columns)
    {
        var bitsAllocatedTag = new DicomTag(group, 0x0100);
        var bitPositionTag = new DicomTag(group, 0x0102);
        if (_rawPixelData is null || !dataset.Contains(bitsAllocatedTag) || !dataset.Contains(bitPositionTag))
        {
            return null;
        }

        int overlayBitsAllocated = dataset.GetSingleValueOrDefault(bitsAllocatedTag, 0);
        int overlayBitPosition = dataset.GetSingleValueOrDefault(bitPositionTag, -1);
        if (overlayBitsAllocated <= 1 || overlayBitPosition < 0)
        {
            return null;
        }

        int pixelCount = rows * columns;
        bool[] pixels = new bool[pixelCount];

        if (_bitsAllocated >= 16)
        {
            var source = MemoryMarshal.Cast<byte, ushort>(_rawPixelData.AsSpan());
            int count = Math.Min(source.Length, pixelCount);
            for (int index = 0; index < count; index++)
            {
                pixels[index] = ((source[index] >> overlayBitPosition) & 0x1) != 0;
            }
        }
        else
        {
            int count = Math.Min(_rawPixelData.Length, pixelCount);
            for (int index = 0; index < count; index++)
            {
                pixels[index] = (((int)_rawPixelData[index] >> overlayBitPosition) & 0x1) != 0;
            }
        }

        return pixels;
    }

    private void ApplyBitmapOverlays(byte[] outputBgra)
    {
        if (_bitmapOverlays.Count == 0)
        {
            return;
        }

        const int overlayR = 255;
        const int overlayG = 216;
        const int overlayB = 32;
        const double overlayWeight = 0.72;
        const double baseWeight = 1.0 - overlayWeight;

        foreach (BitmapOverlayPlane overlay in _bitmapOverlays)
        {
            for (int overlayY = 0; overlayY < overlay.Height; overlayY++)
            {
                int imageY = overlay.OriginY + overlayY;
                if (imageY < 0 || imageY >= _imageHeight)
                {
                    continue;
                }

                for (int overlayX = 0; overlayX < overlay.Width; overlayX++)
                {
                    int maskIndex = (overlayY * overlay.Width) + overlayX;
                    if (!overlay.Pixels[maskIndex])
                    {
                        continue;
                    }

                    int imageX = overlay.OriginX + overlayX;
                    if (imageX < 0 || imageX >= _imageWidth)
                    {
                        continue;
                    }

                    int outputIndex = ((imageY * _imageWidth) + imageX) * 4;
                    outputBgra[outputIndex] = (byte)Math.Clamp((outputBgra[outputIndex] * baseWeight) + (overlayB * overlayWeight), 0, 255);
                    outputBgra[outputIndex + 1] = (byte)Math.Clamp((outputBgra[outputIndex + 1] * baseWeight) + (overlayG * overlayWeight), 0, 255);
                    outputBgra[outputIndex + 2] = (byte)Math.Clamp((outputBgra[outputIndex + 2] * baseWeight) + (overlayR * overlayWeight), 0, 255);
                    outputBgra[outputIndex + 3] = 255;
                }
            }
        }
    }

    private static int ComputeExpectedPixelBytes(int width, int height, int samplesPerPixel, int bitsAllocated, string photometricInterpretation)
    {
        int bytesPerSample = Math.Max(1, bitsAllocated / 8);
        string photometric = (photometricInterpretation ?? string.Empty).Trim().ToUpperInvariant();
        if (samplesPerPixel >= 3 && (photometric == "YBR_FULL_422" || photometric == "YBR_PARTIAL_422"))
        {
            return width * height * 2 * bytesPerSample;
        }

        return width * height * samplesPerPixel * bytesPerSample;
    }

    private static byte[]? ReadTagValueAsBytes(DicomDataset dataset, DicomTag tag)
    {
        try
        {
            var item = dataset.GetDicomItem<DicomItem>(tag);
            if (item is DicomElement element)
            {
                return element.Buffer?.Data;
            }
        }
        catch
        {
        }

        return null;
    }

    private static bool[] UnpackOverlayBits(byte[] packedBits, int pixelCount)
    {
        bool[] pixels = new bool[pixelCount];
        for (int index = 0; index < pixelCount; index++)
        {
            int byteIndex = index >> 3;
            int bitIndex = index & 0x7;
            if (byteIndex >= packedBits.Length)
            {
                break;
            }

            pixels[index] = ((packedBits[byteIndex] >> bitIndex) & 0x1) != 0;
        }

        return pixels;
    }

    // ==============================================================================================
    // Color LUT (ported from TdView.SetColorLUT)
    // ==============================================================================================

    private void SetColorLutInternal(int scheme)
    {
        _colorScheme = scheme;
        var (r, g, b) = ColorLut.GetLut(scheme);
        _lutR = r;
        _lutG = g;
        _lutB = b;
    }

    /// <summary>
    /// Changes the color lookup table and re-renders.
    /// </summary>
    public void SetColorScheme(int scheme)
    {
        SetColorLutInternal(scheme);
        RenderImage();
        UpdateOverlay();
        NotifyViewStateChanged();
    }

    // ==============================================================================================
    // Zoom (ported from TdView.DetermineZoom, ImageZoom, dcmZoomIn/Out, SetFitInZoom)
    // ==============================================================================================

    /// <summary>
    /// Computes and applies fit-to-window zoom (ported from TdView.DetermineZoom).
    /// </summary>
    public void ApplyFitToWindow()
    {
        if (_imageWidth == 0 || _imageHeight == 0) return;

        double canvasWidth = RootGrid.Bounds.Width;
        double canvasHeight = RootGrid.Bounds.Height;
        if (canvasWidth <= 0 || canvasHeight <= 0) return;

        double scaleX = canvasWidth / GetDisplayWidth();
        double scaleY = canvasHeight / GetDisplayHeight();
        _zoomFactor = Math.Min(scaleX, scaleY);

        _fitToWindow = true;
        ApplyZoomTransform();
        CenterImage();
        Update3DCursorOverlay();
        UpdateMeasurementPresentation();
        ZoomChanged?.Invoke();
        NotifyViewStateChanged();
    }

    private void ApplyInitialFitToWindow()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!_fitToWindow || (_rawPixelData == null && _volumeSlicePixels == null))
            {
                return;
            }

            if (RootGrid.Bounds.Width <= 0 || RootGrid.Bounds.Height <= 0)
            {
                return;
            }

            ApplyFitToWindow();
            UpdateOverlay();
        }, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Sets zoom to 1:1 native resolution.
    /// </summary>
    public void ZoomToOriginal()
    {
        _fitToWindow = false;
        _zoomFactor = 1.0;
        ApplyZoomTransform();
        CenterImage();
        Update3DCursorOverlay();
        UpdateMeasurementPresentation();
        ZoomChanged?.Invoke();
        NotifyViewStateChanged();
    }

    /// <summary>
    /// Zooms in by 10% (ported from TdView.dcmZoomIn).
    /// </summary>
    public void ZoomIn()
    {
        _fitToWindow = false;
        _zoomFactor = Math.Min(20.0, _zoomFactor * 1.1);
        ApplyZoomTransform();
        Update3DCursorOverlay();
        UpdateMeasurementPresentation();
        ZoomChanged?.Invoke();
        NotifyViewStateChanged();
    }

    /// <summary>
    /// Zooms out by 10% (ported from TdView.dcmZoomOut).
    /// </summary>
    public void ZoomOut()
    {
        _fitToWindow = false;
        _zoomFactor = Math.Max(0.01, _zoomFactor / 1.1);
        ApplyZoomTransform();
        Update3DCursorOverlay();
        UpdateMeasurementPresentation();
        ZoomChanged?.Invoke();
        NotifyViewStateChanged();
    }

    /// <summary>
    /// Sets an exact zoom factor.
    /// </summary>
    public void SetZoom(double factor)
    {
        _fitToWindow = false;
        _zoomFactor = Math.Clamp(factor, 0.01, 20.0);
        ApplyZoomTransform();
        Update3DCursorOverlay();
        UpdateMeasurementPresentation();
        ZoomChanged?.Invoke();
        NotifyViewStateChanged();
    }

    public DisplayState CaptureDisplayState()
    {
        return new DisplayState(
            _windowCenter,
            _windowWidth,
            _zoomFactor,
            _fitToWindow,
            _panX,
            _panY,
            _colorScheme,
            _volumeOrientation,
            _projectionMode,
            _projectionThicknessMm);
    }

    public bool TryCaptureNavigationState(out NavigationState state)
    {
        state = default!;

        Point controlCenter = new(RootGrid.Bounds.Width / 2.0, RootGrid.Bounds.Height / 2.0);
        if (!TryGetImagePoint(controlCenter, out Point centerImagePoint))
        {
            if ((_rawPixelData is null && _volumeSlicePixels is null) || _zoomFactor <= 0)
            {
                return false;
            }

            double x = DisplayToImageX((controlCenter.X - _panX) / _zoomFactor);
            double y = DisplayToImageY((controlCenter.Y - _panY) / _zoomFactor);
            centerImagePoint = new Point(x, y);
        }

        state = new NavigationState(_zoomFactor, _fitToWindow, centerImagePoint);
        return true;
    }

    public void ApplyDisplayState(DisplayState state)
    {
        _windowCenter = state.WindowCenter;
        _windowWidth = Math.Max(1, state.WindowWidth);

        if (_colorScheme != state.ColorScheme)
        {
            SetColorLutInternal(state.ColorScheme);
        }

        _volumeOrientation = state.Orientation;
        _projectionMode = state.ProjectionMode;
        _projectionThicknessMm = Math.Max(GetMinimumProjectionThicknessMm(), state.ProjectionThicknessMm);

        if (state.FitToWindow)
        {
            _fitToWindow = true;
            ApplyFitToWindow();
        }
        else
        {
            _fitToWindow = false;
            _zoomFactor = Math.Clamp(state.ZoomFactor, 0.01, 20.0);
            ApplyZoomTransform();
            _panX = state.PanX;
            _panY = state.PanY;
            _panTransform.X = _panX;
            _panTransform.Y = _panY;
            Update3DCursorOverlay();
            UpdateMeasurementPresentation();
            ZoomChanged?.Invoke();
            NotifyViewStateChanged();
        }

        RenderImage();
        UpdateOverlay();
        WindowChanged?.Invoke();
    }

    public void ApplyNavigationState(NavigationState state)
    {
        if (_rawPixelData is null && _volumeSlicePixels is null)
        {
            return;
        }

        if (state.FitToWindow)
        {
            _fitToWindow = true;
            ApplyFitToWindow();
            UpdateOverlay();
            ZoomChanged?.Invoke();
            NotifyViewStateChanged();
            return;
        }

        _fitToWindow = false;
        _zoomFactor = Math.Clamp(state.ZoomFactor, 0.01, 20.0);
        ApplyZoomTransform();

        double cx = RootGrid.Bounds.Width / 2.0;
        double cy = RootGrid.Bounds.Height / 2.0;
        _panX = cx - (ImageToDisplayX(state.CenterImagePoint.X) * _zoomFactor);
        _panY = cy - (ImageToDisplayY(state.CenterImagePoint.Y) * _zoomFactor);
        _panTransform.X = _panX;
        _panTransform.Y = _panY;
        Update3DCursorOverlay();
        UpdateMeasurementPresentation();
        UpdateOverlay();
        ZoomChanged?.Invoke();
        NotifyViewStateChanged();
    }

    private void ApplyZoomTransform()
    {
        _zoomTransform.ScaleX = _zoomFactor;
        _zoomTransform.ScaleY = _zoomFactor;

        // Switch interpolation: nearest-neighbor for zoomed-in pixel inspection,
        // high quality for zoomed-out overview
        RenderOptions.SetBitmapInterpolationMode(DicomImage,
            _zoomFactor > 2.0 ? BitmapInterpolationMode.LowQuality : BitmapInterpolationMode.HighQuality);
    }

    private void CenterImage()
    {
        double canvasWidth = RootGrid.Bounds.Width;
        double canvasHeight = RootGrid.Bounds.Height;
        double displayWidth = GetDisplayWidth() * _zoomFactor;
        double displayHeight = GetDisplayHeight() * _zoomFactor;

        _panX = (canvasWidth - displayWidth) / 2.0;
        _panY = (canvasHeight - displayHeight) / 2.0;
        _panTransform.X = _panX;
        _panTransform.Y = _panY;
        Update3DCursorOverlay();
        UpdateMeasurementPresentation();
    }

    /// <summary>
    /// Resets window center/width to the DICOM default or auto-computed values.
    /// </summary>
    public void ResetWindowLevel()
    {
        _windowCenter = _defaultWindowCenter;
        _windowWidth = _defaultWindowWidth;
        RenderImage();
        UpdateOverlay();
        WindowChanged?.Invoke();
        NotifyViewStateChanged();
    }

    public void SetWindowLevel(double center, double width)
    {
        _windowCenter = center;
        _windowWidth = Math.Max(1, width);
        RenderImage();
        UpdateOverlay();
        WindowChanged?.Invoke();
        NotifyViewStateChanged();
    }

    public void Set3DCursorOverlay(Point? imagePoint)
    {
        _cursor3DImagePoint = imagePoint;
        Update3DCursorOverlay();
    }

    public void SetReferenceLineOverlay(Point? startImagePoint, Point? endImagePoint)
    {
        _referenceLineStartImagePoint = startImagePoint;
        _referenceLineEndImagePoint = endImagePoint;
        UpdateReferenceLineOverlay();
    }

    private void NotifyViewStateChanged() => ViewStateChanged?.Invoke();

    // ==============================================================================================
    // Overlay (ported from TdView.OverlayData — simplified 4-corner text)
    // ==============================================================================================

    private void UpdateOverlay()
    {
        bool hasImage = _rawPixelData != null || _volumeSlicePixels != null;
        if (!hasImage)
        {
            OverlayTopLeft.Text = "";
            OverlayTopRight.Text = "";
            OverlayCenterLeft.Text = "";
            OverlayCenterRight.Text = "";
            OverlayBottomLeft.Text = "";
            OverlayBottomRight.Text = "";
            ApplyOverlayVisibility();
            return;
        }

        OverlayTopLeft.Text = string.IsNullOrEmpty(_patientName)
            ? ""
            : $"{_patientName}\n{_patientId}";

        OverlayTopRight.Text = string.Join("\n",
            new[] { _institution, FormatStudyDate(_studyDate), _studyDescription }
                .Where(s => !string.IsNullOrEmpty(s)));
        OrientationBadgeText.Text = _volume is null
            ? string.Empty
            : GetOrientationLabel(_volumeOrientation);
        ProjectionBadgeText.Text = _volume is null
            ? string.Empty
            : $"{GetProjectionModeLabel(_projectionMode)}  {_projectionThicknessMm:F1} mm";

        OverlayCenterRight.Text = GetHorizontalOrientationLabel(isRightEdge: true);
        OverlayCenterLeft.Text = GetHorizontalOrientationLabel(isRightEdge: false);

        OverlayBottomLeft.Text = $"W: {_windowWidth:F0}  C: {_windowCenter:F0}";

        string zoomPct = $"Zoom: {_zoomFactor * 100:F0}%";
        string dims = $"{_imageWidth}×{_imageHeight}  {_bitsStored}-bit";
        string info = string.IsNullOrEmpty(_modality) ? dims : $"{dims}  {_modality}";

        if (_volume is not null)
        {
            int sliceCount = VolumeReslicer.GetSliceCount(_volume, _volumeOrientation);
            string orientLabel = _volumeOrientation switch
            {
                SliceOrientation.Axial => "Axial",
                SliceOrientation.Coronal => "Coronal",
                SliceOrientation.Sagittal => "Sagittal",
                _ => ""
            };
            info += $"\n{orientLabel} {_volumeSliceIndex + 1}/{sliceCount}  [Volume]";
        }
        else if (_frameCount > 1)
        {
            info += $"  [{_frameCount} frames]";
        }

        OverlayBottomRight.Text = $"{zoomPct}\n{info}";
        ApplyOverlayVisibility();
    }

    private void ApplyOverlayVisibility()
    {
        bool visible = _showOverlay;
        OverlayTopLeft.IsVisible = visible;
        OverlayTopRight.IsVisible = visible;
        OrientationBadge.IsVisible = visible && _volume is not null;
        ProjectionBadge.IsVisible = visible && _volume is not null;
        OverlayCenterLeft.IsVisible = visible && !string.IsNullOrEmpty(OverlayCenterLeft.Text);
        OverlayCenterRight.IsVisible = visible && !string.IsNullOrEmpty(OverlayCenterRight.Text);
        OverlayBottomLeft.IsVisible = visible;
        OverlayBottomRight.IsVisible = visible;
    }

    private string GetHorizontalOrientationLabel(bool isRightEdge)
    {
        if (SpatialMetadata is null)
        {
            return string.Empty;
        }

        SpatialVector3D direction = isRightEdge
            ? SpatialMetadata.RowDirection
            : SpatialMetadata.RowDirection * -1;

        return FormatPatientOrientation(direction);
    }

    private static string FormatPatientOrientation(SpatialVector3D direction)
    {
        const double minimumComponent = 0.2;

        var components = new (double Value, string Positive, string Negative)[]
        {
            (direction.X, "L", "R"),
            (direction.Y, "P", "A"),
            (direction.Z, "H", "F"),
        };

        var ordered = components
            .OrderByDescending(component => Math.Abs(component.Value))
            .ToArray();

        string label = string.Concat(
            ordered
                .Where(component => Math.Abs(component.Value) >= minimumComponent)
                .Select(component => component.Value >= 0 ? component.Positive : component.Negative));

        return label.Length > 0 ? label : string.Empty;
    }

    private sealed record BitmapOverlayPlane(
        ushort Group,
        int Width,
        int Height,
        int OriginX,
        int OriginY,
        bool[] Pixels);

    public bool TryGetImagePoint(Point controlPoint, out Point imagePoint)
    {
        imagePoint = default;

        if ((_rawPixelData == null && _volumeSlicePixels == null) || _zoomFactor <= 0)
        {
            return false;
        }

        double x = DisplayToImageX((controlPoint.X - _panX) / _zoomFactor);
        double y = DisplayToImageY((controlPoint.Y - _panY) / _zoomFactor);
        imagePoint = new Point(x, y);
        return x >= 0 && y >= 0 && x < _imageWidth && y < _imageHeight;
    }

    private void Update3DCursorOverlay()
    {
        if (_cursor3DImagePoint is null || (_rawPixelData == null && _volumeSlicePixels == null) || _zoomFactor <= 0)
        {
            Cursor3DHorizontal.IsVisible = false;
            Cursor3DVertical.IsVisible = false;
            Cursor3DMarker.IsVisible = false;
            UpdateReferenceLineOverlay();
            return;
        }

        double displayX = _panX + (ImageToDisplayX(_cursor3DImagePoint.Value.X) * _zoomFactor);
        double displayY = _panY + (ImageToDisplayY(_cursor3DImagePoint.Value.Y) * _zoomFactor);

        Cursor3DHorizontal.StartPoint = new Point(0, displayY);
        Cursor3DHorizontal.EndPoint = new Point(RootGrid.Bounds.Width, displayY);
        Cursor3DVertical.StartPoint = new Point(displayX, 0);
        Cursor3DVertical.EndPoint = new Point(displayX, RootGrid.Bounds.Height);

        Canvas.SetLeft(Cursor3DMarker, displayX - (Cursor3DMarker.Width / 2));
        Canvas.SetTop(Cursor3DMarker, displayY - (Cursor3DMarker.Height / 2));

        bool isVisible = displayX >= -12 && displayX <= RootGrid.Bounds.Width + 12 &&
            displayY >= -12 && displayY <= RootGrid.Bounds.Height + 12;
        Cursor3DHorizontal.IsVisible = isVisible;
        Cursor3DVertical.IsVisible = isVisible;
        Cursor3DMarker.IsVisible = isVisible;
        UpdateReferenceLineOverlay();
    }

    private void UpdateReferenceLineOverlay()
    {
        if (_referenceLineStartImagePoint is null ||
            _referenceLineEndImagePoint is null ||
            (_rawPixelData == null && _volumeSlicePixels == null) ||
            _zoomFactor <= 0)
        {
            ReferenceLinePrimary.IsVisible = false;
            return;
        }

        Point start = _referenceLineStartImagePoint.Value;
        Point end = _referenceLineEndImagePoint.Value;
        double startX = _panX + (ImageToDisplayX(start.X) * _zoomFactor);
        double startY = _panY + (ImageToDisplayY(start.Y) * _zoomFactor);
        double endX = _panX + (ImageToDisplayX(end.X) * _zoomFactor);
        double endY = _panY + (ImageToDisplayY(end.Y) * _zoomFactor);

        ReferenceLinePrimary.StartPoint = new Point(startX, startY);
        ReferenceLinePrimary.EndPoint = new Point(endX, endY);
        ReferenceLinePrimary.IsVisible = true;
    }

    private static string FormatStudyDate(string dcmDate)
    {
        if (string.IsNullOrEmpty(dcmDate) || dcmDate.Length < 8) return dcmDate;
        return $"{dcmDate[..4]}-{dcmDate[4..6]}-{dcmDate[6..8]}";
    }

    // ==============================================================================================
    // Pointer Handlers — Avalonia unified pointer events
    // ==============================================================================================

    /// <summary>
    /// Determines if the pointer is in the edge (zoom) region.
    /// Ported from TdView.IsCursorInZoomRegion: outer 1/6th of each dimension.
    /// </summary>
    private bool IsCursorInZoomRegion(Point pos)
    {
        double frameW = RootGrid.Bounds.Width / 6.0;
        double frameH = RootGrid.Bounds.Height / 6.0;

        return pos.X < frameW
            || pos.X > RootGrid.Bounds.Width - frameW
            || pos.Y < frameH
            || pos.Y > RootGrid.Bounds.Height - frameH;
    }

    private bool IsScrollActionMode() => ActionMode == ActionToolbarMode.ScrollStack;

    private bool IsWindowActionMode() => ActionMode == ActionToolbarMode.Window;

    private void BeginWindowLevelDrag(Point pos, IPointer pointer)
    {
        _isRightDragging = true;
        _mouseDownPos = pos;
        _startWindowCenter = _windowCenter;
        _startWindowWidth = _windowWidth;
        pointer.Capture(RootGrid);
        _capturedPointer = pointer;
        Cursor = CreateWindowCursor();
    }

    private void OnProjectionBadgePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_volume is null)
        {
            return;
        }

        PointerPoint point = e.GetCurrentPoint(ProjectionBadge);
        if (point.Properties.IsRightButtonPressed)
        {
            ProjectionBadge.ContextMenu?.Open(ProjectionBadge);
            e.Handled = true;
            return;
        }

        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        _isProjectionThicknessDragging = true;
        _projectionDragStartY = e.GetPosition(RootGrid).Y;
        _projectionDragStartThicknessMm = _projectionThicknessMm;
        _projectionPointer = e.Pointer;
        e.Pointer.Capture(ProjectionBadge);
        Cursor = new Cursor(StandardCursorType.SizeNorthSouth);
        e.Handled = true;
    }

    private void OnOrientationBadgePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_volume is null)
        {
            return;
        }

        PointerPoint point = e.GetCurrentPoint(OrientationBadge);
        if (point.Properties.IsRightButtonPressed)
        {
            OrientationBadge.ContextMenu?.Open(OrientationBadge);
            e.Handled = true;
            return;
        }

        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        SliceOrientation nextOrientation = _volumeOrientation switch
        {
            SliceOrientation.Axial => SliceOrientation.Coronal,
            SliceOrientation.Coronal => SliceOrientation.Sagittal,
            SliceOrientation.Sagittal => SliceOrientation.Axial,
            _ => SliceOrientation.Axial,
        };

        SetVolumeOrientation(nextOrientation);
        e.Handled = true;
    }

    private void OnProjectionBadgePointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isProjectionThicknessDragging || _volume is null)
        {
            return;
        }

        double currentY = e.GetPosition(RootGrid).Y;
        double deltaY = _projectionDragStartY - currentY;
        double baseThickness = Math.Max(_projectionDragStartThicknessMm, GetMinimumProjectionThicknessMm());
        double sensitivity = Math.Clamp(baseThickness / 70.0, 0.04, 1.25);
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            sensitivity *= 0.35;
        }

        SetProjectionThicknessMm(_projectionDragStartThicknessMm + (deltaY * sensitivity));
        e.Handled = true;
    }

    private void OnProjectionBadgePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isProjectionThicknessDragging)
        {
            return;
        }

        EndProjectionThicknessDrag();
        e.Handled = true;
    }

    private void OnProjectionBadgePointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        EndProjectionThicknessDrag();
    }

    private void EndProjectionThicknessDrag()
    {
        _isProjectionThicknessDragging = false;
        _projectionPointer?.Capture(null);
        _projectionPointer = null;
        UpdateZoneCursor(_mouseDownPos);
    }

    private static Cursor CreateWindowCursor()
    {
        if (s_windowCursor is not null)
        {
            return s_windowCursor;
        }

        lock (s_windowCursorLock)
        {
            if (s_windowCursor is not null)
            {
                return s_windowCursor;
            }

            try
            {
                Uri uri = new("avares://KPACS.Viewer/Assets/Cursors/window-cursor.png");
                using Stream stream = AssetLoader.Open(uri);
                var bitmap = new Bitmap(stream);
                s_windowCursor = new Cursor(bitmap, new PixelPoint(16, 16));
            }
            catch
            {
                s_windowCursor = new Cursor(StandardCursorType.Cross);
            }

            return s_windowCursor;
        }
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(RootGrid);
        var pos = e.GetPosition(RootGrid);

        if (HandleMeasurementPointerPressed(point, pos, e))
        {
            return;
        }

        if (point.Properties.IsLeftButtonPressed &&
            e.KeyModifiers.HasFlag(KeyModifiers.Shift) &&
            TryGetImagePoint(pos, out Point shiftImagePoint))
        {
            ImagePointPressed?.Invoke(new DicomImagePointerInfo(shiftImagePoint, e.KeyModifiers));
            e.Handled = true;
            return;
        }

        // Double-click left: let the host window decide how to handle layout focus.
        if (point.Properties.IsLeftButtonPressed && e.ClickCount == 2)
        {
            ImageDoubleClicked?.Invoke();
            e.Handled = true;
            return;
        }

        if (point.Properties.IsLeftButtonPressed && TryGetImagePoint(pos, out Point imagePoint))
        {
            ImagePointPressed?.Invoke(new DicomImagePointerInfo(imagePoint, e.KeyModifiers));
        }

        if (point.Properties.IsRightButtonPressed || (point.Properties.IsLeftButtonPressed && IsWindowActionMode()))
        {
            BeginWindowLevelDrag(pos, e.Pointer);
            e.Handled = true;
        }
        else if (point.Properties.IsLeftButtonPressed)
        {
            _isLeftDragging = true;
            _mouseDownPos = pos;

            if (IsScrollActionMode())
            {
                _isStackDragging = true;
                _lastStackMouseY = (int)_mouseDownPos.Y;
                Cursor = new Cursor(StandardCursorType.SizeNorthSouth);
                e.Pointer.Capture(RootGrid);
                _capturedPointer = e.Pointer;
                e.Handled = true;
                return;
            }

            // Lock the zone at click time (ported from FZoomRegion := IsCursorInZoomRegion)
            _isEdgeZoom = IsCursorInZoomRegion(_mouseDownPos);

            if (_isEdgeZoom)
            {
                _lastMouseY = (int)_mouseDownPos.Y;
                Cursor = new Cursor(StandardCursorType.SizeNorthSouth);
            }
            else
            {
                _startPanX = _panX;
                _startPanY = _panY;
                Cursor = new Cursor(StandardCursorType.Hand);
            }

            e.Pointer.Capture(RootGrid);
            _capturedPointer = e.Pointer;
            e.Handled = true;
        }
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (HandleMeasurementPointerReleased(e))
        {
            return;
        }

        if ((e.InitialPressMouseButton == MouseButton.Right || IsWindowActionMode()) && _isRightDragging)
        {
            _isRightDragging = false;
            _capturedPointer?.Capture(null);
            _capturedPointer = null;
            UpdateZoneCursor(e.GetPosition(RootGrid));
            e.Handled = true;
        }
        else if (e.InitialPressMouseButton == MouseButton.Left && _isLeftDragging)
        {
            _isLeftDragging = false;
            _isEdgeZoom = false;
            _isStackDragging = false;
            _capturedPointer?.Capture(null);
            _capturedPointer = null;
            UpdateZoneCursor(e.GetPosition(RootGrid));
            e.Handled = true;
        }
    }

    // ==============================================================================================
    // Pointer Move — Dispatch to windowing, edge-zoom, or center-pan
    // ==============================================================================================

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        Point pos = e.GetPosition(RootGrid);

        if (HandleMeasurementPointerMoved(pos, e))
        {
            HoveredImagePointChanged?.Invoke(TryGetImagePoint(pos, out Point imagePoint)
                ? new DicomHoverInfo(imagePoint, e.KeyModifiers)
                : null);
            return;
        }

        if (_isRightDragging)
        {
            // Window/Level: horizontal = width, vertical = center
            double dx = pos.X - _mouseDownPos.X;
            double dy = pos.Y - _mouseDownPos.Y;

            double sensitivity = Math.Max(1.0, _defaultWindowWidth / 500.0);

            _windowWidth = Math.Max(1, _startWindowWidth + dx * sensitivity);
            _windowCenter = _startWindowCenter + dy * sensitivity;

            RenderImage();
            UpdateOverlay();
            WindowChanged?.Invoke();
            NotifyViewStateChanged();
        }
        else if (_isLeftDragging && _isStackDragging)
        {
            int currentY = (int)pos.Y;
            int deltaY = currentY - _lastStackMouseY;

            if (Math.Abs(deltaY) > 1)
            {
                int stepDivisor = GetStackDragStepDivisor();
                int stackDelta = Math.Max(1, Math.Abs(deltaY) / stepDivisor);
                if (deltaY < 0)
                {
                    stackDelta = -stackDelta;
                }

                StackScrollRequested?.Invoke(stackDelta);
                _lastStackMouseY = currentY;
                e.Handled = true;
            }
        }
        else if (_isLeftDragging && _isEdgeZoom)
        {
            // Edge zone: drag up = zoom in, drag down = zoom out
            int currentY = (int)pos.Y;
            if (currentY != _lastMouseY)
            {
                double zoomPct = _zoomFactor * 100.0;
                double step = Math.Max(1.0, zoomPct / 100.0 + 1.0);
                double tempZoomPct;

                if (currentY < _lastMouseY)
                    tempZoomPct = 100.0 + step;  // drag up = zoom in
                else
                    tempZoomPct = 100.0 - step;  // drag down = zoom out

                tempZoomPct = Math.Clamp(tempZoomPct, 5.0, 1000.0);

                double factor = tempZoomPct / 100.0;
                double newZoom = Math.Clamp(_zoomFactor * factor, 0.01, 20.0);

                double cx = RootGrid.Bounds.Width / 2.0;
                double cy = RootGrid.Bounds.Height / 2.0;
                double ratio = newZoom / _zoomFactor;
                _panX = cx - ratio * (cx - _panX);
                _panY = cy - ratio * (cy - _panY);
                _panTransform.X = _panX;
                _panTransform.Y = _panY;

                _zoomFactor = newZoom;
                _fitToWindow = false;
                ApplyZoomTransform();
                Update3DCursorOverlay();
                UpdateMeasurementPresentation();
                UpdateOverlay();
                ZoomChanged?.Invoke();
                NotifyViewStateChanged();

                _lastMouseY = currentY;
            }
        }
        else if (_isLeftDragging && !_isEdgeZoom)
        {
            // Center zone: pan
            double dx = pos.X - _mouseDownPos.X;
            double dy = pos.Y - _mouseDownPos.Y;

            _panX = _startPanX + dx;
            _panY = _startPanY + dy;
            _panTransform.X = _panX;
            _panTransform.Y = _panY;
            _fitToWindow = false;
            Update3DCursorOverlay();
            UpdateMeasurementPresentation();
            NotifyViewStateChanged();
        }
        else if (!_isRightDragging && !_isLeftDragging && _rawPixelData != null)
        {
            UpdateZoneCursor(pos);
            HoveredImagePointChanged?.Invoke(TryGetImagePoint(pos, out Point imagePoint)
                ? new DicomHoverInfo(imagePoint, e.KeyModifiers)
                : null);
        }
    }

    private void OnPointerExited(object? sender, PointerEventArgs e)
    {
        HandleMeasurementPointerExited();

        if (!_isRightDragging && !_isLeftDragging)
        {
            HoveredImagePointChanged?.Invoke(null);
        }
    }

    /// <summary>
    /// Updates the cursor to reflect whether the pointer is in the edge (zoom) or center (pan) zone.
    /// </summary>
    private void UpdateZoneCursor(Point pos)
    {
        UpdateInteractiveCursor(pos);
    }

    // ==============================================================================================
    // Pointer Wheel — Zoom at cursor position
    // ==============================================================================================

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (WheelMode == MouseWheelMode.StackScroll)
        {
            StackScrollRequested?.Invoke(e.Delta.Y > 0 ? -1 : 1);
            e.Handled = true;
            return;
        }

        Point mousePos = e.GetPosition(RootGrid);

        double oldZoom = _zoomFactor;
        double zoomDelta = e.Delta.Y > 0 ? 1.1 : 1.0 / 1.1;
        double newZoom = Math.Clamp(_zoomFactor * zoomDelta, 0.01, 20.0);

        // Zoom centered on mouse position
        double ratio = newZoom / oldZoom;
        _panX = mousePos.X - ratio * (mousePos.X - _panX);
        _panY = mousePos.Y - ratio * (mousePos.Y - _panY);
        _panTransform.X = _panX;
        _panTransform.Y = _panY;

        _zoomFactor = newZoom;
        _fitToWindow = false;
        ApplyZoomTransform();
        Update3DCursorOverlay();
        UpdateMeasurementPresentation();

        UpdateOverlay();
        ZoomChanged?.Invoke();
        NotifyViewStateChanged();
        e.Handled = true;
    }

    private int GetStackDragStepDivisor()
    {
        if (!StackSkipImages || StackItemCount <= 1)
        {
            return 1;
        }

        int maxIndex = Math.Max(1, StackItemCount - 1);
        return Math.Max(2, (int)Math.Ceiling(200.0 / maxIndex));
    }

    private void UpdateDisplayGeometry(double pixelSpacingX, double pixelSpacingY)
    {
        pixelSpacingX = pixelSpacingX > 0 ? pixelSpacingX : 1.0;
        pixelSpacingY = pixelSpacingY > 0 ? pixelSpacingY : 1.0;

        double spacingBase = Math.Min(pixelSpacingX, pixelSpacingY);
        if (spacingBase <= 0)
        {
            spacingBase = 1.0;
        }

        _displayScaleX = pixelSpacingX / spacingBase;
        _displayScaleY = pixelSpacingY / spacingBase;
    }

    private void ApplyDisplayImageSize()
    {
        if (_imageWidth <= 0 || _imageHeight <= 0)
        {
            return;
        }

        DicomImage.Width = GetDisplayWidth();
        DicomImage.Height = GetDisplayHeight();
    }

    private double GetDisplayWidth() => _imageWidth * _displayScaleX;

    private double GetDisplayHeight() => _imageHeight * _displayScaleY;

    private double ImageToDisplayX(double imageX) => imageX * _displayScaleX;

    private double ImageToDisplayY(double imageY) => imageY * _displayScaleY;

    private double DisplayToImageX(double displayX) => _displayScaleX <= 0 ? displayX : displayX / _displayScaleX;

    private double DisplayToImageY(double displayY) => _displayScaleY <= 0 ? displayY : displayY / _displayScaleY;

    // ==============================================================================================
    // Resize handler
    // ==============================================================================================

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        bool hasImage = _rawPixelData != null || _volumeSlicePixels != null;
        if (!hasImage)
        {
            Update3DCursorOverlay();
            UpdateMeasurementPresentation();
            UpdateOverlay();
            return;
        }

        if (_fitToWindow)
        {
            ApplyFitToWindow();
        }
        else if (_zoomFactor > 0 && e.PreviousSize.Width > 0 && e.PreviousSize.Height > 0)
        {
            Point previousCenter = new(e.PreviousSize.Width / 2.0, e.PreviousSize.Height / 2.0);
            Point centerImagePoint = new(
                DisplayToImageX((previousCenter.X - _panX) / _zoomFactor),
                DisplayToImageY((previousCenter.Y - _panY) / _zoomFactor));

            double newCenterX = e.NewSize.Width / 2.0;
            double newCenterY = e.NewSize.Height / 2.0;
            _panX = newCenterX - (ImageToDisplayX(centerImagePoint.X) * _zoomFactor);
            _panY = newCenterY - (ImageToDisplayY(centerImagePoint.Y) * _zoomFactor);
            _panTransform.X = _panX;
            _panTransform.Y = _panY;
        }

        Update3DCursorOverlay();
        UpdateMeasurementPresentation();
        UpdateOverlay();
    }
}

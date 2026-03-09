// ------------------------------------------------------------------------------------------------
// KPACS.Viewer - ViewerTypes.cs
// Type definitions for the DICOM viewer.
// ------------------------------------------------------------------------------------------------

namespace KPACS.Viewer;

/// <summary>
/// Color scheme identifiers matching the original K-PACS LUT definitions.
/// </summary>
public enum ColorScheme
{
    GrayscaleInverted = -1,
    Grayscale = 1,
    HotIron = 2,
    Rainbow = 3,
    Gold = 5,
    Bone = 10,
}

/// <summary>
/// Mouse interaction tool.
/// </summary>
public enum ViewerTool
{
    /// <summary>Right-click drag adjusts window width/center.</summary>
    WindowLevel,
    /// <summary>Left-click drag pans the image.</summary>
    Pan,
    /// <summary>Mouse wheel zooms in/out.</summary>
    Zoom,
}

/// <summary>
/// Mouse wheel behavior for a viewport.
/// </summary>
public enum MouseWheelMode
{
    Zoom,
    StackScroll,
}

/// <summary>
/// Measurement interaction mode for the study viewer.
/// </summary>
public enum MeasurementTool
{
    None,
    PixelLens,
    Line,
    Angle,
    RectangleRoi,
    PolygonRoi,
}

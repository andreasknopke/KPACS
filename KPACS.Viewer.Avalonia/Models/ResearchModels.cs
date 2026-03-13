using KPACS.Viewer.Rendering;

namespace KPACS.Viewer.Models;

public enum MeasurementReviewState
{
    None,
    Suggested,
    Confirmed,
    Revised,
    Rejected,
}

public enum TrackingConfidenceBand
{
    Low,
    Medium,
    High,
}

public sealed record MeasurementTrackingMetadata(
    string TrackingId,
    string Label,
    string BaselineSeriesInstanceUid = "",
    string FollowUpSeriesInstanceUid = "",
    Guid? SourceMeasurementId = null,
    string TimepointLabel = "",
    MeasurementReviewState ReviewState = MeasurementReviewState.None,
    double Confidence = 0,
    string ConfidenceSummary = "",
    Vector3D? SuggestedCenterPatient = null,
    Vector3D? AppliedTranslation = null);

public sealed record SemiAutoRecistRequest(
    StudyMeasurement SourceMeasurement,
    SeriesVolume SourceVolume,
    SeriesVolume TargetVolume,
    string TargetTimepointLabel = "",
    double SearchRadiusMm = 18,
    double SearchStepMm = 2.5,
    double TemplateMarginMm = 4);

public sealed record SemiAutoRecistDiagnostics(
    double RegistrationConfidence,
    Vector3D RegistrationTranslation,
    double LocalMatchScore,
    double AmbiguityMargin,
    double AppliedDisplacementMm,
    int EvaluatedCandidates,
    bool UsedLocalRefinement);

public sealed record SemiAutoRecistCandidate(
    StudyMeasurement Measurement,
    Vector3D InitialCenterPatient,
    Vector3D SuggestedCenterPatient,
    double Confidence,
    TrackingConfidenceBand ConfidenceBand,
    string Summary,
    SemiAutoRecistDiagnostics Diagnostics);

public sealed record SliceRadiomicsResult(
    Guid MeasurementId,
    int PixelCount,
    double AreaSquareMillimeters,
    double Mean,
    double StandardDeviation,
    double Minimum,
    double Maximum,
    double Median,
    double Percentile10,
    double Percentile90,
    double Entropy,
    double Uniformity);

public enum RegistrationTransformKind
{
    Unknown,
    Translation,
    Rigid,
    Affine,
    Deformable,
}

public enum SceneAnnotationKind
{
    Unknown,
    FollowUpRecist,
    LinkedNavigation,
    ThreeDCursor,
    ManualRefinement,
}

public sealed record RegistrationTransformState(
    RegistrationTransformKind Kind,
    Vector3D Translation,
    double[]? Matrix4x4RowMajor = null,
    string Description = "");

public sealed record RegistrationRefinementState(
    double RegistrationConfidence,
    double LocalMatchScore,
    double AmbiguityMargin,
    double AppliedDisplacementMm,
    int EvaluatedCandidates,
    bool UsedLocalRefinement,
    string Summary = "");

public sealed record SceneFocusState(
    Vector3D SourcePatientPoint,
    Vector3D TargetPatientPoint,
    Vector3D? SuggestedPatientPoint = null,
    string Label = "");

public sealed record SceneViewportState(
    string ViewportId,
    SliceOrientation Orientation,
    VolumeProjectionMode ProjectionMode,
    double ProjectionThicknessMm,
    int SliceIndex,
    double ZoomFactor,
    bool FitToWindow,
    double CenterImageX,
    double CenterImageY,
    bool IsPrimary = false);

public sealed record RegistrationSceneState(
    string SceneId,
    SceneAnnotationKind Kind,
    string Label,
    string SourceSeriesInstanceUid,
    string TargetSeriesInstanceUid,
    string SourceFrameOfReferenceUid,
    string TargetFrameOfReferenceUid,
    RegistrationTransformState Transform,
    RegistrationRefinementState? Refinement,
    SceneFocusState? Focus,
    SceneViewportState[] Viewports,
    DateTimeOffset CreatedUtc,
    string Notes = "");
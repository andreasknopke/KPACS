using KPACS.Viewer.Models;
using KPACS.Viewer.Rendering;

namespace KPACS.Viewer.Services;

public static class RegistrationSceneStateFactory
{
    public static RegistrationSceneState CreateRecistFollowUpScene(
        SemiAutoRecistRequest request,
        SemiAutoRecistCandidate candidate,
        SceneViewportState[]? viewports = null,
        string? label = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(candidate);

        string resolvedLabel = !string.IsNullOrWhiteSpace(label)
            ? label
            : candidate.Measurement.Tracking?.Label is { Length: > 0 } trackingLabel
                ? trackingLabel
                : "RECIST follow-up scene";

        var transform = new RegistrationTransformState(
            RegistrationTransformKind.Translation,
            candidate.Diagnostics.RegistrationTranslation,
            CreateTranslationMatrix(candidate.Diagnostics.RegistrationTranslation),
            "Volume registration translation used as the initial scene transform.");

        var refinement = new RegistrationRefinementState(
            candidate.Diagnostics.RegistrationConfidence,
            candidate.Diagnostics.LocalMatchScore,
            candidate.Diagnostics.AmbiguityMargin,
            candidate.Diagnostics.AppliedDisplacementMm,
            candidate.Diagnostics.EvaluatedCandidates,
            candidate.Diagnostics.UsedLocalRefinement,
            candidate.Summary);

        var focus = new SceneFocusState(
            candidate.InitialCenterPatient,
            candidate.SuggestedCenterPatient,
            candidate.Measurement.Tracking?.SuggestedCenterPatient,
            resolvedLabel);

        SceneViewportState[] resolvedViewports = viewports ??
        [
            CreateDefaultViewport("source-axial", SliceOrientation.Axial),
            CreateDefaultViewport("target-axial", SliceOrientation.Axial, isPrimary: true),
            CreateDefaultViewport("target-coronal", SliceOrientation.Coronal),
            CreateDefaultViewport("target-sagittal", SliceOrientation.Sagittal),
        ];

        return new RegistrationSceneState(
            Guid.NewGuid().ToString("N"),
            SceneAnnotationKind.FollowUpRecist,
            resolvedLabel,
            request.SourceVolume.SeriesInstanceUid,
            request.TargetVolume.SeriesInstanceUid,
            request.SourceVolume.FrameOfReferenceUid,
            request.TargetVolume.FrameOfReferenceUid,
            transform,
            refinement,
            focus,
            resolvedViewports,
            DateTimeOffset.UtcNow,
            candidate.Summary);
    }

    public static SceneViewportState CreateViewportState(
        string viewportId,
        SliceOrientation orientation,
        VolumeProjectionMode projectionMode,
        double projectionThicknessMm,
        int sliceIndex,
        double zoomFactor,
        bool fitToWindow,
        double centerImageX,
        double centerImageY,
        bool isPrimary = false) =>
        new(
            viewportId,
            orientation,
            projectionMode,
            projectionThicknessMm,
            sliceIndex,
            zoomFactor,
            fitToWindow,
            centerImageX,
            centerImageY,
            isPrimary);

    private static SceneViewportState CreateDefaultViewport(string viewportId, SliceOrientation orientation, bool isPrimary = false) =>
        new(
            viewportId,
            orientation,
            VolumeProjectionMode.Mpr,
            1.0,
            0,
            1.0,
            true,
            0.0,
            0.0,
            isPrimary);

    private static double[] CreateTranslationMatrix(Vector3D translation) =>
    [
        1, 0, 0, translation.X,
        0, 1, 0, translation.Y,
        0, 0, 1, translation.Z,
        0, 0, 0, 1,
    ];
}
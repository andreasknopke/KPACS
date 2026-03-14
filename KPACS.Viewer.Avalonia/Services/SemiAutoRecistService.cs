using KPACS.Viewer.Models;
using KPACS.Viewer.Rendering;

namespace KPACS.Viewer.Services;

public static class SemiAutoRecistService
{
    private const double MinimumAcceptedLocalScore = 0.58;
    private const double MinimumAcceptedAmbiguityMargin = 0.04;
    private const int MinimumTemplateSamples = 24;

    public static bool TryCreateFollowUpCandidate(
        SemiAutoRecistRequest request,
        out SemiAutoRecistCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(request);

        candidate = default!;

        if (!TryBuildTemplate(request.SourceMeasurement, request.SourceVolume, request.TemplateMarginMm,
                out TemplateProfile template, out Vector3D sourceCenter, out Vector3D[] sourceAnchorOffsets))
        {
            return false;
        }

        if (!TryResolveInitialTargetCenter(request.SourceVolume, request.TargetVolume, sourceCenter,
                out Vector3D initialTargetCenter, out Vector3D registrationTranslation, out double registrationConfidence))
        {
            return false;
        }

        Vector3D refinedCenter = initialTargetCenter;
        double bestLocalScore = 0;
        double ambiguityMargin = 0;
        int evaluatedCandidates = 0;
        bool usedLocalRefinement = false;

        if (request.SearchRadiusMm > 0 && request.SearchStepMm > 0)
        {
            if (TryRefineTargetCenter(
                    request.TargetVolume,
                    initialTargetCenter,
                    template,
                    request.SearchRadiusMm,
                    request.SearchStepMm,
                    out Vector3D localCenter,
                    out double localScore,
                    out double localAmbiguityMargin,
                    out int localEvaluatedCandidates))
            {
                evaluatedCandidates = localEvaluatedCandidates;
                bestLocalScore = localScore;
                ambiguityMargin = localAmbiguityMargin;

                if (localScore >= MinimumAcceptedLocalScore && localAmbiguityMargin >= MinimumAcceptedAmbiguityMargin)
                {
                    refinedCenter = localCenter;
                    usedLocalRefinement = true;
                }
            }
        }

        Vector3D[] targetPatientAnchors = sourceAnchorOffsets
            .Select(offset => refinedCenter + offset)
            .ToArray();

        StudyMeasurement followUpMeasurement = CreateFollowUpMeasurement(
            request.SourceMeasurement,
            request.SourceVolume,
            request.TargetVolume,
            targetPatientAnchors,
            initialTargetCenter,
            refinedCenter,
            registrationTranslation,
            registrationConfidence,
            bestLocalScore,
            ambiguityMargin,
            request.TargetTimepointLabel,
            usedLocalRefinement);

        double appliedDisplacementMm = (refinedCenter - initialTargetCenter).Length;
        double confidence = ComputeConfidence(
            registrationConfidence,
            bestLocalScore,
            ambiguityMargin,
            appliedDisplacementMm,
            request.SearchRadiusMm,
            usedLocalRefinement);

        TrackingConfidenceBand band = confidence switch
        {
            >= 0.78 => TrackingConfidenceBand.High,
            >= 0.52 => TrackingConfidenceBand.Medium,
            _ => TrackingConfidenceBand.Low,
        };

        candidate = new SemiAutoRecistCandidate(
            followUpMeasurement,
            initialTargetCenter,
            refinedCenter,
            confidence,
            band,
            BuildSummary(usedLocalRefinement, confidence, bestLocalScore, appliedDisplacementMm),
            new SemiAutoRecistDiagnostics(
                registrationConfidence,
                registrationTranslation,
                bestLocalScore,
                ambiguityMargin,
                appliedDisplacementMm,
                evaluatedCandidates,
                usedLocalRefinement));

        return true;
    }

    private static bool TryBuildTemplate(
        StudyMeasurement measurement,
        SeriesVolume sourceVolume,
        double marginMm,
        out TemplateProfile template,
        out Vector3D sourceCenter,
        out Vector3D[] anchorOffsets)
    {
        template = default;
        anchorOffsets = [];

        Vector3D[] patientAnchors = measurement.Anchors
            .Where(anchor => anchor.PatientPoint is not null)
            .Select(anchor => anchor.PatientPoint!.Value)
            .ToArray();

        if (patientAnchors.Length == 0)
        {
            sourceCenter = default;
            return false;
        }

        Vector3D sum = default;
        foreach (Vector3D patientAnchor in patientAnchors)
        {
            sum += patientAnchor;
        }

        sourceCenter = sum / patientAnchors.Length;
        Vector3D resolvedSourceCenter = sourceCenter;
        anchorOffsets = patientAnchors.Select(point => point - resolvedSourceCenter).ToArray();

        double halfExtentX = Math.Max(3, anchorOffsets.Max(offset => Math.Abs(offset.Dot(sourceVolume.RowDirection))) + marginMm);
        double halfExtentY = Math.Max(3, anchorOffsets.Max(offset => Math.Abs(offset.Dot(sourceVolume.ColumnDirection))) + marginMm);
        double halfExtentZ = Math.Max(2, anchorOffsets.Max(offset => Math.Abs(offset.Dot(sourceVolume.Normal))) + Math.Max(2, marginMm * 0.5));

        double stepX = Math.Max(1.5, Math.Min(3.0, sourceVolume.SpacingX * 1.5));
        double stepY = Math.Max(1.5, Math.Min(3.0, sourceVolume.SpacingY * 1.5));
        double stepZ = Math.Max(1.5, Math.Min(4.0, sourceVolume.SpacingZ * 1.25));

        var samples = new List<TemplateSample>();
        for (double dz = -halfExtentZ; dz <= halfExtentZ + 0.01; dz += stepZ)
        {
            for (double dy = -halfExtentY; dy <= halfExtentY + 0.01; dy += stepY)
            {
                for (double dx = -halfExtentX; dx <= halfExtentX + 0.01; dx += stepX)
                {
                    double normalized =
                        (dx * dx) / Math.Max(1, halfExtentX * halfExtentX) +
                        (dy * dy) / Math.Max(1, halfExtentY * halfExtentY) +
                        (dz * dz) / Math.Max(1, halfExtentZ * halfExtentZ);
                    if (normalized > 1.0)
                    {
                        continue;
                    }

                    Vector3D patientOffset =
                        (sourceVolume.RowDirection * dx) +
                        (sourceVolume.ColumnDirection * dy) +
                        (sourceVolume.Normal * dz);

                    if (!TrySampleIntensity(sourceVolume, sourceCenter + patientOffset, out double intensity))
                    {
                        continue;
                    }

                    samples.Add(new TemplateSample(patientOffset, intensity));
                }
            }
        }

        if (samples.Count < MinimumTemplateSamples)
        {
            return false;
        }

        double mean = samples.Average(sample => sample.Intensity);
        double variance = samples.Average(sample => Square(sample.Intensity - mean));
        double standardDeviation = Math.Sqrt(Math.Max(variance, 0));
        double minimum = samples.Min(sample => sample.Intensity);
        double maximum = samples.Max(sample => sample.Intensity);

        template = new TemplateProfile(
            samples.ToArray(),
            mean,
            standardDeviation,
            minimum,
            maximum,
            Math.Max(halfExtentX, Math.Max(halfExtentY, halfExtentZ)));
        return true;
    }

    private static bool TryResolveInitialTargetCenter(
        SeriesVolume sourceVolume,
        SeriesVolume targetVolume,
        Vector3D sourceCenter,
        out Vector3D targetCenter,
        out Vector3D registrationTranslation,
        out double registrationConfidence)
    {
        targetCenter = sourceCenter;
        registrationTranslation = default;
        registrationConfidence = 0;

        bool sameFrame =
            !string.IsNullOrWhiteSpace(sourceVolume.FrameOfReferenceUid) &&
            string.Equals(sourceVolume.FrameOfReferenceUid, targetVolume.FrameOfReferenceUid, StringComparison.Ordinal);

        if (VolumeRegistrationService.TryTransformPatientPoint(sourceVolume, targetVolume, sourceCenter,
                out Vector3D transformedPoint, out VolumeTranslationRegistration registration))
        {
            targetCenter = transformedPoint;
            registrationTranslation = registration.Translation;
            registrationConfidence = registration.Confidence;
            return true;
        }

        if (sameFrame)
        {
            registrationConfidence = 0.55;
            return true;
        }

        return false;
    }

    private static bool TryRefineTargetCenter(
        SeriesVolume targetVolume,
        Vector3D initialCenter,
        TemplateProfile template,
        double searchRadiusMm,
        double searchStepMm,
        out Vector3D refinedCenter,
        out double bestScore,
        out double ambiguityMargin,
        out int evaluatedCandidates)
    {
        refinedCenter = initialCenter;
        bestScore = double.NegativeInfinity;
        ambiguityMargin = 0;
        evaluatedCandidates = 0;
        double secondBestScore = double.NegativeInfinity;

        for (double dz = -searchRadiusMm; dz <= searchRadiusMm + 0.01; dz += searchStepMm)
        {
            for (double dy = -searchRadiusMm; dy <= searchRadiusMm + 0.01; dy += searchStepMm)
            {
                for (double dx = -searchRadiusMm; dx <= searchRadiusMm + 0.01; dx += searchStepMm)
                {
                    Vector3D candidateCenter = initialCenter
                        + (targetVolume.RowDirection * dx)
                        + (targetVolume.ColumnDirection * dy)
                        + (targetVolume.Normal * dz);

                    if (!TryScoreCandidate(template, targetVolume, candidateCenter, out double score))
                    {
                        continue;
                    }

                    evaluatedCandidates++;
                    if (score > bestScore)
                    {
                        secondBestScore = bestScore;
                        bestScore = score;
                        refinedCenter = candidateCenter;
                    }
                    else if (score > secondBestScore)
                    {
                        secondBestScore = score;
                    }
                }
            }
        }

        if (evaluatedCandidates == 0 || double.IsNegativeInfinity(bestScore))
        {
            bestScore = 0;
            return false;
        }

        if (double.IsNegativeInfinity(secondBestScore))
        {
            secondBestScore = bestScore;
        }

        ambiguityMargin = Math.Max(0, bestScore - secondBestScore);
        return true;
    }

    private static bool TryScoreCandidate(
        TemplateProfile template,
        SeriesVolume targetVolume,
        Vector3D candidateCenter,
        out double score)
    {
        score = 0;

        double[] targetValues = new double[template.Samples.Length];
        int validCount = 0;
        for (int index = 0; index < template.Samples.Length; index++)
        {
            Vector3D samplePoint = candidateCenter + template.Samples[index].Offset;
            if (!TrySampleIntensity(targetVolume, samplePoint, out double intensity))
            {
                continue;
            }

            targetValues[validCount++] = intensity;
        }

        if (validCount < Math.Max(MinimumTemplateSamples, template.Samples.Length * 2 / 3))
        {
            return false;
        }

        double targetMean = 0;
        for (int index = 0; index < validCount; index++)
        {
            targetMean += targetValues[index];
        }

        targetMean /= validCount;

        double targetVariance = 0;
        for (int index = 0; index < validCount; index++)
        {
            targetVariance += Square(targetValues[index] - targetMean);
        }

        targetVariance /= validCount;
        double targetStdDev = Math.Sqrt(Math.Max(targetVariance, 0));

        double numerator = 0;
        double sumAbsDifference = 0;
        for (int index = 0; index < validCount; index++)
        {
            double sourceCentered = template.Samples[index].Intensity - template.Mean;
            double targetCentered = targetValues[index] - targetMean;
            numerator += sourceCentered * targetCentered;
            sumAbsDifference += Math.Abs(template.Samples[index].Intensity - targetValues[index]);
        }

        double normalizedCorrelation = 0;
        if (template.StandardDeviation > 1e-6 && targetStdDev > 1e-6)
        {
            normalizedCorrelation = numerator / (validCount * template.StandardDeviation * targetStdDev);
        }

        double normalizedNcc = Math.Clamp((normalizedCorrelation + 1.0) * 0.5, 0.0, 1.0);
        double intensityScale = Math.Max(80, template.Maximum - template.Minimum);
        double normalizedDifference = Math.Clamp(1.0 - ((sumAbsDifference / validCount) / intensityScale), 0.0, 1.0);
        double gradientScore = EstimateGradientScore(targetVolume, candidateCenter, intensityScale);

        score = (normalizedNcc * 0.55) + (normalizedDifference * 0.30) + (gradientScore * 0.15);
        return true;
    }

    private static StudyMeasurement CreateFollowUpMeasurement(
        StudyMeasurement sourceMeasurement,
        SeriesVolume sourceVolume,
        SeriesVolume targetVolume,
        IReadOnlyList<Vector3D> targetPatientAnchors,
        Vector3D initialCenter,
        Vector3D refinedCenter,
        Vector3D registrationTranslation,
        double registrationConfidence,
        double localScore,
        double ambiguityMargin,
        string targetTimepointLabel,
        bool usedLocalRefinement)
    {
        double averageSlice = targetPatientAnchors
            .Select(point => targetVolume.PatientToVoxel(point).Z)
            .Average();
        DicomSpatialMetadata representativeSlice = targetVolume.GetSliceSpatialMetadata((int)Math.Round(averageSlice));

        MeasurementAnchor[] anchors = targetPatientAnchors
            .Select(point => new MeasurementAnchor(representativeSlice.PixelPointFromPatient(point), point))
            .ToArray();

        string trackingId = sourceMeasurement.Tracking?.TrackingId ?? Guid.NewGuid().ToString("N");
        string label = sourceMeasurement.Tracking?.Label
            ?? (!string.IsNullOrWhiteSpace(sourceMeasurement.AnnotationText)
                ? sourceMeasurement.AnnotationText
                : $"Lesion {trackingId[..8]}");

        string confidenceSummary = usedLocalRefinement
            ? $"Local refinement accepted (match {localScore:0.00}, ambiguity {ambiguityMargin:0.00})."
            : $"Registration-only suggestion (match {localScore:0.00}, ambiguity {ambiguityMargin:0.00}).";

        var tracking = new MeasurementTrackingMetadata(
            trackingId,
            label,
            sourceVolume.SeriesInstanceUid,
            targetVolume.SeriesInstanceUid,
            sourceMeasurement.Id,
            targetTimepointLabel,
            MeasurementReviewState.Suggested,
            registrationConfidence,
            confidenceSummary,
            refinedCenter,
            registrationTranslation);

        return new StudyMeasurement(
            Guid.NewGuid(),
            sourceMeasurement.Kind,
            representativeSlice.FilePath,
            representativeSlice.SopInstanceUid,
            representativeSlice.FrameOfReferenceUid,
            representativeSlice.AcquisitionNumber,
            anchors,
            sourceMeasurement.AnnotationText,
            tracking,
            sourceMeasurement.LabelOffset);
    }

    private static string BuildSummary(bool usedLocalRefinement, double confidence, double localScore, double displacementMm)
    {
        if (usedLocalRefinement)
        {
            return $"Suggested follow-up lesion with local refinement ({confidence:P0} confidence, match {localScore:0.00}, shift {displacementMm:0.0} mm).";
        }

        return $"Suggested follow-up lesion from registration only ({confidence:P0} confidence, local match {localScore:0.00}).";
    }

    private static double ComputeConfidence(
        double registrationConfidence,
        double localScore,
        double ambiguityMargin,
        double appliedDisplacementMm,
        double searchRadiusMm,
        bool usedLocalRefinement)
    {
        double displacementScore = searchRadiusMm > 0
            ? Math.Clamp(1.0 - (appliedDisplacementMm / Math.Max(1, searchRadiusMm)), 0.0, 1.0)
            : 1.0;

        double confidence = usedLocalRefinement
            ? (registrationConfidence * 0.35) + (localScore * 0.40) + (ambiguityMargin * 0.15) + (displacementScore * 0.10)
            : (registrationConfidence * 0.75) + (localScore * 0.10) + (ambiguityMargin * 0.05) + (displacementScore * 0.10);

        return Math.Clamp(confidence, 0.0, 1.0);
    }

    private static bool TrySampleIntensity(SeriesVolume volume, Vector3D patientPoint, out double intensity)
    {
        (double x, double y, double z) = volume.PatientToVoxel(patientPoint);
        if (x < 0 || y < 0 || z < 0 || x > volume.SizeX - 1 || y > volume.SizeY - 1 || z > volume.SizeZ - 1)
        {
            intensity = 0;
            return false;
        }

        intensity = volume.GetVoxelInterpolated(x, y, z);
        return true;
    }

    private static double EstimateGradientScore(SeriesVolume volume, Vector3D patientPoint, double intensityScale)
    {
        double stepX = Math.Max(volume.SpacingX, 1.0);
        double stepY = Math.Max(volume.SpacingY, 1.0);
        double stepZ = Math.Max(volume.SpacingZ, 1.0);

        if (!TrySampleIntensity(volume, patientPoint + (volume.RowDirection * stepX), out double px) ||
            !TrySampleIntensity(volume, patientPoint - (volume.RowDirection * stepX), out double nx) ||
            !TrySampleIntensity(volume, patientPoint + (volume.ColumnDirection * stepY), out double py) ||
            !TrySampleIntensity(volume, patientPoint - (volume.ColumnDirection * stepY), out double ny) ||
            !TrySampleIntensity(volume, patientPoint + (volume.Normal * stepZ), out double pz) ||
            !TrySampleIntensity(volume, patientPoint - (volume.Normal * stepZ), out double nz))
        {
            return 0;
        }

        double gx = (px - nx) / (2 * stepX);
        double gy = (py - ny) / (2 * stepY);
        double gz = (pz - nz) / (2 * stepZ);
        double magnitude = Math.Sqrt((gx * gx) + (gy * gy) + (gz * gz));
        return Math.Clamp(magnitude / Math.Max(20, intensityScale), 0.0, 1.0);
    }

    private static double Square(double value) => value * value;

    private readonly record struct TemplateSample(Vector3D Offset, double Intensity);

    private readonly record struct TemplateProfile(
        TemplateSample[] Samples,
        double Mean,
        double StandardDeviation,
        double Minimum,
        double Maximum,
        double RadiusMm);
}
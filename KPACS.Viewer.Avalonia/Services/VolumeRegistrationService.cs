using KPACS.Viewer.Models;
using KPACS.Viewer.Rendering;

namespace KPACS.Viewer.Services;

public readonly record struct VolumeTranslationRegistration(
    Vector3D Translation,
    double Confidence,
    int OverlapSamples);

public static class VolumeRegistrationService
{
    private const int MinimumProfileSamples = 12;
    private const int MinimumOverlapSamples = 8;
    private const double MinimumAcceptedScore = 0.08;
    private const double MaximumAcceptedResidualMm = 42.0;
    private const double FallbackOverlapThreshold = 0.18;

    private static readonly Lock SyncRoot = new();
    private static readonly Dictionary<string, VolumeTranslationRegistration?> RegistrationCache = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, VolumeProfile> ProfileCache = new(StringComparer.Ordinal);

    public static bool TryTransformPatientPoint(
        SeriesVolume sourceVolume,
        SeriesVolume targetVolume,
        Vector3D sourcePatientPoint,
        out Vector3D targetPatientPoint,
        out VolumeTranslationRegistration registration)
    {
        if (TryGetRegistration(sourceVolume, targetVolume, out registration))
        {
            targetPatientPoint = sourcePatientPoint + registration.Translation;
            return true;
        }

        targetPatientPoint = sourcePatientPoint;
        return false;
    }

    public static bool TryGetRegistration(
        SeriesVolume sourceVolume,
        SeriesVolume targetVolume,
        out VolumeTranslationRegistration registration)
    {
        string cacheKey = $"{sourceVolume.SeriesInstanceUid}->{targetVolume.SeriesInstanceUid}";
        lock (SyncRoot)
        {
            if (RegistrationCache.TryGetValue(cacheKey, out VolumeTranslationRegistration? cached))
            {
                registration = cached ?? default;
                return cached is not null;
            }
        }

        VolumeTranslationRegistration? computed = ComputeRegistration(sourceVolume, targetVolume);
        lock (SyncRoot)
        {
            RegistrationCache[cacheKey] = computed;
        }

        registration = computed ?? default;
        return computed is not null;
    }

    private static VolumeTranslationRegistration? ComputeRegistration(SeriesVolume sourceVolume, SeriesVolume targetVolume)
    {
        VolumeProfile sourceProfile = GetOrBuildProfile(sourceVolume);
        VolumeProfile targetProfile = GetOrBuildProfile(targetVolume);

        RegistrationCandidate? bestCandidate = null;

        if (sourceProfile.SampleCount >= MinimumProfileSamples && targetProfile.SampleCount >= MinimumProfileSamples)
        {
            if (TryComputeProfileRegistration(sourceProfile, targetProfile, reverseTarget: false, out RegistrationCandidate forwardCandidate))
            {
                bestCandidate = forwardCandidate;
            }

            if (TryComputeProfileRegistration(sourceProfile, targetProfile, reverseTarget: true, out RegistrationCandidate reversedCandidate) &&
                (bestCandidate is null || reversedCandidate.Quality > bestCandidate.Value.Quality))
            {
                bestCandidate = reversedCandidate;
            }
        }

        if (TryComputeBodyBoundsFallback(sourceProfile, targetProfile, out RegistrationCandidate fallbackCandidate) &&
            (bestCandidate is null || fallbackCandidate.Quality > bestCandidate.Value.Quality))
        {
            bestCandidate = fallbackCandidate;
        }

        return bestCandidate?.ToRegistration();
    }

    private static bool TryComputeProfileRegistration(
        VolumeProfile source,
        VolumeProfile target,
        bool reverseTarget,
        out RegistrationCandidate candidate)
    {
        candidate = default;

        VolumeProfile effectiveTarget = reverseTarget ? target.CreateReversed() : target;
        double stepMm = Math.Max(2.0, Math.Min(source.StepMm, effectiveTarget.StepMm));
        double maxShiftMm = Math.Max(source.RangeMm, effectiveTarget.RangeMm) + (2 * stepMm);
        int maxShiftSteps = Math.Max(1, (int)Math.Ceiling(maxShiftMm / stepMm));

        double bestScore = double.NegativeInfinity;
        double bestShiftMm = 0;
        int bestOverlapSamples = 0;

        for (int shiftSteps = -maxShiftSteps; shiftSteps <= maxShiftSteps; shiftSteps++)
        {
            double shiftMm = shiftSteps * stepMm;
            if (!TryScoreShift(source, effectiveTarget, shiftMm, out double score, out int overlapSamples))
            {
                continue;
            }

            if (score > bestScore + 1e-6 ||
                (Math.Abs(score - bestScore) <= 1e-6 && overlapSamples > bestOverlapSamples))
            {
                bestScore = score;
                bestShiftMm = shiftMm;
                bestOverlapSamples = overlapSamples;
            }
        }

        if (bestOverlapSamples < MinimumOverlapSamples)
        {
            return false;
        }

        if (!TryEstimatePatientTranslation(source, effectiveTarget, bestShiftMm, out Vector3D translation, out int translationSamples, out double residualMm))
        {
            return false;
        }

        int overlap = Math.Min(bestOverlapSamples, translationSamples);
        if (overlap < MinimumOverlapSamples || residualMm > MaximumAcceptedResidualMm)
        {
            return false;
        }

        double overlapRatio = overlap / (double)Math.Max(1, Math.Min(source.SampleCount, effectiveTarget.SampleCount));
        double residualScore = Math.Clamp(1.0 - (residualMm / MaximumAcceptedResidualMm), 0.0, 1.0);
        double normalizedScore = Math.Clamp((bestScore + 0.20) / 1.20, 0.0, 1.0);
        double confidence = Math.Clamp((normalizedScore * 0.55) + (overlapRatio * 0.25) + (residualScore * 0.20), 0.0, 1.0);

        if (bestScore < MinimumAcceptedScore && !(overlapRatio >= 0.35 && residualMm <= 20.0))
        {
            return false;
        }

        double quality = confidence + (overlap * 0.002) - (reverseTarget ? 0.0001 : 0.0);
        candidate = new RegistrationCandidate(translation, confidence, overlap, quality);
        return true;
    }

    private static bool TryScoreShift(VolumeProfile source, VolumeProfile target, double shiftMm, out double score, out int overlapSamples)
    {
        score = 0;
        overlapSamples = 0;

        double sumSource = 0;
        double sumTarget = 0;
        double sumSourceSq = 0;
        double sumTargetSq = 0;
        double sumCross = 0;
        double sumDerivativeCross = 0;
        double sumDerivativeSourceSq = 0;
        double sumDerivativeTargetSq = 0;
        double sumAbsDifference = 0;

        foreach (double position in source.SamplePositions)
        {
            if (!source.TryInterpolate(position, out SliceProfileSample sourceSample) ||
                !target.TryInterpolate(position + shiftMm, out SliceProfileSample targetSample))
            {
                continue;
            }

            if (sourceSample.BodyFraction < 0.01 && targetSample.BodyFraction < 0.01)
            {
                continue;
            }

            overlapSamples++;
            sumSource += sourceSample.BodyFraction;
            sumTarget += targetSample.BodyFraction;
            sumSourceSq += sourceSample.BodyFraction * sourceSample.BodyFraction;
            sumTargetSq += targetSample.BodyFraction * targetSample.BodyFraction;
            sumCross += sourceSample.BodyFraction * targetSample.BodyFraction;
            sumDerivativeCross += sourceSample.Derivative * targetSample.Derivative;
            sumDerivativeSourceSq += sourceSample.Derivative * sourceSample.Derivative;
            sumDerivativeTargetSq += targetSample.Derivative * targetSample.Derivative;
            sumAbsDifference += Math.Abs(sourceSample.BodyFraction - targetSample.BodyFraction);
        }

        if (overlapSamples < MinimumOverlapSamples)
        {
            return false;
        }

        double corr = ComputeCorrelation(sumSource, sumTarget, sumSourceSq, sumTargetSq, sumCross, overlapSamples);
        double derivativeCorr = (sumDerivativeSourceSq > 1e-6 && sumDerivativeTargetSq > 1e-6)
            ? sumDerivativeCross / Math.Sqrt(sumDerivativeSourceSq * sumDerivativeTargetSq)
            : 0;
        double similarity = Math.Clamp(1.0 - ((sumAbsDifference / overlapSamples) / 0.45), 0.0, 1.0);

        score = (corr * 0.50) + (derivativeCorr * 0.20) + (similarity * 0.30);
        return true;
    }

    private static double ComputeCorrelation(
        double sumSource,
        double sumTarget,
        double sumSourceSq,
        double sumTargetSq,
        double sumCross,
        int sampleCount)
    {
        double numerator = (sampleCount * sumCross) - (sumSource * sumTarget);
        double denominatorLeft = (sampleCount * sumSourceSq) - (sumSource * sumSource);
        double denominatorRight = (sampleCount * sumTargetSq) - (sumTarget * sumTarget);
        if (denominatorLeft <= 1e-6 || denominatorRight <= 1e-6)
        {
            return 0;
        }

        return numerator / Math.Sqrt(denominatorLeft * denominatorRight);
    }

    private static bool TryEstimatePatientTranslation(
        VolumeProfile source,
        VolumeProfile target,
        double shiftMm,
        out Vector3D translation,
        out int overlapSamples,
        out double residualMm)
    {
        translation = default;
        overlapSamples = 0;
        residualMm = double.MaxValue;

        double totalWeight = 0;
        Vector3D weightedDeltaSum = default;
        var deltas = new List<(Vector3D Delta, double Weight)>();

        foreach (double position in source.SamplePositions)
        {
            if (!source.TryInterpolate(position, out SliceProfileSample sourceSample) ||
                !target.TryInterpolate(position + shiftMm, out SliceProfileSample targetSample))
            {
                continue;
            }

            double similarityWeight = Math.Clamp(1.0 - (Math.Abs(sourceSample.BodyFraction - targetSample.BodyFraction) / 0.50), 0.0, 1.0);
            double weight = Math.Min(sourceSample.BodyFraction, targetSample.BodyFraction) * (0.35 + (0.65 * similarityWeight));
            if (weight < 0.02)
            {
                continue;
            }

            Vector3D delta = targetSample.CentroidPatient - sourceSample.CentroidPatient;
            deltas.Add((delta, weight));
            weightedDeltaSum += delta * weight;
            totalWeight += weight;
            overlapSamples++;
        }

        if (overlapSamples < MinimumOverlapSamples || totalWeight <= 1e-6)
        {
            return false;
        }

        translation = weightedDeltaSum / totalWeight;

        double residualSquared = 0;
        foreach ((Vector3D delta, double weight) in deltas)
        {
            Vector3D error = delta - translation;
            residualSquared += error.Dot(error) * weight;
        }

        residualMm = Math.Sqrt(residualSquared / totalWeight);
        return true;
    }

    private static bool TryComputeBodyBoundsFallback(VolumeProfile source, VolumeProfile target, out RegistrationCandidate candidate)
    {
        candidate = default;
        if (!source.BodyBounds.IsValid || !target.BodyBounds.IsValid)
        {
            return false;
        }

        Vector3D translation = target.BodyBounds.Center - source.BodyBounds.Center;
        VolumeBounds translatedSource = source.BodyBounds.Translate(translation);
        double overlapRatio = translatedSource.ComputeOverlapRatio(target.BodyBounds);
        if (overlapRatio < FallbackOverlapThreshold)
        {
            return false;
        }

        double sourceVolume = Math.Max(1e-6, source.BodyBounds.Volume);
        double targetVolume = Math.Max(1e-6, target.BodyBounds.Volume);
        double sizeSimilarity = Math.Min(sourceVolume, targetVolume) / Math.Max(sourceVolume, targetVolume);
        double confidence = Math.Clamp((overlapRatio * 0.65) + (sizeSimilarity * 0.35), 0.0, 1.0);
        double quality = confidence * 0.92;
        int overlapSamples = Math.Max(MinimumOverlapSamples, (int)Math.Round(Math.Min(source.SampleCount, target.SampleCount) * overlapRatio));

        candidate = new RegistrationCandidate(translation, confidence, overlapSamples, quality);
        return true;
    }

    private static VolumeProfile GetOrBuildProfile(SeriesVolume volume)
    {
        lock (SyncRoot)
        {
            if (ProfileCache.TryGetValue(volume.SeriesInstanceUid, out VolumeProfile? cached))
            {
                return cached;
            }
        }

        VolumeProfile profile = BuildProfile(volume);
        lock (SyncRoot)
        {
            ProfileCache[volume.SeriesInstanceUid] = profile;
        }

        return profile;
    }

    private static VolumeProfile BuildProfile(SeriesVolume volume)
    {
        int width = volume.SizeX;
        int height = volume.SizeY;
        int depth = volume.SizeZ;
        int stepX = Math.Max(1, width / 96);
        int stepY = Math.Max(1, height / 96);
        int sampledPixels = ((width + stepX - 1) / stepX) * ((height + stepY - 1) / stepY);
        double threshold = EstimateBodyThreshold(volume);

        List<double> slicePositions = new(depth);
        List<SliceProfileSample> samples = new(depth);

        bool hasBounds = false;
        Vector3D minBounds = default;
        Vector3D maxBounds = default;

        for (int sliceIndex = 0; sliceIndex < depth; sliceIndex++)
        {
            long tissueCount = 0;
            double sumX = 0;
            double sumY = 0;

            for (int y = 0; y < height; y += stepY)
            {
                for (int x = 0; x < width; x += stepX)
                {
                    if (volume.GetVoxel(x, y, sliceIndex) <= threshold)
                    {
                        continue;
                    }

                    tissueCount++;
                    sumX += x;
                    sumY += y;

                    Vector3D patientPoint = volume.VoxelToPatient(x, y, sliceIndex);
                    if (!hasBounds)
                    {
                        minBounds = patientPoint;
                        maxBounds = patientPoint;
                        hasBounds = true;
                    }
                    else
                    {
                        minBounds = new Vector3D(
                            Math.Min(minBounds.X, patientPoint.X),
                            Math.Min(minBounds.Y, patientPoint.Y),
                            Math.Min(minBounds.Z, patientPoint.Z));
                        maxBounds = new Vector3D(
                            Math.Max(maxBounds.X, patientPoint.X),
                            Math.Max(maxBounds.Y, patientPoint.Y),
                            Math.Max(maxBounds.Z, patientPoint.Z));
                    }
                }
            }

            double centroidVoxelX = tissueCount > 0 ? sumX / tissueCount : (width - 1) / 2.0;
            double centroidVoxelY = tissueCount > 0 ? sumY / tissueCount : (height - 1) / 2.0;
            Vector3D centroidPatient = volume.VoxelToPatient(centroidVoxelX, centroidVoxelY, sliceIndex);
            double bodyFraction = sampledPixels > 0 ? tissueCount / (double)sampledPixels : 0;
            double localPosition = sliceIndex * volume.SpacingZ;

            slicePositions.Add(localPosition);
            samples.Add(new SliceProfileSample(localPosition, bodyFraction, centroidPatient, 0));
        }

        if (slicePositions.Count == 0)
        {
            return new VolumeProfile([], [], Math.Max(1.0, volume.SpacingZ), VolumeBounds.Invalid);
        }

        for (int index = 0; index < samples.Count; index++)
        {
            double derivative = 0;
            if (index > 0 && index < samples.Count - 1)
            {
                double dz = slicePositions[index + 1] - slicePositions[index - 1];
                if (Math.Abs(dz) > 1e-6)
                {
                    derivative = (samples[index + 1].BodyFraction - samples[index - 1].BodyFraction) / dz;
                }
            }

            samples[index] = samples[index] with { Derivative = derivative };
        }

        VolumeBounds bounds = hasBounds ? new VolumeBounds(minBounds, maxBounds) : VolumeBounds.Invalid;
        return new VolumeProfile(slicePositions, samples, Math.Max(1.0, volume.SpacingZ), bounds);
    }

    private static double EstimateBodyThreshold(SeriesVolume volume)
    {
        int stepX = Math.Max(1, volume.SizeX / 48);
        int stepY = Math.Max(1, volume.SizeY / 48);
        int stepZ = Math.Max(1, volume.SizeZ / 48);
        var samples = new List<short>();

        for (int z = 0; z < volume.SizeZ; z += stepZ)
        {
            for (int y = 0; y < volume.SizeY; y += stepY)
            {
                for (int x = 0; x < volume.SizeX; x += stepX)
                {
                    samples.Add(volume.GetVoxel(x, y, z));
                }
            }
        }

        if (samples.Count == 0)
        {
            return volume.MinValue + ((volume.MaxValue - volume.MinValue) * 0.12);
        }

        samples.Sort();
        double low = GetPercentile(samples, 0.05);
        double high = GetPercentile(samples, 0.95);
        double threshold = low + ((high - low) * 0.18);
        if (high - low < 1e-3)
        {
            threshold = volume.MinValue + ((volume.MaxValue - volume.MinValue) * 0.12);
        }

        return threshold;
    }

    private static double GetPercentile(List<short> values, double percentile)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        percentile = Math.Clamp(percentile, 0.0, 1.0);
        double scaledIndex = percentile * (values.Count - 1);
        int lowerIndex = (int)Math.Floor(scaledIndex);
        int upperIndex = (int)Math.Ceiling(scaledIndex);
        if (lowerIndex == upperIndex)
        {
            return values[lowerIndex];
        }

        double fraction = scaledIndex - lowerIndex;
        return values[lowerIndex] + ((values[upperIndex] - values[lowerIndex]) * fraction);
    }

    private readonly record struct RegistrationCandidate(
        Vector3D Translation,
        double Confidence,
        int OverlapSamples,
        double Quality)
    {
        public VolumeTranslationRegistration ToRegistration() =>
            new(Translation, Confidence, OverlapSamples);
    }

    private sealed class VolumeProfile(List<double> samplePositions, List<SliceProfileSample> samples, double stepMm, VolumeBounds bodyBounds)
    {
        public List<double> SamplePositions { get; } = samplePositions;
        public List<SliceProfileSample> Samples { get; } = samples;
        public double StepMm { get; } = stepMm;
        public VolumeBounds BodyBounds { get; } = bodyBounds;
        public int SampleCount => Samples.Count;
        public double RangeMm => SamplePositions.Count > 1 ? SamplePositions[^1] - SamplePositions[0] : 0;

        public bool TryInterpolate(double position, out SliceProfileSample sample)
        {
            sample = default;
            if (SamplePositions.Count == 0 || position < SamplePositions[0] || position > SamplePositions[^1])
            {
                return false;
            }

            int upperIndex = SamplePositions.BinarySearch(position);
            if (upperIndex >= 0)
            {
                sample = Samples[upperIndex];
                return true;
            }

            upperIndex = ~upperIndex;
            if (upperIndex <= 0 || upperIndex >= SamplePositions.Count)
            {
                return false;
            }

            int lowerIndex = upperIndex - 1;
            double lowerPosition = SamplePositions[lowerIndex];
            double upperPosition = SamplePositions[upperIndex];
            double span = upperPosition - lowerPosition;
            double t = span > 1e-6 ? (position - lowerPosition) / span : 0;

            SliceProfileSample lower = Samples[lowerIndex];
            SliceProfileSample upper = Samples[upperIndex];
            sample = new SliceProfileSample(
                position,
                Lerp(lower.BodyFraction, upper.BodyFraction, t),
                Lerp(lower.CentroidPatient, upper.CentroidPatient, t),
                Lerp(lower.Derivative, upper.Derivative, t));
            return true;
        }

        public VolumeProfile CreateReversed()
        {
            if (Samples.Count == 0)
            {
                return this;
            }

            List<SliceProfileSample> reversedSamples = new(Samples.Count);
            for (int index = 0; index < Samples.Count; index++)
            {
                SliceProfileSample original = Samples[Samples.Count - 1 - index];
                double position = SamplePositions[index];
                reversedSamples.Add(new SliceProfileSample(
                    position,
                    original.BodyFraction,
                    original.CentroidPatient,
                    -original.Derivative));
            }

            return new VolumeProfile([..SamplePositions], reversedSamples, StepMm, BodyBounds);
        }
    }

    private readonly record struct SliceProfileSample(
        double Position,
        double BodyFraction,
        Vector3D CentroidPatient,
        double Derivative);

    private readonly record struct VolumeBounds(Vector3D Min, Vector3D Max)
    {
        public static VolumeBounds Invalid => new(new Vector3D(double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity), new Vector3D(double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity));

        public bool IsValid =>
            !(double.IsInfinity(Min.X) || double.IsInfinity(Max.X) ||
              double.IsInfinity(Min.Y) || double.IsInfinity(Max.Y) ||
              double.IsInfinity(Min.Z) || double.IsInfinity(Max.Z));

        public Vector3D Center => new((Min.X + Max.X) / 2.0, (Min.Y + Max.Y) / 2.0, (Min.Z + Max.Z) / 2.0);
        public Vector3D Size => new(Math.Max(0, Max.X - Min.X), Math.Max(0, Max.Y - Min.Y), Math.Max(0, Max.Z - Min.Z));
        public double Volume => Size.X * Size.Y * Size.Z;

        public VolumeBounds Translate(Vector3D translation) => new(Min + translation, Max + translation);

        public double ComputeOverlapRatio(VolumeBounds other)
        {
            if (!IsValid || !other.IsValid)
            {
                return 0;
            }

            double overlapX = Math.Max(0, Math.Min(Max.X, other.Max.X) - Math.Max(Min.X, other.Min.X));
            double overlapY = Math.Max(0, Math.Min(Max.Y, other.Max.Y) - Math.Max(Min.Y, other.Min.Y));
            double overlapZ = Math.Max(0, Math.Min(Max.Z, other.Max.Z) - Math.Max(Min.Z, other.Min.Z));
            double overlapVolume = overlapX * overlapY * overlapZ;
            double referenceVolume = Math.Max(1e-6, Math.Min(Volume, other.Volume));
            return overlapVolume / referenceVolume;
        }
    }

    private static double Lerp(double start, double end, double t) => start + ((end - start) * t);
    private static Vector3D Lerp(Vector3D start, Vector3D end, double t) => start + ((end - start) * t);
}
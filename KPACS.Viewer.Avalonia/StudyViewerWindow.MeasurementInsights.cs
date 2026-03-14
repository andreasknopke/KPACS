using System.Globalization;
using Avalonia;
using Avalonia.Threading;
using FellowOakDicom;
using KPACS.Viewer.Models;
using KPACS.Viewer.Rendering;
using KPACS.Viewer.Services;
using Point = Avalonia.Point;

namespace KPACS.Viewer;

public partial class StudyViewerWindow
{
    private static readonly DicomTag SharedFunctionalGroupsSequenceTag = new(0x5200, 0x9229);
    private static readonly DicomTag PerFrameFunctionalGroupsSequenceTag = new(0x5200, 0x9230);
    private static readonly DicomTag MrDiffusionSequenceTag = new(0x0018, 0x9117);
    private static readonly DicomTag DiffusionBValueTag = new(0x0018, 0x9087);

    private readonly Dictionary<Guid, string> _measurementInsightCache = [];
    private readonly Dictionary<string, DiffusionSeriesDescriptor?> _diffusionSeriesCache = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _measurementInsightRefreshCancellation;

    private string? GetMeasurementTextSupplement(StudyMeasurement measurement, Point[] imagePoints)
    {
        _ = imagePoints;
        return _measurementInsightCache.TryGetValue(measurement.Id, out string? text)
            ? text
            : null;
    }

    private void QueueMeasurementInsightRefresh(Guid? measurementId)
    {
        _measurementInsightRefreshCancellation?.Cancel();

        if (measurementId is not Guid id)
        {
            return;
        }

        StudyMeasurement? measurement = _studyMeasurements.FirstOrDefault(candidate => candidate.Id == id);
        if (measurement is null || !IsInsightRelevantMeasurement(measurement))
        {
            _measurementInsightCache.Remove(id);
            return;
        }

        var cancellation = new CancellationTokenSource();
        _measurementInsightRefreshCancellation = cancellation;
        _ = RefreshMeasurementInsightAsync(measurement, cancellation.Token);
    }

    private void RemoveMeasurementInsight(Guid measurementId)
    {
        _measurementInsightCache.Remove(measurementId);
        _measurementInsightRefreshCancellation?.Cancel();
    }

    private static bool IsInsightRelevantMeasurement(StudyMeasurement measurement) => measurement.Kind switch
    {
        MeasurementKind.RectangleRoi => true,
        MeasurementKind.EllipseRoi => true,
        MeasurementKind.PolygonRoi => true,
        _ => false,
    };

    private async Task RefreshMeasurementInsightAsync(StudyMeasurement measurement, CancellationToken cancellationToken)
    {
        try
        {
            string? supplement = await BuildMeasurementSupplementAsync(measurement, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(supplement))
            {
                _measurementInsightCache.Remove(measurement.Id);
            }
            else
            {
                _measurementInsightCache[measurement.Id] = supplement;
            }

            await Dispatcher.UIThread.InvokeAsync(RefreshMeasurementPanels);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            _measurementInsightCache.Remove(measurement.Id);
            await Dispatcher.UIThread.InvokeAsync(RefreshMeasurementPanels);
        }
    }

    private async Task<string?> BuildMeasurementSupplementAsync(StudyMeasurement measurement, CancellationToken cancellationToken)
    {
        ViewportSlot? sourceSlot = FindSlotForMeasurement(measurement);
        if (sourceSlot?.Series is null || !string.Equals(sourceSlot.Series.Modality, "MR", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        DiffusionSeriesDescriptor? sourceDescriptor = DescribeDiffusionSeries(sourceSlot.Series);
        if (sourceDescriptor is null)
        {
            return null;
        }

        List<DiffusionSeriesObservation> observations = [];
        foreach (SeriesRecord series in _context.StudyDetails.Series)
        {
            cancellationToken.ThrowIfCancellationRequested();

            DiffusionSeriesDescriptor? descriptor = DescribeDiffusionSeries(series);
            if (descriptor is null)
            {
                continue;
            }

            SeriesVolume? volume = await GetOrLoadSeriesVolumeAsync(series, cancellationToken);
            if (volume is null || !measurement.TryGetPatientCenter(out KPACS.Viewer.Models.Vector3D patientCenter))
            {
                continue;
            }

            int sliceIndex = GetVolumeSliceIndexForPatientPoint(volume, SliceOrientation.Axial, patientCenter);
            DicomSpatialMetadata sliceMetadata = VolumeReslicer.GetSliceSpatialMetadata(volume, SliceOrientation.Axial, sliceIndex);
            if (!SliceRadiomicsService.TryExtract(measurement, sliceMetadata, volume, out SliceRadiomicsResult result))
            {
                continue;
            }

            observations.Add(new DiffusionSeriesObservation(descriptor, result));
        }

        if (observations.Count <= 1)
        {
            return null;
        }

        observations = observations
            .OrderBy(observation => observation.Descriptor.IsAdc ? 1 : 0)
            .ThenBy(observation => observation.Descriptor.BValue ?? double.MaxValue)
            .ThenBy(observation => observation.Descriptor.Series.SeriesNumber)
            .ToList();

        var lines = new List<string>(observations.Count + 1);
        foreach (DiffusionSeriesObservation observation in observations)
        {
            lines.Add($"{observation.Descriptor.DisplayLabel} μ/med {observation.Result.Mean:F1}/{observation.Result.Median:F1}  p10/p90 {observation.Result.Percentile10:F1}/{observation.Result.Percentile90:F1}");
        }

        string heuristic = BuildDiffusionHeuristic(observations);
        if (!string.IsNullOrWhiteSpace(heuristic))
        {
            lines.Add(heuristic);
        }

        return string.Join(Environment.NewLine, lines);
    }

    private async Task<SeriesVolume?> GetOrLoadSeriesVolumeAsync(SeriesRecord series, CancellationToken cancellationToken)
    {
        if (_volumeCache.TryGetValue(series.SeriesInstanceUid, out SeriesVolume? cachedVolume))
        {
            return cachedVolume;
        }

        var volumeLoader = new VolumeLoaderService();
        SeriesVolume? volume = await volumeLoader.TryLoadVolumeAsync(series, cancellationToken);
        _volumeCache[series.SeriesInstanceUid] = volume;
        return volume;
    }

    private DiffusionSeriesDescriptor? DescribeDiffusionSeries(SeriesRecord series)
    {
        if (_diffusionSeriesCache.TryGetValue(series.SeriesInstanceUid, out DiffusionSeriesDescriptor? cached))
        {
            return cached;
        }

        DiffusionSeriesDescriptor? descriptor = null;
        InstanceRecord? instance = series.Instances.FirstOrDefault(candidate =>
            !string.IsNullOrWhiteSpace(candidate.FilePath) && File.Exists(candidate.FilePath));

        if (instance is not null && string.Equals(series.Modality, "MR", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                DicomDataset dataset = DicomFile.Open(instance.FilePath, FileReadOption.ReadAll).Dataset;
                string description = series.SeriesDescription?.Trim() ?? string.Empty;
                string imageType = ReadJoinedValues(dataset, DicomTag.ImageType);
                double? bValue = TryGetDiffusionBValue(dataset);
                bool isAdc = ContainsAny(description, imageType, "adc", "apparent diffusion");
                bool isDiffusion = isAdc || bValue is not null || ContainsAny(description, imageType, "dwi", "diff", "tracew", "b=");

                if (isDiffusion)
                {
                    descriptor = new DiffusionSeriesDescriptor(
                        series,
                        bValue,
                        isAdc,
                        BuildDiffusionDisplayLabel(series, bValue, isAdc));
                }
            }
            catch
            {
                descriptor = null;
            }
        }

        _diffusionSeriesCache[series.SeriesInstanceUid] = descriptor;
        return descriptor;
    }

    private static string BuildDiffusionDisplayLabel(SeriesRecord series, double? bValue, bool isAdc)
    {
        if (isAdc)
        {
            return "ADC";
        }

        if (bValue is double diffusionBValue)
        {
            return $"b{diffusionBValue:0}";
        }

        string description = series.SeriesDescription?.Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(description)
            ? $"S{series.SeriesNumber}"
            : $"S{series.SeriesNumber}";
    }

    private static string BuildDiffusionHeuristic(IReadOnlyList<DiffusionSeriesObservation> observations)
    {
        DiffusionSeriesObservation? lowestB = observations
            .Where(observation => !observation.Descriptor.IsAdc && observation.Descriptor.BValue is not null)
            .OrderBy(observation => observation.Descriptor.BValue)
            .FirstOrDefault();
        DiffusionSeriesObservation? highestB = observations
            .Where(observation => !observation.Descriptor.IsAdc && observation.Descriptor.BValue is not null)
            .OrderByDescending(observation => observation.Descriptor.BValue)
            .FirstOrDefault();
        DiffusionSeriesObservation? adc = observations.FirstOrDefault(observation => observation.Descriptor.IsAdc);

        if (highestB is null && adc is null)
        {
            return string.Empty;
        }

        bool persistentHighBSignal = lowestB is not null && highestB is not null && highestB.Result.Median >= lowestB.Result.Median * 1.1;
        bool lowAdcSignal = adc is not null && (adc.Result.Median < 1.2 || adc.Result.Median < 1200);

        return (persistentHighBSignal, lowAdcSignal) switch
        {
            (true, true) => "DWI heuristic: restricted diffusion suggested",
            (true, false) => "DWI heuristic: persistent high-b signal",
            (false, true) => "DWI heuristic: low-ADC region",
            _ => "DWI heuristic: no strong restriction pattern",
        };
    }

    private static string ReadJoinedValues(DicomDataset dataset, DicomTag tag)
    {
        if (!dataset.Contains(tag))
        {
            return string.Empty;
        }

        try
        {
            return string.Join("\\", dataset.GetValues<string>(tag));
        }
        catch
        {
            return dataset.GetSingleValueOrDefault(tag, string.Empty);
        }
    }

    private static bool ContainsAny(string left, string right, params string[] needles)
    {
        string haystack = string.Join(" ", left, right).ToLowerInvariant();
        return needles.Any(needle => haystack.Contains(needle, StringComparison.Ordinal));
    }

    private static double? TryGetDiffusionBValue(DicomDataset dataset)
    {
        if (TryReadDouble(dataset, DiffusionBValueTag, out double bValue))
        {
            return bValue;
        }

        if (TryReadFunctionalGroupDouble(dataset, SharedFunctionalGroupsSequenceTag, MrDiffusionSequenceTag, DiffusionBValueTag, out bValue))
        {
            return bValue;
        }

        if (TryReadFunctionalGroupDouble(dataset, PerFrameFunctionalGroupsSequenceTag, MrDiffusionSequenceTag, DiffusionBValueTag, out bValue))
        {
            return bValue;
        }

        return null;
    }

    private static bool TryReadFunctionalGroupDouble(DicomDataset dataset, DicomTag groupTag, DicomTag nestedSequenceTag, DicomTag valueTag, out double value)
    {
        value = 0;
        if (!dataset.TryGetSequence(groupTag, out DicomSequence? groupSequence))
        {
            return false;
        }

        foreach (DicomDataset groupItem in groupSequence.Items)
        {
            if (!groupItem.TryGetSequence(nestedSequenceTag, out DicomSequence? nestedSequence))
            {
                continue;
            }

            foreach (DicomDataset nestedItem in nestedSequence.Items)
            {
                if (TryReadDouble(nestedItem, valueTag, out value))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryReadDouble(DicomDataset dataset, DicomTag tag, out double value)
    {
        value = 0;
        if (!dataset.Contains(tag))
        {
            return false;
        }

        try
        {
            value = dataset.GetSingleValue<double>(tag);
            return true;
        }
        catch
        {
            string text = dataset.GetSingleValueOrDefault(tag, string.Empty);
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }
    }

    private sealed record DiffusionSeriesDescriptor(SeriesRecord Series, double? BValue, bool IsAdc, string DisplayLabel);
    private sealed record DiffusionSeriesObservation(DiffusionSeriesDescriptor Descriptor, SliceRadiomicsResult Result);
}

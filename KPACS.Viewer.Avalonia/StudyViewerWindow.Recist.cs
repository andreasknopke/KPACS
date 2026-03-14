using Avalonia.Interactivity;
using KPACS.Viewer.Controls;
using KPACS.Viewer.Models;
using KPACS.Viewer.Rendering;
using KPACS.Viewer.Services;
using Point = Avalonia.Point;

namespace KPACS.Viewer;

public partial class StudyViewerWindow
{
    private async void OnToolboxRecistSuggestClick(object? sender, RoutedEventArgs e)
    {
        CloseAllActionPopups();
        ViewportToolboxPopup.IsOpen = false;

        StudyMeasurement? sourceMeasurement = GetSelectedMeasurement();
        if (sourceMeasurement is null)
        {
            ShowToast("Select a baseline line or ROI first, then trigger RECIST from the target viewport.", ToastSeverity.Warning);
            return;
        }

        if (!IsRecistSupportedMeasurement(sourceMeasurement))
        {
            ShowToast("RECIST follow-up currently supports line and ROI measurements only.", ToastSeverity.Warning);
            return;
        }

        ViewportSlot? sourceSlot = FindSlotForMeasurement(sourceMeasurement);
        if (sourceSlot?.Series is null)
        {
            ShowToast("The selected baseline measurement is not loaded in any viewport.", ToastSeverity.Warning);
            return;
        }

        ViewportSlot? targetSlot = ResolveRecistTargetSlot(sourceSlot);
        if (targetSlot?.Series is null)
        {
            ShowToast("Load the follow-up series into another viewport, then run RECIST from that viewport.", ToastSeverity.Warning);
            return;
        }

        if (SameSeries(sourceSlot, targetSlot))
        {
            ShowToast("RECIST follow-up needs a different target series. Open the target series in another viewport first.", ToastSeverity.Warning);
            return;
        }

        if (!await EnsureSlotVolumeAvailableAsync(sourceSlot) || sourceSlot.Volume is null)
        {
            ShowToast("The baseline series could not be loaded as a volume for RECIST matching.", ToastSeverity.Error);
            return;
        }

        if (!await EnsureSlotVolumeAvailableAsync(targetSlot) || targetSlot.Volume is null)
        {
            ShowToast("The target series could not be loaded as a volume for RECIST matching.", ToastSeverity.Error);
            return;
        }

        SemiAutoRecistRequest request = new(
            sourceMeasurement,
            sourceSlot.Volume,
            targetSlot.Volume,
            BuildRecistTimepointLabel(targetSlot.Series));

        if (!SemiAutoRecistService.TryCreateFollowUpCandidate(request, out SemiAutoRecistCandidate candidate))
        {
            ShowToast("RECIST follow-up suggestion failed. Try a clearer baseline ROI or a better registered target series.", ToastSeverity.Warning);
            return;
        }

        UpsertStudyMeasurement(candidate.Measurement);
        _selectedMeasurementId = candidate.Measurement.Id;
        RefreshMeasurementPanels();

        SetActiveSlot(targetSlot);
        FocusSlotOnMeasurement(targetSlot, candidate.Measurement);

        ToastSeverity severity = candidate.ConfidenceBand switch
        {
            TrackingConfidenceBand.High => ToastSeverity.Success,
            TrackingConfidenceBand.Medium => ToastSeverity.Info,
            _ => ToastSeverity.Warning,
        };

        ShowToast($"{candidate.Summary} Target: {BuildSeriesDisplayLabel(targetSlot.Series)}", severity, TimeSpan.FromSeconds(7));
    }

    private StudyMeasurement? GetSelectedMeasurement() =>
        _selectedMeasurementId is Guid measurementId
            ? _studyMeasurements.FirstOrDefault(measurement => measurement.Id == measurementId)
            : null;

    private static bool IsRecistSupportedMeasurement(StudyMeasurement measurement) => measurement.Kind switch
    {
        MeasurementKind.Line => true,
        MeasurementKind.RectangleRoi => true,
        MeasurementKind.EllipseRoi => true,
        MeasurementKind.PolygonRoi => true,
        _ => false,
    };

    private ViewportSlot? FindSlotForMeasurement(StudyMeasurement measurement) =>
        _slots
            .Where(slot => slot.Series is not null && SlotContainsMeasurementSource(slot, measurement))
            .OrderByDescending(slot => slot.Volume is not null)
            .ThenByDescending(slot => ReferenceEquals(slot, _activeSlot))
            .FirstOrDefault();

    private static bool SlotContainsMeasurementSource(ViewportSlot slot, StudyMeasurement measurement)
    {
        if (slot.Series is null)
        {
            return false;
        }

        foreach (InstanceRecord instance in slot.Series.Instances)
        {
            bool sopMatch = !string.IsNullOrWhiteSpace(measurement.ReferencedSopInstanceUid)
                && string.Equals(instance.SopInstanceUid, measurement.ReferencedSopInstanceUid, StringComparison.OrdinalIgnoreCase);
            bool fileMatch = !string.IsNullOrWhiteSpace(measurement.SourceFilePath)
                && (string.Equals(instance.FilePath, measurement.SourceFilePath, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(instance.SourceFilePath, measurement.SourceFilePath, StringComparison.OrdinalIgnoreCase));

            if (sopMatch || fileMatch)
            {
                return true;
            }
        }

        return false;
    }

    private ViewportSlot? ResolveRecistTargetSlot(ViewportSlot sourceSlot)
    {
        ViewportSlot? preferredSlot = _activeSlot;
        if (preferredSlot?.Series is not null && !SameSeries(sourceSlot, preferredSlot))
        {
            return preferredSlot;
        }

        return _slots.FirstOrDefault(slot => slot.Series is not null && !SameSeries(sourceSlot, slot));
    }

    private static bool SameSeries(ViewportSlot left, ViewportSlot right) =>
        left.Series is not null
        && right.Series is not null
        && string.Equals(left.Series.SeriesInstanceUid, right.Series.SeriesInstanceUid, StringComparison.OrdinalIgnoreCase);

    private async Task<bool> EnsureSlotVolumeAvailableAsync(ViewportSlot slot)
    {
        if (slot.Series is null)
        {
            return false;
        }

        if (slot.Volume is not null)
        {
            return true;
        }

        await EnsureVolumeLoadedForSlotAsync(slot, slot.Series);
        return slot.Volume is not null;
    }

    private void UpsertStudyMeasurement(StudyMeasurement measurement)
    {
        int existingIndex = _studyMeasurements.FindIndex(existing => existing.Id == measurement.Id);
        if (existingIndex >= 0)
        {
            _studyMeasurements[existingIndex] = measurement;
        }
        else
        {
            _studyMeasurements.Add(measurement);
        }
    }

    private void FocusSlotOnMeasurement(ViewportSlot slot, StudyMeasurement measurement)
    {
        if (slot.Series is null || slot.Volume is null || !measurement.TryGetPatientCenter(out Vector3D patientCenter))
        {
            return;
        }

        int sliceIndex = GetVolumeSliceIndexForPatientPoint(slot.Volume, slot.Panel.VolumeOrientation, patientCenter);
        slot.InstanceIndex = Math.Clamp(sliceIndex, 0, Math.Max(0, slot.Panel.VolumeSliceCount - 1));
        LoadSlot(slot, refreshThumbnailStrip: ReferenceEquals(slot, _activeSlot));

        DicomSpatialMetadata sliceMetadata = VolumeReslicer.GetSliceSpatialMetadata(slot.Volume, slot.Panel.VolumeOrientation, slot.InstanceIndex);
        Point imagePoint = sliceMetadata.PixelPointFromPatient(patientCenter);

        if (slot.Panel.TryCaptureNavigationState(out DicomViewPanel.NavigationState navigationState))
        {
            slot.Panel.ApplyNavigationState(navigationState with { FitToWindow = false, CenterImagePoint = imagePoint });
        }
        else
        {
            slot.Panel.ApplyNavigationState(new DicomViewPanel.NavigationState(1.0, 1.0, false, imagePoint));
        }
    }

    private static string BuildRecistTimepointLabel(SeriesRecord series)
    {
        string description = series.SeriesDescription?.Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(description)
            ? $"Series {series.SeriesNumber}"
            : $"Series {series.SeriesNumber}: {description}";
    }

    private static string BuildSeriesDisplayLabel(SeriesRecord series)
    {
        string description = series.SeriesDescription?.Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(description)
            ? $"S{series.SeriesNumber}"
            : $"S{series.SeriesNumber} {description}";
    }
}
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using KPACS.Viewer.Controls;
using KPACS.Viewer.Models;
using KPACS.Viewer.Windows;

namespace KPACS.Viewer;

public partial class StudyViewerWindow
{
    private readonly List<StudyMeasurement> _studyMeasurements = [];
    private readonly Dictionary<Guid, PolygonAutoOutlineState> _polygonAutoOutlineStates = [];
    private MeasurementTool _measurementTool = MeasurementTool.None;
    private Guid? _selectedMeasurementId;
    private bool _isApplyingPolygonAutoOutlineCorrection;

    private MeasurementTool GetEffectiveMeasurementTool() => _measurementTool;

    private void InitializeMeasurementsUi()
    {
        _measurementTool = MeasurementTool.None;
        UpdateMeasurementToolButtons();
    }

    private void ConfigureMeasurementPanel(ViewportSlot slot, DicomViewPanel panel)
    {
        panel.SetMeasurementTool(GetEffectiveMeasurementTool());
        panel.SetMeasurementNudgeMode(false);
        panel.SetMeasurements(_studyMeasurements, _selectedMeasurementId);
        panel.SetMeasurementTextSupplementProvider(GetMeasurementTextSupplement);
        panel.MeasurementCreated += OnPanelMeasurementCreated;
        panel.MeasurementUpdated += OnPanelMeasurementUpdated;
        panel.MeasurementDeleted += OnPanelMeasurementDeleted;
        panel.SelectedMeasurementChanged += OnPanelMeasurementSelectedChanged;
        panel.AutoOutlinedMeasurementCreated += OnPanelAutoOutlinedMeasurementCreated;
        panel.VolumeRoiDraftChanged += _ => RefreshVolumeRoiDraftPanel();
    }

    private void ApplyMeasurementContext(ViewportSlot slot)
    {
        slot.Panel.SetMeasurementTool(GetEffectiveMeasurementTool());
        slot.Panel.SetMeasurementNudgeMode(false);
        slot.Panel.SetMeasurements(_studyMeasurements, _selectedMeasurementId);
        RefreshMeasurementInsightPanel();
        RefreshVolumeRoiDraftPanel();
    }

    private void RefreshMeasurementPanels()
    {
        MeasurementTool effectiveTool = GetEffectiveMeasurementTool();
        foreach (ViewportSlot slot in _slots)
        {
            slot.Panel.SetMeasurementTool(effectiveTool);
            slot.Panel.SetMeasurementNudgeMode(false);
            slot.Panel.SetMeasurements(_studyMeasurements, _selectedMeasurementId);
        }

        RefreshMeasurementInsightPanel();
        RefreshVolumeRoiDraftPanel();
        UpdateStatus();
    }

    private void OnMeasurementToolPopupClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Control button || button.Tag is not string tag)
        {
            return;
        }

        if (!Enum.TryParse(tag, ignoreCase: true, out MeasurementTool tool))
        {
            tool = MeasurementTool.None;
        }

        SetMeasurementTool(tool);
        CloseViewportToolbox();
        CloseAllActionPopups();
        RestartActionToolbarHideTimer();
        e.Handled = true;
    }

    private void SetMeasurementTool(MeasurementTool tool)
    {
        _measurementTool = tool;
        if (tool != MeasurementTool.None)
        {
            _is3DCursorToolArmed = false;
            Update3DCursorToolButton();
        }

        UpdateMeasurementToolButtons();
        RefreshMeasurementPanels();
    }

    private void UpdateMeasurementToolButtons()
    {
        if (ActionToolsButton is null)
        {
            return;
        }

        ToolboxNavigateButton.IsChecked = _measurementTool == MeasurementTool.None;
        ToolboxPixelLensButton.IsChecked = _measurementTool == MeasurementTool.PixelLens;
        ToolboxLineButton.IsChecked = _measurementTool == MeasurementTool.Line;
        ToolboxAngleButton.IsChecked = _measurementTool == MeasurementTool.Angle;
        ToolboxAnnotationButton.IsChecked = _measurementTool == MeasurementTool.Annotation;
        ToolboxRectangleRoiButton.IsChecked = _measurementTool == MeasurementTool.RectangleRoi;
        ToolboxEllipseRoiButton.IsChecked = _measurementTool == MeasurementTool.EllipseRoi;
        ToolboxPolygonRoiButton.IsChecked = _measurementTool == MeasurementTool.PolygonRoi;
        ToolboxVolumeRoiButton.IsChecked = _measurementTool == MeasurementTool.VolumeRoi;
        ToolboxBallRoiButton.IsChecked = _measurementTool == MeasurementTool.BallRoiCorrection;
        ToolboxModifyButton.IsChecked = _measurementTool == MeasurementTool.Modify;
        ToolboxEraseButton.IsChecked = _measurementTool == MeasurementTool.Erase;
    }

    private bool TryNudgeSelectedMeasurement(Avalonia.Vector delta)
    {
        IEnumerable<ViewportSlot> candidateSlots = _slots
            .Where(slot => slot.Panel.IsImageLoaded)
            .OrderByDescending(slot => ReferenceEquals(slot, _activeSlot));

        foreach (ViewportSlot slot in candidateSlots)
        {
            if (slot.Panel.TryNudgeSelectedMeasurement(delta))
            {
                return true;
            }
        }

        return false;
    }

    private async void OnPanelMeasurementCreated(StudyMeasurement measurement)
    {
        _studyMeasurements.RemoveAll(existing => existing.Id == measurement.Id);
        _studyMeasurements.Add(measurement);
        _selectedMeasurementId = measurement.Id;
        QueueMeasurementInsightRefresh(measurement.Id);
        RefreshMeasurementPanels();

        if (measurement.Kind != MeasurementKind.Annotation)
        {
            return;
        }

        var dialog = new AnnotationTextWindow(measurement.AnnotationText);
        string? annotationText = await dialog.ShowDialog<string?>(this);
        if (annotationText is null)
        {
            return;
        }

        StudyMeasurement updatedMeasurement = measurement.WithAnnotationText(annotationText);
        int index = _studyMeasurements.FindIndex(existing => existing.Id == measurement.Id);
        if (index >= 0)
        {
            _studyMeasurements[index] = updatedMeasurement;
            _selectedMeasurementId = updatedMeasurement.Id;
            RefreshMeasurementPanels();
        }
    }

    private void OnPanelMeasurementUpdated(StudyMeasurement measurement)
    {
        if (!_isApplyingPolygonAutoOutlineCorrection && measurement.Kind == MeasurementKind.PolygonRoi)
        {
            _polygonAutoOutlineStates.Remove(measurement.Id);
        }

        int index = _studyMeasurements.FindIndex(existing => existing.Id == measurement.Id);
        if (index >= 0)
        {
            _studyMeasurements[index] = measurement;
        }
        else
        {
            _studyMeasurements.Add(measurement);
        }

        _selectedMeasurementId = measurement.Id;
        QueueMeasurementInsightRefresh(measurement.Id);
        RefreshMeasurementPanels();
    }

    private void OnPanelMeasurementSelectedChanged(Guid? measurementId)
    {
        if (_selectedMeasurementId == measurementId)
        {
            return;
        }

        _selectedMeasurementId = measurementId;
        QueueMeasurementInsightRefresh(measurementId);
        RefreshMeasurementPanels();
    }

    private void OnPanelMeasurementDeleted(Guid measurementId)
    {
        _studyMeasurements.RemoveAll(existing => existing.Id == measurementId);
        _polygonAutoOutlineStates.Remove(measurementId);
        RemoveMeasurementInsight(measurementId);
        if (_selectedMeasurementId == measurementId)
        {
            _selectedMeasurementId = null;
        }

        RefreshMeasurementPanels();
    }

    private void OnPanelAutoOutlinedMeasurementCreated(DicomViewPanel.AutoOutlinedMeasurementInfo info)
    {
        if (info.Kind != MeasurementKind.PolygonRoi)
        {
            return;
        }

        _polygonAutoOutlineStates[info.MeasurementId] = new PolygonAutoOutlineState(info.SeedPoint, info.SensitivityLevel);
        RefreshMeasurementInsightPanel();
    }

    private sealed record PolygonAutoOutlineState(Point SeedPoint, int SensitivityLevel);

    private string GetMeasurementToolLabel() => _measurementTool switch
    {
        MeasurementTool.None => "Navigate",
        MeasurementTool.PixelLens => "Pixel lens",
        MeasurementTool.Line => "Line",
        MeasurementTool.Angle => "Angle",
        MeasurementTool.Annotation => "Annotation",
        MeasurementTool.RectangleRoi => "Rectangle ROI",
        MeasurementTool.EllipseRoi => "Ellipse ROI",
        MeasurementTool.PolygonRoi => "Polygon ROI",
        MeasurementTool.VolumeRoi => "3D ROI",
        MeasurementTool.BallRoiCorrection => "ROI ball",
        MeasurementTool.Modify => "Modify",
        MeasurementTool.Erase => "Erase",
        _ => "Navigate",
    };
}
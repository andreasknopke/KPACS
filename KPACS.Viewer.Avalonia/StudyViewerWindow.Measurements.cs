using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using KPACS.Viewer.Controls;
using KPACS.Viewer.Models;
using KPACS.Viewer.Windows;

namespace KPACS.Viewer;

public partial class StudyViewerWindow
{
    private readonly List<StudyMeasurement> _studyMeasurements = [];
    private MeasurementTool _measurementTool = MeasurementTool.None;
    private Guid? _selectedMeasurementId;

    private MeasurementTool GetEffectiveMeasurementTool() => _measurementTool;

    private void InitializeMeasurementsUi()
    {
        _measurementTool = MeasurementTool.None;
        UpdateMeasurementToolButtons();
    }

    private void ConfigureMeasurementPanel(ViewportSlot slot, DicomViewPanel panel)
    {
        panel.SetMeasurementTool(GetEffectiveMeasurementTool());
        panel.SetMeasurements(_studyMeasurements, _selectedMeasurementId);
        panel.MeasurementCreated += OnPanelMeasurementCreated;
        panel.MeasurementUpdated += OnPanelMeasurementUpdated;
        panel.MeasurementDeleted += OnPanelMeasurementDeleted;
        panel.SelectedMeasurementChanged += OnPanelMeasurementSelectedChanged;
    }

    private void ApplyMeasurementContext(ViewportSlot slot)
    {
        slot.Panel.SetMeasurementTool(GetEffectiveMeasurementTool());
        slot.Panel.SetMeasurements(_studyMeasurements, _selectedMeasurementId);
    }

    private void RefreshMeasurementPanels()
    {
        MeasurementTool effectiveTool = GetEffectiveMeasurementTool();
        foreach (ViewportSlot slot in _slots)
        {
            slot.Panel.SetMeasurementTool(effectiveTool);
            slot.Panel.SetMeasurements(_studyMeasurements, _selectedMeasurementId);
        }

        UpdateStatus();
    }

    private void OnMeasurementToolPopupClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Avalonia.Controls.Button button || button.Tag is not string tag)
        {
            return;
        }

        if (!Enum.TryParse(tag, ignoreCase: true, out MeasurementTool tool))
        {
            tool = MeasurementTool.None;
        }

        SetMeasurementTool(tool);
        CloseAllActionPopups();
        CloseViewportToolbox();
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
        if (ToolboxNavigateButton is null)
            return;

        ToolboxNavigateButton.IsChecked = _measurementTool == MeasurementTool.None;
        ToolboxPixelLensButton.IsChecked = _measurementTool == MeasurementTool.PixelLens;
        ToolboxLineButton.IsChecked = _measurementTool == MeasurementTool.Line;
        ToolboxAngleButton.IsChecked = _measurementTool == MeasurementTool.Angle;
        ToolboxAnnotationButton.IsChecked = _measurementTool == MeasurementTool.Annotation;
        ToolboxRectangleRoiButton.IsChecked = _measurementTool == MeasurementTool.RectangleRoi;
        ToolboxPolygonRoiButton.IsChecked = _measurementTool == MeasurementTool.PolygonRoi;
        ToolboxModifyButton.IsChecked = _measurementTool == MeasurementTool.Modify;
        ToolboxEraseButton.IsChecked = _measurementTool == MeasurementTool.Erase;
    }

    private async void OnPanelMeasurementCreated(StudyMeasurement measurement)
    {
        _studyMeasurements.RemoveAll(existing => existing.Id == measurement.Id);
        _studyMeasurements.Add(measurement);
        _selectedMeasurementId = measurement.Id;
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
        RefreshMeasurementPanels();
    }

    private void OnPanelMeasurementSelectedChanged(Guid? measurementId)
    {
        if (_selectedMeasurementId == measurementId)
        {
            return;
        }

        _selectedMeasurementId = measurementId;
        RefreshMeasurementPanels();
    }

    private void OnPanelMeasurementDeleted(Guid measurementId)
    {
        _studyMeasurements.RemoveAll(existing => existing.Id == measurementId);
        if (_selectedMeasurementId == measurementId)
        {
            _selectedMeasurementId = null;
        }

        RefreshMeasurementPanels();
    }

    private string GetMeasurementToolLabel() => _measurementTool switch
    {
        MeasurementTool.None => "Navigate",
        MeasurementTool.PixelLens => "Pixel lens",
        MeasurementTool.Line => "Line",
        MeasurementTool.Angle => "Angle",
        MeasurementTool.Annotation => "Annotation",
        MeasurementTool.RectangleRoi => "Rectangle ROI",
        MeasurementTool.PolygonRoi => "Polygon ROI",
        MeasurementTool.Modify => "Modify",
        MeasurementTool.Erase => "Erase",
        _ => "Navigate",
    };
}
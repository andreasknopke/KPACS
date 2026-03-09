using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using KPACS.Viewer.Controls;
using KPACS.Viewer.Models;

namespace KPACS.Viewer;

public partial class StudyViewerWindow
{
    private readonly List<StudyMeasurement> _studyMeasurements = [];
    private MeasurementTool _measurementTool = MeasurementTool.None;
    private Guid? _selectedMeasurementId;

    private void InitializeMeasurementsUi()
    {
        _measurementTool = MeasurementTool.None;
        UpdateMeasurementToolButtons();
    }

    private void ConfigureMeasurementPanel(ViewportSlot slot, DicomViewPanel panel)
    {
        panel.SetMeasurementTool(_measurementTool);
        panel.SetMeasurements(_studyMeasurements, _selectedMeasurementId);
        panel.MeasurementCreated += OnPanelMeasurementCreated;
        panel.MeasurementUpdated += OnPanelMeasurementUpdated;
        panel.SelectedMeasurementChanged += OnPanelMeasurementSelectedChanged;
    }

    private void ApplyMeasurementContext(ViewportSlot slot)
    {
        slot.Panel.SetMeasurementTool(_measurementTool);
        slot.Panel.SetMeasurements(_studyMeasurements, _selectedMeasurementId);
    }

    private void RefreshMeasurementPanels()
    {
        foreach (ViewportSlot slot in _slots)
        {
            slot.Panel.SetMeasurementTool(_measurementTool);
            slot.Panel.SetMeasurements(_studyMeasurements, _selectedMeasurementId);
        }

        UpdateStatus();
    }

    private void OnMeasurementToolButtonClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not ToggleButton button || button.Tag is not string tag)
        {
            return;
        }

        if (!Enum.TryParse(tag, ignoreCase: true, out MeasurementTool tool))
        {
            tool = MeasurementTool.None;
        }

        SetMeasurementTool(tool);
    }

    private void SetMeasurementTool(MeasurementTool tool)
    {
        _measurementTool = tool;
        UpdateMeasurementToolButtons();
        RefreshMeasurementPanels();
    }

    private void UpdateMeasurementToolButtons()
    {
        if (MeasureNavigateButton is null)
        {
            return;
        }

        MeasureNavigateButton.IsChecked = _measurementTool == MeasurementTool.None;
        MeasureLensButton.IsChecked = _measurementTool == MeasurementTool.PixelLens;
        MeasureLineButton.IsChecked = _measurementTool == MeasurementTool.Line;
        MeasureAngleButton.IsChecked = _measurementTool == MeasurementTool.Angle;
        MeasureRectangleButton.IsChecked = _measurementTool == MeasurementTool.RectangleRoi;
        MeasurePolygonButton.IsChecked = _measurementTool == MeasurementTool.PolygonRoi;
    }

    private void OnPanelMeasurementCreated(StudyMeasurement measurement)
    {
        _studyMeasurements.RemoveAll(existing => existing.Id == measurement.Id);
        _studyMeasurements.Add(measurement);
        _selectedMeasurementId = measurement.Id;
        RefreshMeasurementPanels();
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

    private string GetMeasurementToolLabel() => _measurementTool switch
    {
        MeasurementTool.None => "Navigate",
        MeasurementTool.PixelLens => "Pixel lens",
        MeasurementTool.Line => "Line",
        MeasurementTool.Angle => "Angle",
        MeasurementTool.RectangleRoi => "Rectangle ROI",
        MeasurementTool.PolygonRoi => "Polygon ROI",
        _ => "Navigate",
    };
}
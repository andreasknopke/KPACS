using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using KPACS.Viewer.Models;

namespace KPACS.Viewer.Windows;

public sealed class NetworkSettingsWindow : Window
{
    private readonly TextBox _localAeTitleBox;
    private readonly NumericUpDown _localPortBox;
    private readonly TextBox _archiveNameBox;
    private readonly TextBox _archiveHostBox;
    private readonly NumericUpDown _archivePortBox;
    private readonly TextBox _remoteAeTitleBox;
    private readonly TextBox _inboxDirectoryBox;
    private readonly CheckBox _enableDicomLoggingCheckBox;
    private readonly TextBox _dicomLogPathBox;

    public NetworkSettingsWindow(DicomNetworkSettings settings)
    {
        Title = "Network Configuration";
        Width = 760;
        Height = 430;
        MinWidth = 720;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#FFE3E3E3"));

        RemoteArchiveEndpoint archive = settings.GetSelectedArchive() ?? new RemoteArchiveEndpoint();

        _localAeTitleBox = new TextBox { Text = settings.LocalAeTitle, HorizontalAlignment = HorizontalAlignment.Stretch };
        _localPortBox = new NumericUpDown { Value = settings.LocalPort, Minimum = 1, Maximum = 65535, HorizontalAlignment = HorizontalAlignment.Stretch };
        _archiveNameBox = new TextBox { Text = archive.Name, HorizontalAlignment = HorizontalAlignment.Stretch };
        _archiveHostBox = new TextBox { Text = archive.Host, HorizontalAlignment = HorizontalAlignment.Stretch };
        _archivePortBox = new NumericUpDown { Value = archive.Port, Minimum = 1, Maximum = 65535, HorizontalAlignment = HorizontalAlignment.Stretch };
        _remoteAeTitleBox = new TextBox { Text = archive.RemoteAeTitle, HorizontalAlignment = HorizontalAlignment.Stretch };
        _inboxDirectoryBox = new TextBox { Text = settings.InboxDirectory, HorizontalAlignment = HorizontalAlignment.Stretch };
        _enableDicomLoggingCheckBox = new CheckBox { IsChecked = settings.EnableDicomCommunicationLogging, Content = "Write detailed DICOM communication trace" };
        _dicomLogPathBox = new TextBox { Text = settings.DicomCommunicationLogPath, HorizontalAlignment = HorizontalAlignment.Stretch };

        var cancelButton = new Button { Content = "Cancel", MinWidth = 96 };
        cancelButton.Click += (_, _) => Close((DicomNetworkSettings?)null);

        var saveButton = new Button { Content = "Save", MinWidth = 96, IsDefault = true };
        saveButton.Click += (_, _) => Close(BuildSettings(settings, archive));

        var formGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("170,230,170,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto"),
            ColumnSpacing = 14,
            RowSpacing = 14,
        };

        AddLabel(formGrid, "Local AE title", 0, 0);
        AddControl(formGrid, _localAeTitleBox, 0, 1);
        AddLabel(formGrid, "Local receive port", 0, 2);
        AddControl(formGrid, _localPortBox, 0, 3);

        AddLabel(formGrid, "Archive name", 1, 0);
        AddControl(formGrid, _archiveNameBox, 1, 1);
        AddLabel(formGrid, "Remote AE title", 1, 2);
        AddControl(formGrid, _remoteAeTitleBox, 1, 3);

        AddLabel(formGrid, "Archive host", 2, 0);
        AddControl(formGrid, _archiveHostBox, 2, 1);
        AddLabel(formGrid, "Archive port", 2, 2);
        AddControl(formGrid, _archivePortBox, 2, 3);

        AddLabel(formGrid, "Inbound inbox", 3, 0);
        AddControl(formGrid, _inboxDirectoryBox, 3, 1, columnSpan: 3);

        AddLabel(formGrid, "Diagnostics", 4, 0);
        AddControl(formGrid, _enableDicomLoggingCheckBox, 4, 1, columnSpan: 3);

        AddLabel(formGrid, "Trace log file", 5, 0);
        AddControl(formGrid, _dicomLogPathBox, 5, 1, columnSpan: 3);

        Content = new Border
        {
            Padding = new Thickness(16),
            Child = new StackPanel
            {
                Spacing = 14,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Configure the local Storage SCP and the primary remote archive.",
                        FontWeight = FontWeight.Bold,
                        Foreground = new SolidColorBrush(Color.Parse("#FF222222")),
                    },
                    formGrid,
                    new TextBlock
                    {
                        Text = "Double-clicking a network study retrieves representative images first and continues a full background fetch into the local imagebox. Enable diagnostics to capture local-side association, request, and response details in a text log.",
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = new SolidColorBrush(Color.Parse("#FF4D4D4D")),
                    },
                    new Grid
                    {
                        ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
                        Margin = new Thickness(0, 8, 0, 0),
                        Children =
                        {
                            cancelButton,
                            saveButton,
                        },
                    },
                },
            },
        };

        Grid.SetColumn(cancelButton, 1);
        Grid.SetColumn(saveButton, 2);
    }

    private DicomNetworkSettings BuildSettings(DicomNetworkSettings existingSettings, RemoteArchiveEndpoint existingArchive)
    {
        return new DicomNetworkSettings
        {
            LocalAeTitle = _localAeTitleBox.Text ?? string.Empty,
            LocalPort = Convert.ToInt32(_localPortBox.Value ?? 11112),
            InboxDirectory = _inboxDirectoryBox.Text ?? string.Empty,
            EnableDicomCommunicationLogging = _enableDicomLoggingCheckBox.IsChecked == true,
            DicomCommunicationLogPath = _dicomLogPathBox.Text ?? string.Empty,
            SelectedArchiveId = existingArchive.Id,
            Archives =
            [
                new RemoteArchiveEndpoint
                {
                    Id = existingArchive.Id,
                    Name = _archiveNameBox.Text ?? string.Empty,
                    Host = _archiveHostBox.Text ?? string.Empty,
                    Port = Convert.ToInt32(_archivePortBox.Value ?? 104),
                    RemoteAeTitle = _remoteAeTitleBox.Text ?? string.Empty,
                },
            ],
        };
    }

    private static void AddLabel(Grid grid, string text, int row, int column)
    {
        var label = new TextBlock
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        Grid.SetRow(label, row);
        Grid.SetColumn(label, column);
        grid.Children.Add(label);
    }

    private static void AddControl(Grid grid, Control control, int row, int column, int columnSpan = 1)
    {
        control.HorizontalAlignment = HorizontalAlignment.Stretch;
        Grid.SetRow(control, row);
        Grid.SetColumn(control, column);
        Grid.SetColumnSpan(control, columnSpan);
        grid.Children.Add(control);
    }
}

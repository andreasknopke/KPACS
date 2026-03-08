using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using KPACS.Viewer.Models;
using KPACS.Viewer.Services;

namespace KPACS.Viewer;

public partial class MainWindow : Window
{
    private readonly App _app;
    private bool _uiReady;
    private bool _showPatientPanel = true;
    private BrowserMode _browserMode = BrowserMode.Database;
    private List<StudyListItem> _allStudies = [];
    private List<StudyListItem> _filesystemScannedStudies = [];
    private Dictionary<string, StudyDetails> _filesystemPreviewDetails = new(StringComparer.Ordinal);
    private readonly ObservableCollection<StudyListItem> _studies = [];
    private readonly ObservableCollection<PatientRow> _patients = [];
    private readonly ObservableCollection<SeriesGridRow> _seriesRows = [];
    private readonly ObservableCollection<FilesystemFolderNode> _filesystemRoots = [];
    private string? _filesystemRootPath;
    private string? _lastScannedFolderPath;
    private bool _lastScanPreferDicomDir;

    public MainWindow(App app)
    {
        _app = app;
        InitializeComponent();

        PatientGrid.ItemsSource = _patients;
        StudyGrid.ItemsSource = _studies;
        SeriesGrid.ItemsSource = _seriesRows;
        FilesystemTreeView.ItemsSource = _filesystemRoots;
        _uiReady = true;
        Opened += async (_, _) => await InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        BrowserModeTabs.SelectedIndex = 1;
        _browserMode = BrowserMode.Database;
        UpdateModeUi();
        await RefreshCurrentModeAsync();
    }

    private async Task RefreshCurrentModeAsync(string? statusOverride = null, bool applySearchFilters = true)
    {
        switch (_browserMode)
        {
            case BrowserMode.Database:
                await LoadDatabaseStudiesAsync(statusOverride);
                break;
            case BrowserMode.Filesystem:
                LoadFilesystemPreviewStudies(statusOverride, applySearchFilters);
                break;
            case BrowserMode.Network:
                ClearStudyResults("Network mode", statusOverride ?? "Network mode is not implemented yet.");
                break;
            case BrowserMode.Email:
                ClearStudyResults("Email mode", statusOverride ?? "Email mode is not implemented yet.");
                break;
        }
    }

    private async Task LoadDatabaseStudiesAsync(string? statusOverride)
    {
        _allStudies = await _app.Repository.SearchStudiesAsync(BuildQuery());
        BuildPatientRows();
        ApplyPatientFilter();

        DatabaseStatsText.Text = $"{_allStudies.Count} studies indexed in SQLite.";
        StatusText.Text = statusOverride ?? (_allStudies.Count == 0
            ? "K-PACS imagebox ready — switch to Filesystem mode to scan media before importing."
            : $"Loaded {_allStudies.Count} studies from the K-PACS imagebox.");
    }

    private void LoadFilesystemPreviewStudies(string? statusOverride, bool applySearchFilters)
    {
        _allStudies = applySearchFilters
            ? ApplyStudyQuery(_filesystemScannedStudies)
            : _filesystemScannedStudies
                .OrderByDescending(study => study.StudyDate)
                .ThenBy(study => study.PatientName)
                .ToList();

        BuildPatientRows();
        ApplyPatientFilter();

        DatabaseStatsText.Text = _filesystemPreviewDetails.Count == 0
            ? "No filesystem scan loaded."
            : $"{_filesystemPreviewDetails.Count} studies available from the last filesystem scan.";

        StatusText.Text = statusOverride ?? (_filesystemPreviewDetails.Count == 0
            ? "Expand Computer, choose a drive or folder, then right-click it and select Scan folder."
            : applySearchFilters
                ? "Filesystem scan loaded. Studies are preview-only until you import or view them."
                : "Filesystem scan loaded. Fresh scan results are shown without applying the search filter yet.");
    }

    private void ClearStudyResults(string statsText, string statusText)
    {
        _allStudies = [];
        _studies.Clear();
        _patients.Clear();
        _seriesRows.Clear();
        PatientGrid.SelectedItem = null;
        StudyGrid.SelectedItem = null;
        DatabaseStatsText.Text = statsText;
        StudySummaryText.Text = "No studies available in the current mode.";
        StatusText.Text = statusText;
    }

    private StudyQuery BuildQuery()
    {
        return new StudyQuery
        {
            PatientId = PatientIdBox.Text?.Trim() ?? string.Empty,
            PatientName = PatientNameBox.Text?.Trim() ?? string.Empty,
            PatientBirthDate = ParseDateText(PatientBirthDateBox.Text)?.ToString("yyyyMMdd") ?? string.Empty,
            AccessionNumber = AccessionBox.Text?.Trim() ?? string.Empty,
            ReferringPhysician = ReferringPhysicianBox.Text?.Trim() ?? string.Empty,
            StudyDescription = StudyDescriptionBox.Text?.Trim() ?? string.Empty,
            QuickSearch = QuickSearchBox.Text?.Trim() ?? string.Empty,
            Modalities = GetSelectedModalities(),
            FromStudyDate = ParseDateText(FromDateBox.Text),
            ToStudyDate = ParseDateText(ToDateBox.Text),
        };
    }

    private List<StudyListItem> ApplyStudyQuery(IEnumerable<StudyListItem> sourceStudies)
    {
        StudyQuery query = BuildQuery();
        IEnumerable<StudyListItem> queryable = sourceStudies;

        if (!string.IsNullOrWhiteSpace(query.PatientId))
            queryable = queryable.Where(study => ContainsIgnoreCase(study.PatientId, query.PatientId));
        if (!string.IsNullOrWhiteSpace(query.PatientName))
            queryable = queryable.Where(study => ContainsIgnoreCase(study.PatientName, query.PatientName));
        if (!string.IsNullOrWhiteSpace(query.PatientBirthDate))
            queryable = queryable.Where(study => string.Equals(study.PatientBirthDate, query.PatientBirthDate, StringComparison.Ordinal));
        if (!string.IsNullOrWhiteSpace(query.AccessionNumber))
            queryable = queryable.Where(study => ContainsIgnoreCase(study.AccessionNumber, query.AccessionNumber));
        if (!string.IsNullOrWhiteSpace(query.ReferringPhysician))
            queryable = queryable.Where(study => ContainsIgnoreCase(study.ReferringPhysician, query.ReferringPhysician));
        if (!string.IsNullOrWhiteSpace(query.StudyDescription))
            queryable = queryable.Where(study => ContainsIgnoreCase(study.StudyDescription, query.StudyDescription));
        if (!string.IsNullOrWhiteSpace(query.QuickSearch))
        {
            queryable = queryable.Where(study =>
                ContainsIgnoreCase(study.PatientName, query.QuickSearch)
                || ContainsIgnoreCase(study.PatientId, query.QuickSearch)
                || ContainsIgnoreCase(study.StudyDescription, query.QuickSearch)
                || ContainsIgnoreCase(study.Modalities, query.QuickSearch));
        }
        if (query.Modalities.Count > 0)
            queryable = queryable.Where(study => query.Modalities.Any(modality => ContainsIgnoreCase(study.Modalities, modality)));
        if (query.FromStudyDate is not null)
            queryable = queryable.Where(study => TryParseDicomDate(study.StudyDate, out DateOnly date) && date >= query.FromStudyDate.Value);
        if (query.ToStudyDate is not null)
            queryable = queryable.Where(study => TryParseDicomDate(study.StudyDate, out DateOnly date) && date <= query.ToStudyDate.Value);

        return queryable
            .OrderByDescending(study => study.StudyDate)
            .ThenBy(study => study.PatientName)
            .ToList();
    }

    private void BuildPatientRows()
    {
        if (_browserMode != BrowserMode.Database || !_showPatientPanel)
        {
            _patients.Clear();
            PatientGrid.SelectedItem = null;
            return;
        }

        string? selectedKey = (PatientGrid.SelectedItem as PatientRow)?.SelectionKey;

        var groupedPatients = _allStudies
            .GroupBy(study => $"{study.PatientId}\u001F{study.PatientName}")
            .Select(group =>
            {
                StudyListItem latest = group.OrderByDescending(item => item.StudyDate).ThenBy(item => item.PatientName).First();
                return new PatientRow
                {
                    PatientId = latest.PatientId,
                    PatientName = latest.PatientName,
                    PatientBirthDate = latest.DisplayPatientBirthDate,
                    StudyCount = group.Count(),
                    LatestStudyDate = latest.DisplayStudyDate,
                    Modalities = string.Join(", ", group.Select(item => item.Modalities).Where(item => !string.IsNullOrWhiteSpace(item)).Distinct().OrderBy(item => item)),
                };
            })
            .OrderBy(patient => patient.PatientName)
            .ThenBy(patient => patient.PatientId)
            .ToList();

        _patients.Clear();
        foreach (PatientRow patient in groupedPatients)
        {
            _patients.Add(patient);
        }

        if (_patients.Count == 0)
        {
            PatientGrid.SelectedItem = null;
            return;
        }

        PatientRow? selectedPatient = _patients.FirstOrDefault(patient => patient.SelectionKey == selectedKey) ?? _patients[0];
        PatientGrid.SelectedItem = selectedPatient;
    }

    private void ApplyPatientFilter()
    {
        string? selectedKey = _browserMode == BrowserMode.Database && _showPatientPanel
            ? (PatientGrid.SelectedItem as PatientRow)?.SelectionKey
            : null;

        List<StudyListItem> visibleStudies = string.IsNullOrWhiteSpace(selectedKey)
            ? _allStudies
            : _allStudies.Where(study => $"{study.PatientId}\u001F{study.PatientName}" == selectedKey).ToList();

        string? previousSelectionId = (StudyGrid.SelectedItem as StudyListItem)?.SelectionId;

        _studies.Clear();
        foreach (StudyListItem study in visibleStudies.OrderByDescending(item => item.StudyDate).ThenBy(item => item.PatientName))
        {
            _studies.Add(study);
        }

        StudyGrid.SelectedItem = previousSelectionId is not null
            ? _studies.FirstOrDefault(study => study.SelectionId == previousSelectionId)
            : _studies.FirstOrDefault();

        StudySummaryText.Text = _studies.Count == 0
            ? "No studies match the current selection."
            : _browserMode == BrowserMode.Filesystem
                ? $"{_studies.Count} preview studies match the current filter. Double-click to import and open."
                : _showPatientPanel
                    ? $"{_studies.Count} studies for the selected patient. Double-click a study to open it."
                    : $"{_studies.Count} studies match the current filter. Double-click a study to open it.";
    }

    private async Task<StudyDetails?> LoadStudyDetailsForSelectionAsync(StudyListItem selectedStudy)
    {
        return _browserMode switch
        {
            BrowserMode.Database => await _app.Repository.GetStudyDetailsAsync(selectedStudy.StudyKey),
            BrowserMode.Filesystem => _filesystemPreviewDetails.GetValueOrDefault(selectedStudy.StudyInstanceUid),
            _ => null,
        };
    }

    private async Task LoadSelectedStudyDetailsAsync()
    {
        if (StudyGrid.SelectedItem is not StudyListItem selectedStudy)
        {
            _seriesRows.Clear();
            StudySummaryText.Text = "Select a study to see its series overview.";
            return;
        }

        StudyDetails? details = await LoadStudyDetailsForSelectionAsync(selectedStudy);
        if (details is null)
        {
            _seriesRows.Clear();
            StudySummaryText.Text = "Selected study could not be loaded.";
            return;
        }

        _seriesRows.Clear();
        foreach (SeriesRecord series in details.Series.OrderBy(series => series.SeriesNumber).ThenBy(series => series.SeriesDescription))
        {
            _seriesRows.Add(new SeriesGridRow
            {
                SeriesNumber = series.SeriesNumber,
                Modality = series.Modality,
                SeriesDescription = string.IsNullOrWhiteSpace(series.SeriesDescription) ? "(no description)" : series.SeriesDescription,
                InstanceCount = series.Instances.Count,
                FirstFileName = series.Instances.Count == 0 ? string.Empty : Path.GetFileName(series.Instances[0].FilePath),
            });
        }

        string modeSuffix = _browserMode == BrowserMode.Filesystem ? "preview" : "local";
        StudySummaryText.Text = $"{selectedStudy.PatientName}   {selectedStudy.DisplayPatientBirthDate}   {selectedStudy.DisplayStudyDate}   {selectedStudy.Modalities}   {details.Series.Count} series / {selectedStudy.InstanceCount} images   [{modeSuffix}]";
    }

    private async Task OpenSelectedStudyAsync()
    {
        if (StudyGrid.SelectedItem is not StudyListItem selectedStudy)
        {
            StatusText.Text = "Select a study first.";
            return;
        }

        StudyDetails? details = _browserMode switch
        {
            BrowserMode.Database => await _app.Repository.GetStudyDetailsAsync(selectedStudy.StudyKey),
            BrowserMode.Filesystem => await ImportPreviewStudyAsync(selectedStudy),
            BrowserMode.Network => null,
            BrowserMode.Email => null,
            _ => null,
        };

        if (details is null)
        {
            if (_browserMode is BrowserMode.Network or BrowserMode.Email)
            {
                StatusText.Text = "This browser mode does not provide studies yet.";
            }
            else if (_browserMode == BrowserMode.Database)
            {
                StatusText.Text = "Selected study could not be loaded from SQLite.";
            }
            return;
        }

        var viewer = new StudyViewerWindow(new ViewerStudyContext
        {
            StudyDetails = details,
            LayoutRows = 1,
            LayoutColumns = 1,
        });
        await viewer.ShowDialog(this);
    }

    private async Task<StudyDetails?> ImportPreviewStudyAsync(StudyListItem selectedStudy)
    {
        if (!_filesystemPreviewDetails.TryGetValue(selectedStudy.StudyInstanceUid, out StudyDetails? previewDetails))
        {
            StatusText.Text = "Preview study data is no longer available. Please scan the folder again.";
            return null;
        }

        ImportResult result = await _app.ImportService.ImportStudyAsync(previewDetails);
        string summary = string.Join("  ", result.Messages.Where(message => !string.IsNullOrWhiteSpace(message)));
        StudyDetails? importedDetails = await _app.Repository.GetStudyDetailsByStudyInstanceUidAsync(selectedStudy.StudyInstanceUid);
        if (importedDetails is null)
        {
            StatusText.Text = string.IsNullOrWhiteSpace(summary)
                ? "Study import finished, but the imported study could not be loaded from SQLite."
                : summary;
            return null;
        }

        StatusText.Text = string.IsNullOrWhiteSpace(summary)
            ? $"Imported study {selectedStudy.PatientName} into the local database."
            : summary;
        return importedDetails;
    }

    private async void OnSearchClick(object? sender, RoutedEventArgs e) => await RefreshCurrentModeAsync();

    private async void OnRefreshClick(object? sender, RoutedEventArgs e)
    {
        if (_browserMode == BrowserMode.Filesystem && !string.IsNullOrWhiteSpace(_lastScannedFolderPath))
        {
            await ScanFolderAsync(_lastScannedFolderPath, _lastScanPreferDicomDir);
            return;
        }

        await RefreshCurrentModeAsync();
    }

    private async void OnTogglePatientPanelClick(object? sender, RoutedEventArgs e)
    {
        _showPatientPanel = !_showPatientPanel;
        UpdateModeUi();
        await RefreshCurrentModeAsync();
    }

    private async void OnTodayClick(object? sender, RoutedEventArgs e)
    {
        string today = DateTime.Now.ToString("dd.MM.yyyy");
        FromDateBox.Text = today;
        ToDateBox.Text = today;
        await RefreshCurrentModeAsync();
    }

    private async void OnYesterdayClick(object? sender, RoutedEventArgs e)
    {
        string yesterday = DateTime.Now.Date.AddDays(-1).ToString("dd.MM.yyyy");
        FromDateBox.Text = yesterday;
        ToDateBox.Text = yesterday;
        await RefreshCurrentModeAsync();
    }

    private async void OnClearClick(object? sender, RoutedEventArgs e)
    {
        PatientIdBox.Text = string.Empty;
        PatientNameBox.Text = string.Empty;
        PatientBirthDateBox.Text = string.Empty;
        AccessionBox.Text = string.Empty;
        ReferringPhysicianBox.Text = string.Empty;
        StudyDescriptionBox.Text = string.Empty;
        QuickSearchBox.Text = string.Empty;
        FromDateBox.Text = string.Empty;
        ToDateBox.Text = string.Empty;
        SetAllModalities(false);
        await RefreshCurrentModeAsync();
    }

    private async void OnAllModalitiesClick(object? sender, RoutedEventArgs e)
    {
        SetAllModalities(true);
        await RefreshCurrentModeAsync();
    }

    private async void OnClearModalitiesClick(object? sender, RoutedEventArgs e)
    {
        SetAllModalities(false);
        await RefreshCurrentModeAsync();
    }

    private async void OnImportSelectedClick(object? sender, RoutedEventArgs e)
    {
        if (_browserMode != BrowserMode.Filesystem)
        {
            StatusText.Text = "Import uses Filesystem mode. Scan a folder first, then import the selected study.";
            return;
        }

        if (StudyGrid.SelectedItem is not StudyListItem selectedStudy)
        {
            StatusText.Text = "Select a preview study first.";
            return;
        }

        StudyDetails? details = await ImportPreviewStudyAsync(selectedStudy);
        if (details is not null)
        {
            await RefreshCurrentModeAsync(StatusText.Text);
        }
    }

    private async void OnBrowseFilesystemRootClick(object? sender, RoutedEventArgs e)
    {
        if (_browserMode != BrowserMode.Filesystem)
        {
            StatusText.Text = "Browse is available in Filesystem mode.";
            return;
        }

        TopLevel? topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
        {
            return;
        }

        IReadOnlyList<IStorageFolder> folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose root folder for filesystem browser",
            AllowMultiple = false,
        });

        string? path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        await LoadFilesystemRootAsync(path);
    }

    private async Task LoadFilesystemRootAsync(string path)
    {
        _filesystemRootPath = path;
        FilesystemRootText.Text = $"Root: {path}";
        FilesystemHintText.IsVisible = false;
        FilesystemTreeView.IsVisible = true;
        StatusText.Text = $"Loading filesystem tree: {path}";

        FilesystemFolderNode rootNode = await Task.Run(() => BuildFilesystemFolderNode(path));
        _filesystemRoots.Clear();
        _filesystemRoots.Add(rootNode);
        StatusText.Text = $"Filesystem root loaded: {path}. Right-click a folder and select Scan folder.";
    }

    private Task LoadComputerRootAsync()
    {
        _filesystemRootPath = null;
        FilesystemRootText.Text = "Root: Computer";
        FilesystemHintText.IsVisible = false;
        FilesystemTreeView.IsVisible = true;

        _filesystemRoots.Clear();
        _filesystemRoots.Add(BuildComputerRootNode());
        StatusText.Text = "Filesystem mode ready. Expand Computer to choose a drive or folder.";
        return Task.CompletedTask;
    }

    private async Task ScanFolderAsync(string folderPath, bool preferDicomDir)
    {
        StatusText.Text = $"Scanning folder: {folderPath}";
        FilesystemScanResult scanResult = await _app.FilesystemScanService.ScanPathAsync(folderPath, preferDicomDir);

        _lastScannedFolderPath = folderPath;
        _lastScanPreferDicomDir = preferDicomDir;
        _filesystemPreviewDetails = scanResult.Studies.ToDictionary(study => study.Study.StudyInstanceUid, StringComparer.Ordinal);
        _filesystemScannedStudies = scanResult.Studies.Select(study => study.Study).ToList();

        string summary = string.Join("  ", scanResult.Messages.Where(message => !string.IsNullOrWhiteSpace(message)));
        string statusMessage = string.IsNullOrWhiteSpace(summary)
            ? $"Scanned folder {folderPath}. {_filesystemPreviewDetails.Count} studies available for preview."
            : summary;

        await RefreshCurrentModeAsync(statusMessage, applySearchFilters: false);
    }

    private void OnPseudonymizeClick(object? sender, RoutedEventArgs e)
    {
        _ = OnPseudonymizeInternalAsync();
    }

    private async Task OnPseudonymizeInternalAsync()
    {
        if (_browserMode != BrowserMode.Database)
        {
            StatusText.Text = "Pseudonymize is only available for imported studies in Database mode.";
            return;
        }

        if (StudyGrid.SelectedItem is not StudyListItem selectedStudy)
        {
            StatusText.Text = "Select a study before pseudonymization.";
            return;
        }

        var dialog = new PseudonymizeWindow();
        bool accepted = await dialog.ShowDialog<bool>(this);
        if (!accepted || dialog.Request is null)
        {
            return;
        }

        try
        {
            int changedFiles = await _app.PseudonymizationService.PseudonymizeStudyAsync(selectedStudy.StudyKey, dialog.Request);
            StatusText.Text = $"Pseudonymized {changedFiles} DICOM files for study {selectedStudy.PatientName}.";
            await RefreshCurrentModeAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Pseudonymize failed: {ex.Message}";
        }
    }

    private async void OnDeleteStudyClick(object? sender, RoutedEventArgs e) => await DeleteSelectedStudyAsync();

    private async void OnOpenViewerClick(object? sender, RoutedEventArgs e) => await OpenSelectedStudyAsync();

    private void OnStudyGridPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(StudyGrid).Properties.IsRightButtonPressed)
        {
            return;
        }

        if (e.Source is not Control sourceControl)
        {
            return;
        }

        DataGridRow? row = sourceControl.FindAncestorOfType<DataGridRow>();
        if (row?.DataContext is StudyListItem study)
        {
            StudyGrid.SelectedItem = study;
        }
    }

    private void OnStudyContextMenuOpened(object? sender, RoutedEventArgs e)
    {
        DeleteStudyMenuItem.IsEnabled = _browserMode == BrowserMode.Database && StudyGrid.SelectedItem is StudyListItem;
    }

    private void OnFilesystemTreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not Control sourceControl)
        {
            return;
        }

        TreeViewItem? item = sourceControl.FindAncestorOfType<TreeViewItem>();
        if (item?.DataContext is FilesystemFolderNode node)
        {
            EnsureFilesystemNodeChildrenLoaded(node);

            PointerPointProperties properties = e.GetCurrentPoint(FilesystemTreeView).Properties;
            if (properties.IsLeftButtonPressed || properties.IsRightButtonPressed)
            {
                FilesystemTreeView.SelectedItem = node;
            }
        }
    }

    private void OnFilesystemContextMenuOpened(object? sender, RoutedEventArgs e)
    {
        ScanFolderMenuItem.IsEnabled = FilesystemTreeView.SelectedItem is FilesystemFolderNode node
            && !string.IsNullOrWhiteSpace(node.FullPath)
            && Directory.Exists(node.FullPath);
    }

    private void OnFilesystemTreeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (FilesystemTreeView.SelectedItem is FilesystemFolderNode node)
        {
            EnsureFilesystemNodeChildrenLoaded(node);
            StatusText.Text = $"Selected folder: {node.FullPath}";
        }
    }

    private void OnFilesystemTreeKeyDown(object? sender, KeyEventArgs e)
    {
        if (FilesystemTreeView.SelectedItem is not FilesystemFolderNode node)
        {
            return;
        }

        if (e.Key is Key.Right or Key.Enter or Key.Space)
        {
            EnsureFilesystemNodeChildrenLoaded(node);
        }
    }

    private async void OnScanFolderClick(object? sender, RoutedEventArgs e)
    {
        if (FilesystemTreeView.SelectedItem is not FilesystemFolderNode node)
        {
            StatusText.Text = "Select a folder first.";
            return;
        }

        bool preferDicomDir = false;
        if (DicomFilesystemScanService.ContainsDicomDir(node.FullPath))
        {
            var prompt = new UseDicomDirPromptWindow(node.FullPath);
            preferDicomDir = await prompt.ShowDialog<bool>(this);
        }

        await ScanFolderAsync(node.FullPath, preferDicomDir);
    }

    private async void OnBrowserModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (BrowserModeTabs is null || !_uiReady)
        {
            return;
        }

        _browserMode = BrowserModeTabs.SelectedIndex switch
        {
            0 => BrowserMode.Network,
            1 => BrowserMode.Database,
            2 => BrowserMode.Filesystem,
            3 => BrowserMode.Email,
            _ => BrowserMode.Database,
        };

        if (PatientPanel is null
            || BrowserContentGrid is null
            || FilesystemPanel is null
            || ModePlaceholderPanel is null
            || TogglePatientPanelButton is null
            || BrowseFilesystemButton is null
            || ImportActionButton is null
            || ModifyActionButton is null
            || ModePlaceholderText is null)
        {
            return;
        }

        UpdateModeUi();

        if (_browserMode == BrowserMode.Filesystem && _filesystemRoots.Count == 0)
        {
            await LoadComputerRootAsync();
        }

        await RefreshCurrentModeAsync();
    }

    private void UpdateModeUi()
    {
        bool databaseMode = _browserMode == BrowserMode.Database;
        bool filesystemMode = _browserMode == BrowserMode.Filesystem;
        bool placeholderMode = _browserMode is BrowserMode.Network or BrowserMode.Email;
        bool showPatientPanel = databaseMode && _showPatientPanel;
        bool showMiddlePane = showPatientPanel || filesystemMode || placeholderMode;

        PatientPanel.IsVisible = showPatientPanel;
        FilesystemPanel.IsVisible = filesystemMode;
        ModePlaceholderPanel.IsVisible = placeholderMode;
        if (BrowserContentGrid.ColumnDefinitions.Count > 1)
        {
            BrowserContentGrid.ColumnDefinitions[1].Width = showMiddlePane ? new GridLength(220) : new GridLength(0);
        }
        TogglePatientPanelButton.IsVisible = databaseMode;
        TogglePatientPanelButton.Content = showPatientPanel ? "Hide patients" : "Show patients";
        BrowseFilesystemButton.IsEnabled = filesystemMode;
        ImportActionButton.IsEnabled = filesystemMode;
        ModifyActionButton.IsEnabled = databaseMode;

        ModePlaceholderText.Text = _browserMode switch
        {
            BrowserMode.Network => "Network mode is present for visual parity, but remote query/retrieve is not implemented yet.",
            BrowserMode.Email => "Email mode is present for visual parity, but email/export workflows are not implemented yet.",
            _ => string.Empty,
        };

        if (filesystemMode)
        {
            FilesystemRootText.Text = string.IsNullOrWhiteSpace(_filesystemRootPath)
                ? "Root: Computer"
                : $"Root: {_filesystemRootPath}";
            FilesystemHintText.IsVisible = _filesystemRoots.Count == 0;
            FilesystemTreeView.IsVisible = _filesystemRoots.Count > 0;
        }
    }

    private void OnPatientSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_browserMode == BrowserMode.Database && _showPatientPanel)
        {
            ApplyPatientFilter();
        }
    }

    private async void OnStudySelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_browserMode == BrowserMode.Database && _showPatientPanel && StudyGrid.SelectedItem is StudyListItem study)
        {
            PatientRow? row = _patients.FirstOrDefault(patient => patient.SelectionKey == $"{study.PatientId}\u001F{study.PatientName}");
            if (row is not null && !ReferenceEquals(PatientGrid.SelectedItem, row))
            {
                PatientGrid.SelectedItem = row;
            }
        }

        await LoadSelectedStudyDetailsAsync();
    }

    private async void OnStudyDoubleTapped(object? sender, TappedEventArgs e) => await OpenSelectedStudyAsync();

    private async Task DeleteSelectedStudyAsync()
    {
        if (_browserMode != BrowserMode.Database)
        {
            StatusText.Text = "Delete Study is only available in Database mode.";
            return;
        }

        if (StudyGrid.SelectedItem is not StudyListItem selectedStudy)
        {
            StatusText.Text = "Select a study first.";
            return;
        }

        var confirmWindow = new ConfirmDeleteStudyWindow(selectedStudy);
        bool confirmed = await confirmWindow.ShowDialog<bool>(this);
        if (!confirmed)
        {
            StatusText.Text = "Delete study cancelled.";
            return;
        }

        try
        {
            await _app.StudyDeletionService.DeleteStudyAsync(selectedStudy);
            await RefreshCurrentModeAsync($"Deleted study {selectedStudy.PatientName} ({selectedStudy.DisplayStudyDate}) from the K-PACS imagebox.");
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Delete study failed: {ex.Message}";
        }
    }

    protected override async void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Key == Key.Enter)
        {
            await RefreshCurrentModeAsync();
            e.Handled = true;
        }
        else if (e.Key == Key.O && e.KeyModifiers == KeyModifiers.Control)
        {
            await OpenSelectedStudyAsync();
            e.Handled = true;
        }
    }

    private static bool ContainsIgnoreCase(string? source, string value)
    {
        return !string.IsNullOrWhiteSpace(source) && source.Contains(value, StringComparison.OrdinalIgnoreCase);
    }

    private List<string> GetSelectedModalities()
    {
        return GetModalityCheckBoxes()
            .Where(checkBox => checkBox.IsChecked == true)
            .Select(checkBox => checkBox.Content?.ToString() ?? string.Empty)
            .Where(content => !string.IsNullOrWhiteSpace(content))
            .ToList();
    }

    private IEnumerable<CheckBox> GetModalityCheckBoxes()
    {
        yield return ModalityUsCheckBox;
        yield return ModalityCtCheckBox;
        yield return ModalityMrCheckBox;
        yield return ModalityDrCheckBox;
        yield return ModalityOtCheckBox;
        yield return ModalityScCheckBox;
        yield return ModalitySrCheckBox;
        yield return ModalityRfCheckBox;
        yield return ModalityNmCheckBox;
        yield return ModalityCrCheckBox;
        yield return ModalityXaCheckBox;
        yield return ModalityMgCheckBox;
    }

    private void SetAllModalities(bool isChecked)
    {
        foreach (CheckBox checkBox in GetModalityCheckBoxes())
        {
            checkBox.IsChecked = isChecked;
        }
    }

    private static DateOnly? ParseDateText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string trimmed = text.Trim();
        string[] formats = ["dd.MM.yyyy", "d.M.yyyy", "yyyyMMdd", "yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy"];
        foreach (string format in formats)
        {
            if (DateOnly.TryParseExact(trimmed, format, null, System.Globalization.DateTimeStyles.None, out DateOnly parsed))
            {
                return parsed;
            }
        }

        return DateOnly.TryParse(trimmed, out DateOnly fallback) ? fallback : null;
    }

    private static bool TryParseDicomDate(string? dicomDate, out DateOnly date)
    {
        if (!string.IsNullOrWhiteSpace(dicomDate)
            && dicomDate.Length >= 8
            && int.TryParse(dicomDate[..4], out int year)
            && int.TryParse(dicomDate[4..6], out int month)
            && int.TryParse(dicomDate[6..8], out int day))
        {
            try
            {
                date = new DateOnly(year, month, day);
                return true;
            }
            catch
            {
            }
        }

        date = default;
        return false;
    }

    private static FilesystemFolderNode BuildFilesystemFolderNode(string path)
    {
        var node = new FilesystemFolderNode
        {
            DisplayName = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } name ? name : path,
            FullPath = path,
            ChildrenLoaded = false,
        };

        if (DirectoryHasSubdirectories(path))
        {
            node.Children.Add(CreatePlaceholderNode());
        }

        return node;
    }

    private static FilesystemFolderNode BuildComputerRootNode()
    {
        var node = new FilesystemFolderNode
        {
            DisplayName = "Computer",
            FullPath = string.Empty,
            ChildrenLoaded = true,
        };

        foreach (DriveInfo drive in DriveInfo.GetDrives().OrderBy(drive => drive.Name))
        {
            string driveLabel = drive.Name;
            try
            {
                if (!string.IsNullOrWhiteSpace(drive.VolumeLabel))
                {
                    driveLabel = $"{drive.Name} ({drive.VolumeLabel})";
                }
                else if (drive.DriveType == DriveType.CDRom)
                {
                    driveLabel = $"{drive.Name} (CD-ROM)";
                }
            }
            catch
            {
            }

            var driveNode = new FilesystemFolderNode
            {
                DisplayName = driveLabel,
                FullPath = drive.Name,
                ChildrenLoaded = false,
            };

            if (DirectoryHasSubdirectories(drive.Name))
            {
                driveNode.Children.Add(CreatePlaceholderNode());
            }

            node.Children.Add(driveNode);
        }

        return node;
    }

    private static FilesystemFolderNode CreatePlaceholderNode()
    {
        return new FilesystemFolderNode
        {
            DisplayName = string.Empty,
            FullPath = string.Empty,
            IsPlaceholder = true,
            ChildrenLoaded = true,
        };
    }

    private static bool DirectoryHasSubdirectories(string path)
    {
        try
        {
            return Directory.Exists(path) && Directory.EnumerateDirectories(path).Any();
        }
        catch
        {
            return false;
        }
    }

    private static void EnsureFilesystemNodeChildrenLoaded(FilesystemFolderNode node)
    {
        if (node.ChildrenLoaded || node.IsPlaceholder || string.IsNullOrWhiteSpace(node.FullPath) || !Directory.Exists(node.FullPath))
        {
            return;
        }

        node.Children.Clear();

        try
        {
            foreach (string directory in Directory.EnumerateDirectories(node.FullPath).OrderBy(Path.GetFileName))
            {
                FilesystemFolderNode childNode = BuildFilesystemFolderNode(directory);
                node.Children.Add(childNode);
            }
        }
        catch
        {
        }

        node.ChildrenLoaded = true;
    }

    private enum BrowserMode
    {
        Network,
        Database,
        Filesystem,
        Email,
    }

    private sealed class PatientRow
    {
        public string PatientName { get; init; } = string.Empty;
        public string PatientId { get; init; } = string.Empty;
        public string PatientBirthDate { get; init; } = string.Empty;
        public int StudyCount { get; init; }
        public string LatestStudyDate { get; init; } = string.Empty;
        public string Modalities { get; init; } = string.Empty;
        public string SelectionKey => $"{PatientId}\u001F{PatientName}";
    }

    private sealed class SeriesGridRow
    {
        public int SeriesNumber { get; init; }
        public string Modality { get; init; } = string.Empty;
        public string SeriesDescription { get; init; } = string.Empty;
        public int InstanceCount { get; init; }
        public string FirstFileName { get; init; } = string.Empty;
    }
}

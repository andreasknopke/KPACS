using System.Collections.ObjectModel;
using System.Net.Sockets;
using System.Text.Json;
using Avalonia.Media;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using KPACS.DCMClasses;
using KPACS.Viewer.Models;
using KPACS.Viewer.Services;
using KPACS.Viewer.Windows;

namespace KPACS.Viewer;

public partial class MainWindow : Window
{
    private const int CurrentOnboardingVersion = 1;
    private const double DefaultPatientPaneWidth = 320;
    private const double MinPatientPaneWidth = 240;
    private const double MaxPatientPaneWidth = 520;
    private const double DefaultSeriesPaneHeight = 190;
    private const double MinSeriesPaneHeight = 120;
    private const double MaxSeriesPaneHeight = 420;
    private readonly App _app;
    private bool _uiReady;
    private bool _showPatientPanel = true;
    private BrowserMode _browserMode = BrowserMode.Database;
    private List<StudyListItem> _allStudies = [];
    private List<StudyListItem> _filesystemScannedStudies = [];
    private Dictionary<string, StudyDetails> _filesystemPreviewDetails = new(StringComparer.Ordinal);
    private Dictionary<string, RemoteStudySearchResult> _networkSearchResults = new(StringComparer.Ordinal);
    private Dictionary<string, StudyDetails> _networkPreviewDetails = new(StringComparer.Ordinal);
    private Dictionary<string, List<RemoteSeriesPreview>> _networkSeriesPreviews = new(StringComparer.Ordinal);
    private readonly ObservableCollection<StudyListItem> _studies = [];
    private readonly ObservableCollection<BackgroundJobRow> _backgroundJobs = [];
    private readonly ObservableCollection<PatientRow> _patients = [];
    private readonly ObservableCollection<SeriesGridRow> _seriesRows = [];
    private readonly ObservableCollection<FilesystemFolderNode> _filesystemRoots = [];
    private readonly ObservableCollection<RelayInboxItem> _relayInboxItems = [];
    private readonly ObservableCollection<ToastNotificationItem> _toastNotifications = [];
    private readonly IReadOnlyList<OnboardingStep> _onboardingSteps = CreateOnboardingSteps();
    private readonly string _browserLayoutSettingsPath;
    private string? _filesystemRootPath;
    private string? _lastScannedFolderPath;
    private bool _lastScanPreferDicomDir;
    private string? _lastStorageScpToastMessage;
    private bool _filesystemScanInProgress;
    private bool _relayInboxBusy;
    private bool _onboardingVisible;
    private int _networkInfoRefreshVersion;
    private int _databaseInfoRefreshVersion;
    private int _onboardingCompletedVersion;
    private int _onboardingDismissedVersion;
    private int _onboardingStepIndex;
    private double _patientPaneWidth = DefaultPatientPaneWidth;
    private double _seriesPaneHeight = DefaultSeriesPaneHeight;
    private int _viewerWindowCount = 1;
    private readonly List<StudyViewerWindow> _managedViewerWindows = [];

    public MainWindow(App app)
    {
        _app = app;
        _browserLayoutSettingsPath = Path.Combine(_app.Paths.ApplicationDirectory, "study-browser-layout.json");
        InitializeComponent();
        _app.WindowPlacementService.Register(this, "StudyBrowserWindow");
        LoadBrowserLayoutSettings();

        PatientGrid.ItemsSource = _patients;
        StudyGrid.ItemsSource = _studies;
        SeriesGrid.ItemsSource = _seriesRows;
        BackgroundJobsGrid.ItemsSource = _backgroundJobs;
        FilesystemTreeView.ItemsSource = _filesystemRoots;
        RelayInboxGrid.ItemsSource = _relayInboxItems;
        ToastItemsControl.ItemsSource = _toastNotifications;
        _uiReady = true;
        _app.BackgroundJobs.JobsChanged += OnBackgroundJobsChanged;
        _app.StorageScpService.StatusChanged += OnStorageScpStatusChanged;
        _app.NetworkSettingsService.SettingsChanged += OnNetworkSettingsChanged;
        Closed += OnMainWindowClosed;
        Opened += async (_, _) => await InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        BrowserModeTabs.SelectedIndex = GetBrowserModeTabIndex(_browserMode);
        ViewerWindowCountComboBox.SelectedIndex = Math.Clamp(_viewerWindowCount, 1, 4) - 1;
        _ = RefreshNetworkInfoPanelAsync();
        _ = RefreshDatabaseInfoPanelAsync();
        UpdateModeUi();

        if (_browserMode == BrowserMode.Filesystem)
        {
            await EnsureFilesystemRootLoadedAsync();
        }

        RefreshBackgroundJobsPanel();
        await RefreshCurrentModeAsync();
        await MaybeStartOnboardingAsync();
    }

    private async Task RefreshCurrentModeAsync(string? statusOverride = null, bool applySearchFilters = true, bool userInitiated = false)
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
                await LoadNetworkStudiesAsync(statusOverride, userInitiated);
                break;
            case BrowserMode.Email:
                ClearStudyResults("Relay inbox", statusOverride ?? "Relay inbox ready.");
                await RefreshRelayInboxAsync(statusOverride, showToast: false);
                break;
        }
    }

    private async Task MaybeStartOnboardingAsync()
    {
        if (_onboardingCompletedVersion >= CurrentOnboardingVersion || _onboardingDismissedVersion >= CurrentOnboardingVersion)
        {
            return;
        }

        await Task.Delay(250);
        await ShowOnboardingAsync(0, showToast: true);
    }

    private async Task SetBrowserModeAsync(BrowserMode mode)
    {
        int targetIndex = GetBrowserModeTabIndex(mode);
        if (BrowserModeTabs is not null && BrowserModeTabs.SelectedIndex != targetIndex)
        {
            BrowserModeTabs.SelectedIndex = targetIndex;
            await Task.Yield();
            return;
        }

        _browserMode = mode;
        SaveBrowserLayoutSettings();
        UpdateModeUi();

        if (_browserMode == BrowserMode.Filesystem && _filesystemRoots.Count == 0)
        {
            await EnsureFilesystemRootLoadedAsync();
        }

        await RefreshCurrentModeAsync();
    }

    private async Task ShowOnboardingAsync(int stepIndex, bool showToast)
    {
        if (_onboardingSteps.Count == 0
            || OnboardingOverlay is null
            || OnboardingStepText is null
            || OnboardingModeBadge is null
            || OnboardingModeBadgeText is null
            || OnboardingTitleText is null
            || OnboardingSummaryText is null
            || OnboardingTipsText is null
            || OnboardingHintText is null
            || OnboardingPrimaryActionButton is null
            || OnboardingPreviousButton is null
            || OnboardingNextButton is null)
        {
            return;
        }

        _onboardingStepIndex = Math.Clamp(stepIndex, 0, _onboardingSteps.Count - 1);
        OnboardingStep step = _onboardingSteps[_onboardingStepIndex];

        if (step.Mode is BrowserMode mode)
        {
            await SetBrowserModeAsync(mode);
        }

        _onboardingVisible = true;
        OnboardingOverlay.IsVisible = true;
        OnboardingStepText.Text = $"Step {_onboardingStepIndex + 1} of {_onboardingSteps.Count}";
        OnboardingModeBadge.IsVisible = !string.IsNullOrWhiteSpace(step.Badge);
        OnboardingModeBadgeText.Text = step.Badge;
        OnboardingTitleText.Text = step.Title;
        OnboardingSummaryText.Text = step.Summary;
        OnboardingTipsText.Text = string.Join(Environment.NewLine, step.Bullets.Select(bullet => $"• {bullet}"));
        OnboardingHintText.Text = step.Hint;
        OnboardingPrimaryActionButton.IsVisible = step.PrimaryAction != OnboardingPrimaryAction.None;
        OnboardingPrimaryActionButton.Content = step.PrimaryActionLabel ?? "Try it";
        OnboardingPreviousButton.IsEnabled = _onboardingStepIndex > 0;
        OnboardingNextButton.Content = _onboardingStepIndex == _onboardingSteps.Count - 1 ? "Finish" : "Next";

        SetStatus(step.StatusText);
        if (showToast && !string.IsNullOrWhiteSpace(step.ToastMessage))
        {
            ShowToast(step.ToastMessage, ToastSeverity.Info, TimeSpan.FromSeconds(7));
        }
    }

    private void HideOnboarding(bool completed)
    {
        _onboardingVisible = false;
        if (OnboardingOverlay is not null)
        {
            OnboardingOverlay.IsVisible = false;
        }

        if (completed)
        {
            _onboardingCompletedVersion = CurrentOnboardingVersion;
            SetStatus("Onboarding finished. Use Guide any time to replay the tour.");
            ShowToast("Onboarding finished. Use Guide any time to replay the tour.", ToastSeverity.Success, TimeSpan.FromSeconds(5));
        }
        else
        {
            _onboardingDismissedVersion = CurrentOnboardingVersion;
            SetStatus("Onboarding skipped for now. Use Guide any time to reopen it.");
        }

        SaveBrowserLayoutSettings();
    }

    private async Task RunOnboardingPrimaryActionAsync()
    {
        OnboardingStep step = _onboardingSteps[_onboardingStepIndex];
        switch (step.PrimaryAction)
        {
            case OnboardingPrimaryAction.OpenNetworkConfiguration:
                await OpenNetworkConfigurationAsync(allowModeSwitch: true);
                break;

            case OnboardingPrimaryAction.ChooseFilesystemRoot:
                await BrowseFilesystemRootAsync(allowModeSwitch: true);
                break;

            case OnboardingPrimaryAction.OpenSelectedStudy:
                await SetBrowserModeAsync(BrowserMode.Database);
                if (GetSelectedStudies().Count != 1)
                {
                    SetStatus("Select one study in Database mode, then use View or double-click it.");
                    ShowToast("Select one study in Database mode, then use View or double-click it.", ToastSeverity.Warning, TimeSpan.FromSeconds(6));
                    return;
                }

                await OpenSelectedStudyAsync();
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
            : $"Loaded {_allStudies.Count} studies from the K-PACS imagebox and filesystem index.");
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
            ? _filesystemScanInProgress ? "Filesystem scan in progress..." : "No filesystem scan loaded."
            : _filesystemScanInProgress
                ? $"Filesystem scan in progress — {_filesystemPreviewDetails.Count} studies found so far."
                : $"{_filesystemPreviewDetails.Count} studies available from the last filesystem scan.";

        StatusText.Text = statusOverride ?? (_filesystemPreviewDetails.Count == 0
            ? "Expand Computer, choose a drive or folder, then right-click it and select Scan folder."
            : applySearchFilters
                ? "Filesystem scan loaded. Studies open immediately and are copied into the local imagebox in the background."
                : "Filesystem scan loaded. Fresh scan results are shown without applying the search filter yet.");
    }

    private async Task LoadNetworkStudiesAsync(string? statusOverride, bool userInitiated)
    {
        StudyQuery query = BuildQuery();
        if (IsEmptyNetworkQuery(query))
        {
            _networkSearchResults.Clear();
            _networkPreviewDetails.Clear();
            _networkSeriesPreviews.Clear();
            _allStudies = [];
            _studies.Clear();
            _patients.Clear();
            _seriesRows.Clear();
            PatientGrid.SelectedItem = null;
            StudyGrid.SelectedItem = null;

            RemoteArchiveEndpoint? configuredArchive = _app.NetworkSettingsService.CurrentSettings.GetSelectedArchive();
            DatabaseStatsText.Text = configuredArchive is null
                ? "No remote archive configured."
                : $"Remote archive {configuredArchive.Name} is configured. Enter at least one filter before searching.";
            StudySummaryText.Text = "Enter at least one search criterion before querying the remote archive.";
            UpdateNetworkSetupSummary();
            SetStatus(statusOverride ?? "Network search is idle. Enter at least one filter before querying the remote archive.");

            if (userInitiated)
            {
                ShowToast("Remote query blocked: enter at least one filter before searching the archive.", ToastSeverity.Warning, TimeSpan.FromSeconds(7));
            }

            return;
        }

        RemoteArchiveEndpoint? archive = _app.NetworkSettingsService.CurrentSettings.GetSelectedArchive();
        SetStatus(statusOverride ?? (archive is null
            ? "No remote archive configured."
            : $"Searching remote archive {archive.Name}..."));
        if (archive is not null)
        {
            ShowToast($"Searching remote archive {archive.Name}.", ToastSeverity.Info);
        }

        try
        {
            DicomCommunicationTrace.Log("SEARCH", $"UI triggered network search. userInitiated={userInitiated}, archive={(archive is null ? "<none>" : archive.Name)}.");
            List<RemoteStudySearchResult> results = await _app.RemoteStudyBrowserService.SearchStudiesAsync(query);
            _networkSearchResults = results.ToDictionary(result => result.Study.StudyInstanceUid, StringComparer.Ordinal);

            HashSet<string> availableStudyUids = [.. _networkSearchResults.Keys];
            _networkPreviewDetails = _networkPreviewDetails
                .Where(pair => availableStudyUids.Contains(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            _networkSeriesPreviews = _networkSeriesPreviews
                .Where(pair => availableStudyUids.Contains(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

            _allStudies = results.Select(result => result.Study).ToList();

            BuildPatientRows();
            ApplyPatientFilter();

            DatabaseStatsText.Text = archive is null
                ? "No remote archive configured."
                : $"{_allStudies.Count} remote studies from {archive.Name}. Storage SCP: {_app.StorageScpService.LastStatus}";
            UpdateNetworkSetupSummary();
            SetStatus(statusOverride ?? (_allStudies.Count == 0
                ? "No remote studies matched the current query."
                : $"Loaded {_allStudies.Count} studies from remote archive {archive?.Name}."));
            ShowToast(_allStudies.Count == 0
                ? $"No remote studies matched on {archive?.Name ?? "the configured archive"}."
                : $"Found {_allStudies.Count} remote studies on {archive?.Name ?? "the archive"}.", _allStudies.Count == 0 ? ToastSeverity.Warning : ToastSeverity.Success);
        }
        catch (Exception ex)
        {
            DicomCommunicationTrace.LogException("SEARCH", "UI network search failed", ex);
            _networkSearchResults.Clear();
            _networkPreviewDetails.Clear();
            _networkSeriesPreviews.Clear();
            ClearStudyResults("Network mode", $"Remote query failed: {ex.Message}");
            ShowToast($"Remote query failed: {ex.Message}", ToastSeverity.Error, TimeSpan.FromSeconds(8));
        }
    }

    private void ClearStudyResults(string statsText, string statusText)
    {
        _allStudies = [];
        _networkSearchResults.Clear();
        _networkPreviewDetails.Clear();
        _networkSeriesPreviews.Clear();
        _studies.Clear();
        _patients.Clear();
        _seriesRows.Clear();
        PatientGrid.SelectedItem = null;
        StudyGrid.SelectedItem = null;
        DatabaseStatsText.Text = statsText;
        StudySummaryText.Text = "No studies available in the current mode.";
        SetStatus(statusText);
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
        if (_browserMode is BrowserMode.Filesystem or BrowserMode.Email || !_showPatientPanel)
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
        string? selectedKey = ((_browserMode == BrowserMode.Database) || (_browserMode == BrowserMode.Network)) && _showPatientPanel
            ? (PatientGrid.SelectedItem as PatientRow)?.SelectionKey
            : null;

        List<StudyListItem> visibleStudies = string.IsNullOrWhiteSpace(selectedKey)
            ? _allStudies
            : _allStudies.Where(study => $"{study.PatientId}\u001F{study.PatientName}" == selectedKey).ToList();

        List<string> previousSelectionIds = GetSelectedStudies().Select(study => study.SelectionId).ToList();

        _studies.Clear();
        foreach (StudyListItem study in visibleStudies.OrderByDescending(item => item.StudyDate).ThenBy(item => item.PatientName))
        {
            _studies.Add(study);
        }

        bool autoSelectFirstStudy = !(_browserMode == BrowserMode.Filesystem && _filesystemScanInProgress);
        RestoreStudySelection(previousSelectionIds, autoSelectFirstStudy);

        StudySummaryText.Text = _studies.Count == 0
            ? "No studies match the current selection."
            : _browserMode == BrowserMode.Filesystem
                ? $"{_studies.Count} filesystem studies match the current filter. Double-click to open immediately while the local copy continues in the background."
                : _browserMode == BrowserMode.Network
                    ? _showPatientPanel
                        ? $"{_studies.Count} remote studies for the selected patient. Double-click to retrieve and open."
                        : $"{_studies.Count} remote studies match the current filter. Double-click to retrieve and open."
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
            BrowserMode.Network => await EnsureNetworkPreviewLoadedAsync(selectedStudy),
            _ => null,
        };
    }

    private async Task LoadSelectedStudyDetailsAsync()
    {
        List<StudyListItem> selectedStudies = GetSelectedStudies();
        if (selectedStudies.Count == 0)
        {
            _seriesRows.Clear();
            StudySummaryText.Text = "Select a study to see its series overview.";
            UpdateStudyActionAvailability();
            return;
        }

        if (selectedStudies.Count > 1)
        {
            _seriesRows.Clear();
            int totalSeries = selectedStudies.Sum(study => study.SeriesCount);
            int totalImages = selectedStudies.Sum(study => study.InstanceCount);
            StudySummaryText.Text = $"{selectedStudies.Count} studies selected   {totalSeries} series / {totalImages} images   [multi-select]";
            UpdateStudyActionAvailability();
            return;
        }

        StudyListItem selectedStudy = selectedStudies[0];

        StudyDetails? details = await LoadStudyDetailsForSelectionAsync(selectedStudy);
        if (details is null)
        {
            _seriesRows.Clear();
            StudySummaryText.Text = "Selected study could not be loaded.";
            UpdateStudyActionAvailability();
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
                InstanceCount = Math.Max(series.InstanceCount, series.Instances.Count),
                FirstFileName = series.Instances.Count == 0 ? string.Empty : Path.GetFileName(series.Instances[0].FilePath),
            });
        }

        string modeSuffix = _browserMode switch
        {
            BrowserMode.Filesystem => "preview",
            BrowserMode.Network => "remote",
            _ => "local",
        };
        StudySummaryText.Text = $"{selectedStudy.PatientName}   {selectedStudy.DisplayPatientBirthDate}   {selectedStudy.DisplayStudyDate}   {selectedStudy.Modalities}   {details.Series.Count} series / {selectedStudy.InstanceCount} images   [{modeSuffix}]";
        UpdateStudyActionAvailability();
    }

    private async Task OpenSelectedStudyAsync()
    {
        List<StudyListItem> selectedStudies = GetSelectedStudies();
        if (selectedStudies.Count == 0)
        {
            SetStatus("Select a study first.");
            ShowToast("Select a study first.", ToastSeverity.Warning);
            return;
        }

        if (selectedStudies.Count > 1)
        {
            SetStatus("Viewer open is only available for a single selected study.");
            ShowToast("Viewer open is only available for a single selected study.", ToastSeverity.Warning);
            return;
        }

        StudyListItem selectedStudy = selectedStudies[0];
        CloseManagedViewerWindows();

        RemoteStudyRetrievalSession? retrievalSession = null;
        StudyDetails? details = _browserMode switch
        {
            BrowserMode.Database => await _app.Repository.GetStudyDetailsAsync(selectedStudy.StudyKey),
            BrowserMode.Filesystem => _filesystemPreviewDetails.GetValueOrDefault(selectedStudy.StudyInstanceUid),
            BrowserMode.Network => await RetrieveNetworkStudyAsync(selectedStudy),
            BrowserMode.Email => null,
            _ => null,
        };

        if (details is null)
        {
            if (_browserMode == BrowserMode.Network)
            {
                StatusText.Text = "Remote study retrieval did not provide viewable data yet.";
                ShowToast("Remote study retrieval did not provide viewable data yet.", ToastSeverity.Warning);
            }
            else if (_browserMode == BrowserMode.Email)
            {
                SetStatus("This browser mode does not provide studies yet.");
            }
            else if (_browserMode == BrowserMode.Database)
            {
                SetStatus("Selected study could not be loaded from SQLite.");
                ShowToast("Selected study could not be loaded from SQLite.", ToastSeverity.Error);
            }
            return;
        }

        if ((_browserMode == BrowserMode.Database || _browserMode == BrowserMode.Filesystem)
            && details.Study.Availability != StudyAvailability.Imported)
        {
            bool queued = await _app.ImportService.QueueStudyImportAsync(details);
            if (queued)
            {
                string queueMessage = $"Opening {selectedStudy.PatientName}. Files are being copied into the local imagebox in the background.";
                SetStatus(queueMessage);
                ShowToast(queueMessage, ToastSeverity.Info, TimeSpan.FromSeconds(6));
            }
        }

        if (_browserMode == BrowserMode.Network)
        {
            if (!_networkSearchResults.TryGetValue(selectedStudy.StudyInstanceUid, out RemoteStudySearchResult? remoteStudy))
            {
                SetStatus("Remote study metadata is no longer available. Run the query again.");
                ShowToast("Remote study metadata is no longer available. Run the query again.", ToastSeverity.Warning);
                return;
            }

            if (!_networkSeriesPreviews.TryGetValue(selectedStudy.StudyInstanceUid, out List<RemoteSeriesPreview>? seriesPreviews))
            {
                await EnsureNetworkPreviewLoadedAsync(selectedStudy);
                _networkSeriesPreviews.TryGetValue(selectedStudy.StudyInstanceUid, out seriesPreviews);
            }

            retrievalSession = await _app.RemoteStudyBrowserService.CreateRetrievalSessionAsync(
                remoteStudy,
                details,
                seriesPreviews ?? [],
                CancellationToken.None);
            retrievalSession.StatusChanged += OnRemoteRetrievalStatusChanged;
            SetStatus($"Opening remote study {selectedStudy.PatientName}. Thumbnails load first, then priors, then the remaining series.");
            ShowToast($"Opening remote study {selectedStudy.PatientName}. Series thumbnails load first while prior lookup starts in the viewer.", ToastSeverity.Info, TimeSpan.FromSeconds(6));

            details = retrievalSession.StudyDetails;
            _networkPreviewDetails[selectedStudy.StudyInstanceUid] = details;
            selectedStudy.SeriesCount = details.Series.Count;
            selectedStudy.InstanceCount = details.Series.Sum(series => Math.Max(series.InstanceCount, series.Instances.Count));
        }

        PriorStudyLookupMode priorLookupMode = _browserMode == BrowserMode.Network
            ? PriorStudyLookupMode.RemoteArchive
            : PriorStudyLookupMode.LocalRepository;

        IReadOnlyList<PriorStudySummary> priorStudies = await _app.PriorStudyLookupService.FindPriorStudiesAsync(details.Study, priorLookupMode, CancellationToken.None);
        OpenStudyInViewerWindows(details, retrievalSession, priorLookupMode, priorStudies);
    }

    private async Task<StudyDetails?> EnsureNetworkPreviewLoadedAsync(StudyListItem selectedStudy)
    {
        if (_networkPreviewDetails.TryGetValue(selectedStudy.StudyInstanceUid, out StudyDetails? cachedDetails))
        {
            return cachedDetails;
        }

        if (!_networkSearchResults.TryGetValue(selectedStudy.StudyInstanceUid, out RemoteStudySearchResult? remoteStudy))
        {
            return null;
        }

        try
        {
            SetStatus($"Loading remote series for {selectedStudy.PatientName}...");
            ShowToast($"Loading remote series preview for {selectedStudy.PatientName}.", ToastSeverity.Info);
            (StudyDetails details, List<RemoteSeriesPreview> seriesPreviews) = await _app.RemoteStudyBrowserService.LoadStudyPreviewAsync(remoteStudy);
            _networkPreviewDetails[selectedStudy.StudyInstanceUid] = details;
            _networkSeriesPreviews[selectedStudy.StudyInstanceUid] = seriesPreviews;
            selectedStudy.SeriesCount = details.Series.Count;
            selectedStudy.InstanceCount = details.Series.Sum(series => Math.Max(series.InstanceCount, series.Instances.Count));
            SetStatus($"Loaded remote series preview for {selectedStudy.PatientName}.");
            ShowToast($"Loaded {details.Series.Count} remote series for {selectedStudy.PatientName}.", ToastSeverity.Success);
            return details;
        }
        catch (Exception ex)
        {
            SetStatus($"Remote series preview failed: {ex.Message}");
            ShowToast($"Remote series preview failed: {ex.Message}", ToastSeverity.Error, TimeSpan.FromSeconds(8));
            return null;
        }
    }

    private async Task<StudyDetails?> RetrieveNetworkStudyAsync(StudyListItem selectedStudy)
    {
        if (!_networkSearchResults.TryGetValue(selectedStudy.StudyInstanceUid, out RemoteStudySearchResult? remoteStudy))
        {
            SetStatus("Remote study metadata is no longer available. Run the query again.");
            ShowToast("Remote study metadata is no longer available. Run the query again.", ToastSeverity.Warning);
            return null;
        }

        if (!_networkSeriesPreviews.TryGetValue(selectedStudy.StudyInstanceUid, out List<RemoteSeriesPreview>? seriesPreviews))
        {
            await EnsureNetworkPreviewLoadedAsync(selectedStudy);
            _networkSeriesPreviews.TryGetValue(selectedStudy.StudyInstanceUid, out seriesPreviews);
        }

        StudyDetails? details = _networkPreviewDetails.GetValueOrDefault(selectedStudy.StudyInstanceUid);
        if (details is not null)
        {
            _networkPreviewDetails[selectedStudy.StudyInstanceUid] = details;
            selectedStudy.SeriesCount = details.Series.Count;
            selectedStudy.InstanceCount = details.Series.Sum(series => Math.Max(series.InstanceCount, series.Instances.Count));
        }

        return details;
    }

    private async Task<StudyDetails?> ImportPreviewStudyAsync(StudyListItem selectedStudy)
    {
        if (!_filesystemPreviewDetails.TryGetValue(selectedStudy.StudyInstanceUid, out StudyDetails? previewDetails))
        {
            SetStatus("Preview study data is no longer available. Please scan the folder again.");
            ShowToast("Preview study data is no longer available. Please scan the folder again.", ToastSeverity.Warning);
            return null;
        }

        bool queued = await _app.ImportService.QueueStudyImportAsync(previewDetails);
        if (!queued)
        {
            if (previewDetails.Study.Availability == StudyAvailability.Imported)
            {
                SetStatus($"Study {selectedStudy.PatientName} is already available in the local imagebox.");
                ShowToast($"Study {selectedStudy.PatientName} is already available in the local imagebox.", ToastSeverity.Info, TimeSpan.FromSeconds(5));
                return previewDetails;
            }

            SetStatus($"Study {selectedStudy.PatientName} is already queued for background import.");
            ShowToast($"Study {selectedStudy.PatientName} is already queued for background import.", ToastSeverity.Info, TimeSpan.FromSeconds(5));
            return previewDetails;
        }

        SetStatus($"Study {selectedStudy.PatientName} is being copied into the local imagebox in the background.");
        ShowToast($"Study {selectedStudy.PatientName} is being copied into the local imagebox in the background.", ToastSeverity.Info, TimeSpan.FromSeconds(6));
        return previewDetails;
    }

    private async void OnSearchClick(object? sender, RoutedEventArgs e) => await RefreshCurrentModeAsync(userInitiated: true);

    private async Task OpenNetworkConfigurationAsync(bool allowModeSwitch)
    {
        if (_browserMode != BrowserMode.Network)
        {
            if (!allowModeSwitch)
            {
                StatusText.Text = "Configuration is currently only implemented for Network mode.";
                return;
            }

            await SetBrowserModeAsync(BrowserMode.Network);
        }

        var window = new NetworkSettingsWindow(_app.NetworkSettingsService.CurrentSettings);
        DicomNetworkSettings? updatedSettings = await window.ShowDialog<DicomNetworkSettings?>(this);
        if (updatedSettings is null)
        {
            return;
        }

        await _app.NetworkSettingsService.SaveAsync(updatedSettings);
        _ = RefreshNetworkInfoPanelAsync();
        await RefreshCurrentModeAsync($"Saved network configuration. Storage SCP restarted on port {updatedSettings.LocalPort}. DICOM trace logging {(updatedSettings.EnableDicomCommunicationLogging ? "enabled" : "disabled")}." );
    }

    private async void OnConfigClick(object? sender, RoutedEventArgs e) => await OpenNetworkConfigurationAsync(allowModeSwitch: false);

    private async void OnInfoClick(object? sender, RoutedEventArgs e)
    {
        if (_browserMode == BrowserMode.Database)
        {
            bool dbExists = File.Exists(_app.Paths.DatabasePath);
            long dbSize = dbExists ? new FileInfo(_app.Paths.DatabasePath).Length : 0;
            int studyCount = 0;
            try
            {
                studyCount = (await _app.Repository.SearchStudiesAsync(new StudyQuery())).Count;
            }
            catch
            {
                studyCount = 0;
            }

            DriveHealth driveHealth = GetDriveHealth(_app.Paths.DatabasePath, _app.NetworkSettingsService.CurrentSettings.InboxDirectory);
            string databaseInfo = $"Database file: {_app.Paths.DatabasePath}\n"
                + $"Present: {(dbExists ? "Yes" : "No")}\n"
                + $"Size: {(dbExists ? FormatFileSize(dbSize) : "-")}\n"
                + $"Indexed studies: {studyCount}\n\n"
                + $"Drive health: {driveHealth.Label}\n"
                + $"{driveHealth.PrimaryText}\n"
                + $"{driveHealth.SecondaryText}";

            await new NetworkInfoWindow("Database Information", databaseInfo).ShowDialog(this);
            return;
        }

        if (_browserMode == BrowserMode.Filesystem)
        {
            IReadOnlyList<BackgroundJobInfo> importJobs = _app.BackgroundJobs.GetJobsSnapshot()
                .Where(job => job.JobType == BackgroundJobType.Import)
                .ToList();
            int activeImports = importJobs.Count(job => job.State is BackgroundJobState.Queued or BackgroundJobState.Running);

            string filesystemInfo = $"Last scanned folder: {_lastScannedFolderPath ?? "<none>"}\n"
                + $"Scan in progress: {(_filesystemScanInProgress ? "Yes" : "No")}\n"
                + $"Preview studies loaded: {_filesystemPreviewDetails.Count}\n"
                + $"Import jobs tracked: {importJobs.Count}\n"
                + $"Active import jobs: {activeImports}\n"
                + $"Last scan mode: {(_lastScanPreferDicomDir ? "DICOMDIR preferred" : "Recursive file scan")}";

            await new NetworkInfoWindow("Filesystem Information", filesystemInfo).ShowDialog(this);
            return;
        }

        if (_browserMode != BrowserMode.Network)
        {
            StatusText.Text = "No additional information is available for this mode yet.";
            return;
        }

        DicomNetworkSettings settings = _app.NetworkSettingsService.CurrentSettings;
        RemoteArchiveEndpoint? archive = settings.GetSelectedArchive();
        string info = $"Local AE: {settings.LocalAeTitle}\n"
            + $"Local port: {settings.LocalPort}\n"
            + $"Inbox: {settings.InboxDirectory}\n"
            + $"DICOM trace logging: {(settings.EnableDicomCommunicationLogging ? "Enabled" : "Disabled")}\n"
            + $"Trace log file: {settings.DicomCommunicationLogPath}\n"
            + $"Storage SCP: {_app.StorageScpService.LastStatus}\n\n"
            + (archive is null
                ? "No remote archive configured."
                : $"Archive: {archive.Name}\nHost: {archive.Host}\nPort: {archive.Port}\nRemote AE: {archive.RemoteAeTitle}");

        var infoWindow = new NetworkInfoWindow("Network Information", info);
        await infoWindow.ShowDialog(this);
    }

    private async void OnTogglePatientPanelClick(object? sender, RoutedEventArgs e)
    {
        if (_showPatientPanel)
        {
            CapturePatientPaneWidth();
        }

        _showPatientPanel = !_showPatientPanel;
        SaveBrowserLayoutSettings();
        UpdateModeUi();
        await RefreshCurrentModeAsync(userInitiated: true);
    }

    private async void OnGuideClick(object? sender, RoutedEventArgs e)
    {
        _onboardingDismissedVersion = 0;
        await ShowOnboardingAsync(0, showToast: true);
    }

    private void OnOnboardingSkipClick(object? sender, RoutedEventArgs e) => HideOnboarding(completed: false);

    private async void OnOnboardingPreviousClick(object? sender, RoutedEventArgs e)
    {
        if (_onboardingStepIndex == 0)
        {
            return;
        }

        await ShowOnboardingAsync(_onboardingStepIndex - 1, showToast: false);
    }

    private async void OnOnboardingNextClick(object? sender, RoutedEventArgs e)
    {
        if (_onboardingStepIndex >= _onboardingSteps.Count - 1)
        {
            HideOnboarding(completed: true);
            return;
        }

        await ShowOnboardingAsync(_onboardingStepIndex + 1, showToast: true);
    }

    private async void OnOnboardingPrimaryActionClick(object? sender, RoutedEventArgs e) => await RunOnboardingPrimaryActionAsync();

    private void OnOnboardingToastClick(object? sender, RoutedEventArgs e)
    {
        if (!_onboardingVisible || _onboardingStepIndex < 0 || _onboardingStepIndex >= _onboardingSteps.Count)
        {
            return;
        }

        string toastMessage = _onboardingSteps[_onboardingStepIndex].ToastMessage;
        if (!string.IsNullOrWhiteSpace(toastMessage))
        {
            ShowToast(toastMessage, ToastSeverity.Info, TimeSpan.FromSeconds(7));
        }
    }

    private async void OnTodayClick(object? sender, RoutedEventArgs e)
    {
        string today = DateTime.Now.ToString("dd.MM.yyyy");
        FromDateBox.Text = today;
        ToDateBox.Text = today;
        await RefreshCurrentModeAsync(userInitiated: true);
    }

    private async void OnYesterdayClick(object? sender, RoutedEventArgs e)
    {
        string yesterday = DateTime.Now.Date.AddDays(-1).ToString("dd.MM.yyyy");
        FromDateBox.Text = yesterday;
        ToDateBox.Text = yesterday;
        await RefreshCurrentModeAsync(userInitiated: true);
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
        await RefreshCurrentModeAsync(userInitiated: true);
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

    private async void OnSendSelectedClick(object? sender, RoutedEventArgs e)
    {
        if (_browserMode is not (BrowserMode.Database or BrowserMode.Filesystem))
        {
            SetStatus("Send is available for local studies in Database or Filesystem mode.");
            ShowToast("Send is available for local studies in Database or Filesystem mode.", ToastSeverity.Warning);
            return;
        }

        if (_browserMode == BrowserMode.Filesystem && _filesystemScanInProgress)
        {
            SetStatus("Wait until the filesystem scan finishes before sending studies.");
            ShowToast("Wait until the filesystem scan finishes before sending studies.", ToastSeverity.Warning);
            return;
        }

        List<StudyListItem> selectedStudies = GetSelectedStudies();
        if (selectedStudies.Count == 0)
        {
            SetStatus("Select at least one local study first.");
            ShowToast("Select at least one local study first.", ToastSeverity.Warning);
            return;
        }

        RemoteArchiveEndpoint? archive = _app.NetworkSettingsService.CurrentSettings.GetSelectedArchive();
        if (archive is null)
        {
            SetStatus("No remote archive configured. Open Network configuration first.");
            ShowToast("No remote archive configured. Open Network configuration first.", ToastSeverity.Warning, TimeSpan.FromSeconds(7));
            return;
        }

        try
        {
            var studiesToSend = new List<(StudyListItem Study, StudyDetails Details, int LocalFiles)>();
            foreach (StudyListItem selectedStudy in selectedStudies)
            {
                StudyDetails? studyDetails = _browserMode switch
                {
                    BrowserMode.Database => await _app.Repository.GetStudyDetailsAsync(selectedStudy.StudyKey),
                    BrowserMode.Filesystem => _filesystemPreviewDetails.GetValueOrDefault(selectedStudy.StudyInstanceUid),
                    _ => null,
                };
                if (studyDetails is null)
                {
                    continue;
                }

                int localFiles = studyDetails.Series.Sum(series => series.Instances.Count(instance => !string.IsNullOrWhiteSpace(instance.FilePath) && File.Exists(instance.FilePath)));
                if (localFiles > 0)
                {
                    studiesToSend.Add((selectedStudy, studyDetails, localFiles));
                }
            }

            if (studiesToSend.Count == 0)
            {
                SetStatus("None of the selected studies has local DICOM files to send.");
                ShowToast("None of the selected studies has local DICOM files to send.", ToastSeverity.Warning, TimeSpan.FromSeconds(7));
                return;
            }

            int totalFiles = studiesToSend.Sum(item => item.LocalFiles);
            string studyLabel = _browserMode == BrowserMode.Filesystem ? "preview" : "local";
            int queuedStudies = 0;

            foreach ((StudyListItem _, StudyDetails details, int _) in studiesToSend)
            {
                bool queued = await _app.RemoteStudyBrowserService.QueueSendStudyAsync(details, CancellationToken.None);
                if (queued)
                {
                    queuedStudies++;
                }
            }

            if (queuedStudies == 0)
            {
                SetStatus($"The selected {studyLabel} studies are already queued for sending to {archive.Name}.");
                ShowToast($"The selected {studyLabel} studies are already queued for sending to {archive.Name}.", ToastSeverity.Info, TimeSpan.FromSeconds(6));
                return;
            }

            string queueMessage = queuedStudies == 1
                ? $"Queued 1 {studyLabel} study for background send to {archive.Name} ({totalFiles} images)."
                : $"Queued {queuedStudies} {studyLabel} studies for background send to {archive.Name} ({totalFiles} images).";
            SetStatus(queueMessage);
            ShowToast(queueMessage, ToastSeverity.Info, TimeSpan.FromSeconds(6));
            RefreshBackgroundJobsPanel();
        }
        catch (Exception ex)
        {
            SetStatus($"Send failed: {ex.Message}");
            ShowToast($"Send failed: {ex.Message}", ToastSeverity.Error, TimeSpan.FromSeconds(8));
        }
    }

    private async void OnRelayShareClick(object? sender, RoutedEventArgs e)
    {
        if (_browserMode is not (BrowserMode.Database or BrowserMode.Filesystem))
        {
            SetStatus("Share is available for local studies in Database or Filesystem mode.");
            ShowToast("Share is available for local studies in Database or Filesystem mode.", ToastSeverity.Warning);
            return;
        }

        if (_browserMode == BrowserMode.Filesystem && _filesystemScanInProgress)
        {
            SetStatus("Wait until the filesystem scan finishes before sharing studies.");
            ShowToast("Wait until the filesystem scan finishes before sharing studies.", ToastSeverity.Warning);
            return;
        }

        List<StudyDetails> studiesToShare = [];
        foreach (StudyListItem selectedStudy in GetSelectedStudies())
        {
            StudyDetails? details = _browserMode switch
            {
                BrowserMode.Database => await _app.Repository.GetStudyDetailsAsync(selectedStudy.StudyKey),
                BrowserMode.Filesystem => _filesystemPreviewDetails.GetValueOrDefault(selectedStudy.StudyInstanceUid),
                _ => null,
            };

            if (details is not null && HasAnyLocalInstances(details))
            {
                studiesToShare.Add(details);
            }
        }

        if (studiesToShare.Count == 0)
        {
            SetStatus("Select at least one local study with local DICOM files before sharing.");
            ShowToast("Select at least one local study with local DICOM files before sharing.", ToastSeverity.Warning, TimeSpan.FromSeconds(7));
            return;
        }

        var window = new RelayShareWindow(_app.ShareRelayService, _app.ShareRelaySettingsService, studiesToShare);
        RelayShareResult? result = await window.ShowDialog<RelayShareResult?>(this);
        if (result is null)
        {
            return;
        }

        string shareMessage = $"Shared {result.StudyCount} study(s) to {result.RecipientCount} recipient(s) via relay as '{result.Subject}'. Package size: {FormatFileSize(result.PackageSizeBytes)}.";
        SetStatus(shareMessage);
        ShowToast(shareMessage, ToastSeverity.Info, TimeSpan.FromSeconds(8));
    }

    private async void OnRelaySetupClick(object? sender, RoutedEventArgs e)
    {
        var window = new RelaySettingsWindow(_app.ShareRelaySettingsService.CurrentSettings);
        ShareRelaySettings? settings = await window.ShowDialog<ShareRelaySettings?>(this);
        if (settings is null)
        {
            return;
        }

        await _app.ShareRelaySettingsService.SaveAsync(settings);
        await RefreshRelayInboxAsync("Relay settings saved.", showToast: true);
    }

    private async void OnRefreshRelayInboxClick(object? sender, RoutedEventArgs e) => await RefreshRelayInboxAsync("Relay inbox refreshed.", showToast: true);

    private async void OnRelayDownloadImportClick(object? sender, RoutedEventArgs e)
    {
        if (RelayInboxGrid.SelectedItem is not RelayInboxItem inboxItem)
        {
            SetStatus("Select a relay inbox item first.");
            ShowToast("Select a relay inbox item first.", ToastSeverity.Warning);
            return;
        }

        if (!inboxItem.PackageAvailable)
        {
            SetStatus("The selected relay item does not have a downloadable package yet.");
            ShowToast("The selected relay item does not have a downloadable package yet.", ToastSeverity.Warning);
            return;
        }

        try
        {
            _relayInboxBusy = true;
            UpdateRelayInboxActionAvailability();
            RelayInboxStatusText.Text = $"Downloading and importing '{inboxItem.Subject}'...";

            (ShareRelaySettings updatedSettings, RelayImportResult result) = await _app.ShareRelayService.DownloadAndImportShareAsync(
                _app.ShareRelaySettingsService.CurrentSettings,
                inboxItem,
                _app.ImportService,
                CancellationToken.None);

            await _app.ShareRelaySettingsService.SaveAsync(updatedSettings);
            await RefreshRelayInboxAsync($"Imported '{result.Subject}'.", showToast: false);
            await SetBrowserModeAsync(BrowserMode.Database);
            await RefreshCurrentModeAsync($"Imported '{result.Subject}' from relay ({result.ImportResult.ImportedStudies} studies, {result.ImportResult.ImportedInstances} instances).", userInitiated: true);

            string message = $"Imported '{result.Subject}' from relay ({result.ImportResult.ImportedStudies} studies, {result.ImportResult.ImportedInstances} instances).";
            SetStatus(message);
            ShowToast(message, ToastSeverity.Info, TimeSpan.FromSeconds(8));
        }
        catch (Exception ex)
        {
            SetStatus($"Relay import failed: {ex.Message}");
            ShowToast($"Relay import failed: {ex.Message}", ToastSeverity.Error, TimeSpan.FromSeconds(8));
        }
        finally
        {
            _relayInboxBusy = false;
            UpdateRelayInboxActionAvailability();
        }
    }

    private void OnRelayInboxSelectionChanged(object? sender, SelectionChangedEventArgs e) => UpdateRelayInboxActionAvailability();

    private async Task BrowseFilesystemRootAsync(bool allowModeSwitch)
    {
        if (_browserMode != BrowserMode.Filesystem)
        {
            if (!allowModeSwitch)
            {
                SetStatus("Browse is available in Filesystem mode.");
                return;
            }

            await SetBrowserModeAsync(BrowserMode.Filesystem);
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

    private async void OnBrowseFilesystemRootClick(object? sender, RoutedEventArgs e) => await BrowseFilesystemRootAsync(allowModeSwitch: false);

    private async Task LoadFilesystemRootAsync(string path)
    {
        _filesystemRootPath = path;
        SaveBrowserLayoutSettings();
        FilesystemRootText.Text = $"Root: {path}";
        FilesystemHintText.IsVisible = false;
        FilesystemTreeView.IsVisible = true;
        SetStatus($"Loading filesystem tree: {path}");
        ShowToast($"Loading filesystem tree for {path}.", ToastSeverity.Info);

        FilesystemFolderNode rootNode = await Task.Run(() => BuildFilesystemFolderNode(path));
        _filesystemRoots.Clear();
        _filesystemRoots.Add(rootNode);
        SetStatus($"Filesystem root loaded: {path}. Right-click a folder and select Scan folder.");
        ShowToast($"Filesystem root loaded. You can now scan folders under {path}.", ToastSeverity.Success);
    }

    private Task LoadComputerRootAsync()
    {
        _filesystemRootPath = null;
        SaveBrowserLayoutSettings();
        FilesystemRootText.Text = "Root: Computer";
        FilesystemHintText.IsVisible = false;
        FilesystemTreeView.IsVisible = true;

        _filesystemRoots.Clear();
        _filesystemRoots.Add(BuildComputerRootNode());
        SetStatus("Filesystem mode ready. Expand Computer to choose a drive or folder.");
        return Task.CompletedTask;
    }

    private async Task ScanFolderAsync(string folderPath, bool preferDicomDir)
    {
        SetStatus($"Scanning folder: {folderPath}");
        ShowToast(preferDicomDir
            ? $"Scanning {folderPath} using DICOMDIR references..."
            : $"Searching {folderPath} for DICOM files. This can take a while for large folders...", ToastSeverity.Info, TimeSpan.FromSeconds(6));

        BeginFilesystemScan(folderPath);

        try
        {
            var progress = new Progress<FilesystemScanProgress>(UpdateFilesystemScanProgress);
            FilesystemScanResult scanResult = await _app.FilesystemScanService.ScanPathAsync(folderPath, preferDicomDir, progress);
            List<StudyDetails> indexedStudies = await _app.ImportService.IndexFilesystemStudiesAsync(scanResult.Studies, folderPath);

            _lastScannedFolderPath = folderPath;
            _lastScanPreferDicomDir = preferDicomDir;
            _filesystemPreviewDetails = indexedStudies.ToDictionary(study => study.Study.StudyInstanceUid, StringComparer.Ordinal);
            _filesystemScannedStudies = indexedStudies.Select(study => study.Study).ToList();

            string summary = string.Join("  ", scanResult.Messages.Where(message => !string.IsNullOrWhiteSpace(message)));
            string statusMessage = string.IsNullOrWhiteSpace(summary)
                ? $"Scanned folder {folderPath}. {_filesystemPreviewDetails.Count} studies available and indexed in SQLite."
                : summary;

            FinishFilesystemScan(statusMessage);
            ShowToast(_filesystemPreviewDetails.Count == 0
                ? $"Scan finished for {folderPath}, but no DICOM studies were found."
                : $"Scan finished for {folderPath}. {_filesystemPreviewDetails.Count} studies are ready and indexed in SQLite.", _filesystemPreviewDetails.Count == 0 ? ToastSeverity.Warning : ToastSeverity.Success, TimeSpan.FromSeconds(6));

            await RefreshCurrentModeAsync(statusMessage, applySearchFilters: false);
        }
        catch (Exception ex)
        {
            _filesystemScanInProgress = false;
            if (FilesystemScanProgressPanel is not null)
            {
                FilesystemScanProgressPanel.IsVisible = false;
            }

            UpdateStudyActionAvailability();
            SetStatus($"Filesystem scan failed: {ex.Message}");
            ShowToast($"Filesystem scan failed: {ex.Message}", ToastSeverity.Error, TimeSpan.FromSeconds(8));
        }
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

        if (GetSelectedStudies().Count > 1)
        {
            SetStatus("Modify is only available for a single selected study.");
            ShowToast("Modify is only available for a single selected study.", ToastSeverity.Warning);
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
            await RefreshCurrentModeAsync(userInitiated: true);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Pseudonymize failed: {ex.Message}";
        }
    }

    private async void OnDeleteStudyClick(object? sender, RoutedEventArgs e) => await DeleteSelectedStudyAsync();

    private async void OnOpenViewerClick(object? sender, RoutedEventArgs e) => await OpenSelectedStudyAsync();
    private async void OnPatientDoubleTapped(object? sender, TappedEventArgs e) => await OpenSelectedPatientStudiesAsync();

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
            System.Collections.IList? selectedItems = StudyGrid.SelectedItems;
            if (selectedItems is null || !selectedItems.OfType<StudyListItem>().Any(item => item.SelectionId == study.SelectionId))
            {
                StudyGrid.SelectedItem = study;
            }
        }
    }

    private void OnStudyContextMenuOpened(object? sender, RoutedEventArgs e)
    {
        List<StudyListItem> selectedStudies = GetSelectedStudies();
        DeleteStudyMenuItem.IsEnabled = _browserMode == BrowserMode.Database && selectedStudies.Count > 0;
        DeleteStudyMenuItem.Header = selectedStudies.Count > 1 ? $"Delete {selectedStudies.Count} Studies" : "Delete Study";
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
            ShowToast("Select a folder first.", ToastSeverity.Warning);
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

        SaveBrowserLayoutSettings();

        if (PatientPanel is null
            || BrowserContentGrid is null
            || FilesystemPanel is null
            || ModePlaceholderPanel is null
            || HidePatientPanelButton is null
            || ShowPatientPanelButton is null
            || ViewActionButton is null
            || SendActionButton is null
            || PatientStudySplitter is null
            || StudySeriesSplitter is null
            || ModifyActionButton is null
            || ModePlaceholderText is null)
        {
            return;
        }

        UpdateModeUi();

        if (_browserMode == BrowserMode.Filesystem && _filesystemRoots.Count == 0)
        {
            await EnsureFilesystemRootLoadedAsync();
        }

        await RefreshCurrentModeAsync();
    }

    private void UpdateModeUi()
    {
        bool databaseMode = _browserMode == BrowserMode.Database;
        bool networkMode = _browserMode == BrowserMode.Network;
        bool filesystemMode = _browserMode == BrowserMode.Filesystem;
        bool placeholderMode = _browserMode == BrowserMode.Email;
        bool showPatientPanel = (databaseMode || networkMode) && _showPatientPanel;
        bool showSidePane = showPatientPanel || filesystemMode || placeholderMode;

        PatientPanel.IsVisible = showPatientPanel;
        FilesystemPanel.IsVisible = filesystemMode;
        ModePlaceholderPanel.IsVisible = placeholderMode;
        if (BrowserContentGrid.ColumnDefinitions.Count > 3)
        {
            BrowserContentGrid.ColumnDefinitions[1].Width = showSidePane
                ? new GridLength(Math.Clamp(_patientPaneWidth, MinPatientPaneWidth, MaxPatientPaneWidth), GridUnitType.Pixel)
                : new GridLength(0, GridUnitType.Pixel);
            BrowserContentGrid.ColumnDefinitions[2].Width = showPatientPanel
                ? new GridLength(6, GridUnitType.Pixel)
                : new GridLength(0, GridUnitType.Pixel);
        }
        if (BrowserContentGrid.RowDefinitions.Count > 2)
        {
            BrowserContentGrid.RowDefinitions[2].Height = new GridLength(Math.Clamp(_seriesPaneHeight, MinSeriesPaneHeight, MaxSeriesPaneHeight), GridUnitType.Pixel);
        }
        PatientStudySplitter.IsVisible = showPatientPanel;
        StudySeriesSplitter.IsVisible = true;
        HidePatientPanelButton.IsVisible = showPatientPanel;
        ShowPatientPanelButton.IsVisible = (databaseMode || networkMode) && !showPatientPanel;
        ConfigButton.IsEnabled = networkMode;
        InfoButton.IsEnabled = true;

        ModePlaceholderText.Text = _browserMode switch
        {
            BrowserMode.Email => "Use the Email tab above to refresh the relay inbox and download/import shared studies.",
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

        UpdateStudyActionAvailability();
        UpdateRelayInboxActionAvailability();
    }

    private async Task RefreshRelayInboxAsync(string? statusOverride, bool showToast)
    {
        if (RelayInboxSummaryText is null || RelayInboxStatusText is null)
        {
            return;
        }

        ShareRelaySettings settings = _app.ShareRelaySettingsService.CurrentSettings;
        settings.Normalize();
        RelayInboxSummaryText.Text = string.IsNullOrWhiteSpace(settings.UserEmail)
            ? "Relay is not set up yet. Click 'Set up relay' and enter the relay URL, API key, and your email."
            : $"Signed in as {settings.DisplayName} <{settings.UserEmail}> on device '{settings.DeviceName}'.";

        if (string.IsNullOrWhiteSpace(settings.BaseUrl) || string.IsNullOrWhiteSpace(settings.ApiKey) || string.IsNullOrWhiteSpace(settings.UserEmail) || string.IsNullOrWhiteSpace(settings.DisplayName))
        {
            _relayInboxItems.Clear();
            RelayInboxStatusText.Text = "Enter your relay details once, then press Refresh to load incoming studies.";
            UpdateRelayInboxActionAvailability();
            return;
        }

        try
        {
            _relayInboxBusy = true;
            UpdateRelayInboxActionAvailability();
            RelayInboxStatusText.Text = "Loading incoming studies...";

            (ShareRelaySettings updatedSettings, IReadOnlyList<RelayInboxItem> items) = await _app.ShareRelayService.GetInboxAsync(settings, CancellationToken.None);
            await _app.ShareRelaySettingsService.SaveAsync(updatedSettings, CancellationToken.None);

            _relayInboxItems.Clear();
            foreach (RelayInboxItem item in items)
            {
                _relayInboxItems.Add(item);
            }

            RelayInboxSummaryText.Text = $"Signed in as {updatedSettings.DisplayName} <{updatedSettings.UserEmail}> on device '{updatedSettings.DeviceName}'.";
            RelayInboxStatusText.Text = items.Count == 0
                ? "No incoming studies at the moment."
                : $"{items.Count} incoming share(s) ready. Select one and click 'Download + Import'.";

            if (showToast && !string.IsNullOrWhiteSpace(statusOverride))
            {
                ShowToast(statusOverride, ToastSeverity.Info, TimeSpan.FromSeconds(5));
            }
        }
        catch (Exception ex)
        {
            RelayInboxStatusText.Text = $"Inbox unavailable: {ex.Message}";
            if (showToast)
            {
                ShowToast($"Inbox unavailable: {ex.Message}", ToastSeverity.Warning, TimeSpan.FromSeconds(7));
            }
        }
        finally
        {
            _relayInboxBusy = false;
            UpdateRelayInboxActionAvailability();
        }
    }

    private void UpdateRelayInboxActionAvailability()
    {
        if (RelayInboxRefreshButton is null || RelayInboxDownloadButton is null || RelayInboxGrid is null)
        {
            return;
        }

        RelayInboxRefreshButton.IsEnabled = !_relayInboxBusy;
        RelayInboxDownloadButton.IsEnabled = !_relayInboxBusy
            && RelayInboxGrid.SelectedItem is RelayInboxItem selected
            && selected.PackageAvailable;
    }

    private void OnPatientSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if ((_browserMode == BrowserMode.Database || _browserMode == BrowserMode.Network) && _showPatientPanel)
        {
            ApplyPatientFilter();
        }
    }

    private async void OnStudySelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        StudyListItem? primaryStudy = GetPrimarySelectedStudy();
        if ((_browserMode == BrowserMode.Database || _browserMode == BrowserMode.Network) && _showPatientPanel && primaryStudy is not null)
        {
            PatientRow? row = _patients.FirstOrDefault(patient => patient.SelectionKey == $"{primaryStudy.PatientId}\u001F{primaryStudy.PatientName}");
            if (row is not null && !ReferenceEquals(PatientGrid.SelectedItem, row))
            {
                PatientGrid.SelectedItem = row;
            }
        }

        await LoadSelectedStudyDetailsAsync();
    }

    private void OnBackgroundJobsChanged()
    {
        Dispatcher.UIThread.Post(RefreshBackgroundJobsPanel);
    }

    private async void OnViewerWindowCountChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady || ViewerWindowCountComboBox.SelectedIndex < 0)
        {
            return;
        }

        _viewerWindowCount = Math.Clamp(ViewerWindowCountComboBox.SelectedIndex + 1, 1, 4);
        SaveBrowserLayoutSettings();

        if (_managedViewerWindows.Count == 0)
        {
            return;
        }

        await OpenSelectedStudyAsync();
    }

    private void OnStorageScpStatusChanged()
    {
        Dispatcher.UIThread.Post(() =>
        {
            UpdateNetworkSetupSummary();
            if (_browserMode == BrowserMode.Network)
            {
                DatabaseStatsText.Text = $"{_allStudies.Count} remote studies. Storage SCP: {_app.StorageScpService.LastStatus}";
            }

            MaybeToastStorageScpStatus(_app.StorageScpService.LastStatus);
        });
    }

    private void OnNetworkSettingsChanged(DicomNetworkSettings settings)
    {
        Dispatcher.UIThread.Post(UpdateNetworkSetupSummary);
    }

    private void OnRemoteRetrievalStatusChanged(string message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            SetStatus(message);

            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            string lower = message.ToLowerInvariant();
            if (lower.Contains("failed") || lower.Contains("error"))
            {
                ShowToast(message, ToastSeverity.Error, TimeSpan.FromSeconds(8));
            }
            else if (lower.Contains("completed") || lower.Contains("ready"))
            {
                ShowToast(message, ToastSeverity.Success, TimeSpan.FromSeconds(5));
            }
        });
    }

    private void OnMainWindowClosed(object? sender, EventArgs e)
    {
        CloseManagedViewerWindows();
        CapturePatientPaneWidth();
        CaptureSeriesPaneHeight();
        SaveBrowserLayoutSettings();
        _app.BackgroundJobs.JobsChanged -= OnBackgroundJobsChanged;
        _app.StorageScpService.StatusChanged -= OnStorageScpStatusChanged;
        _app.NetworkSettingsService.SettingsChanged -= OnNetworkSettingsChanged;
        Closed -= OnMainWindowClosed;
    }

    private void SetStatus(string message)
    {
        StatusText.Text = message;
    }

    private void MaybeToastStorageScpStatus(string message)
    {
        if (string.IsNullOrWhiteSpace(message) || string.Equals(message, _lastStorageScpToastMessage, StringComparison.Ordinal))
        {
            return;
        }

        _lastStorageScpToastMessage = message;
        string lower = message.ToLowerInvariant();
        if (lower.Contains("failed") || lower.Contains("exception") || lower.Contains("stopped"))
        {
            ShowToast(message, ToastSeverity.Error, TimeSpan.FromSeconds(8));
        }
        else if (lower.Contains("listening on port"))
        {
            ShowToast(message, ToastSeverity.Success, TimeSpan.FromSeconds(5));
        }
    }

    private void ShowToast(string message, ToastSeverity severity, TimeSpan? duration = null)
    {
        if (string.IsNullOrWhiteSpace(message) || ToastItemsControl is null)
        {
            return;
        }

        if (_toastNotifications.Any(toast => string.Equals(toast.Message, message, StringComparison.Ordinal)))
        {
            return;
        }

        ToastNotificationItem toast = CreateToast(message, severity);
        _toastNotifications.Add(toast);
        while (_toastNotifications.Count > 5)
        {
            _toastNotifications.RemoveAt(0);
        }

        _ = DismissToastAsync(toast.Id, duration ?? GetToastDuration(severity));
    }

    private async Task DismissToastAsync(Guid toastId, TimeSpan duration)
    {
        try
        {
            await Task.Delay(duration);
        }
        catch
        {
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            ToastNotificationItem? toast = _toastNotifications.FirstOrDefault(item => item.Id == toastId);
            if (toast is not null)
            {
                _toastNotifications.Remove(toast);
            }
        });
    }

    private static TimeSpan GetToastDuration(ToastSeverity severity) => severity switch
    {
        ToastSeverity.Success => TimeSpan.FromSeconds(4),
        ToastSeverity.Warning => TimeSpan.FromSeconds(6),
        ToastSeverity.Error => TimeSpan.FromSeconds(8),
        _ => TimeSpan.FromSeconds(4),
    };

    private static ToastNotificationItem CreateToast(string message, ToastSeverity severity)
    {
        return severity switch
        {
            ToastSeverity.Success => new ToastNotificationItem
            {
                Icon = "✓",
                Message = message,
                Background = new SolidColorBrush(Color.Parse("#F0174D28")),
                BorderBrush = new SolidColorBrush(Color.Parse("#FF2F8F3A")),
                Foreground = Brushes.White,
            },
            ToastSeverity.Warning => new ToastNotificationItem
            {
                Icon = "⚠",
                Message = message,
                Background = new SolidColorBrush(Color.Parse("#F07A4E00")),
                BorderBrush = new SolidColorBrush(Color.Parse("#FFFFB14A")),
                Foreground = Brushes.White,
            },
            ToastSeverity.Error => new ToastNotificationItem
            {
                Icon = "✕",
                Message = message,
                Background = new SolidColorBrush(Color.Parse("#F07D2222")),
                BorderBrush = new SolidColorBrush(Color.Parse("#FFFF7575")),
                Foreground = Brushes.White,
            },
            _ => new ToastNotificationItem
            {
                Icon = "ℹ",
                Message = message,
                Background = new SolidColorBrush(Color.Parse("#F0215D8B")),
                BorderBrush = new SolidColorBrush(Color.Parse("#FF65B7F7")),
                Foreground = Brushes.White,
            },
        };
    }

    private void UpdateNetworkSetupSummary()
    {
        RefreshBackgroundJobsPanel();
        _ = RefreshNetworkInfoPanelAsync();
        _ = RefreshDatabaseInfoPanelAsync();
    }

    private void RefreshBackgroundJobsPanel()
    {
        if (BackgroundJobsSummaryText is null || BackgroundJobsGrid is null)
        {
            return;
        }

        IReadOnlyList<BackgroundJobInfo> jobs = _app.BackgroundJobs.GetJobsSnapshot()
            .Where(job => job.JobType == BackgroundJobType.Import)
            .ToList();
        List<BackgroundJobInfo> recentJobs = jobs.Take(12).ToList();

        _backgroundJobs.Clear();
        foreach (BackgroundJobInfo job in recentJobs)
        {
            _backgroundJobs.Add(new BackgroundJobRow
            {
                JobId = job.JobId,
                TypeLabel = job.JobType == BackgroundJobType.Import ? "Import" : "Send",
                Title = string.IsNullOrWhiteSpace(job.Title) ? job.Key : job.Title,
                StateLabel = job.State.ToString(),
                ProgressLabel = job.TotalUnits > 0 ? $"{job.CompletedUnits}/{job.TotalUnits}" : "-",
                StatusText = job.StatusText,
            });
        }

        int activeJobs = jobs.Count(job => job.State is BackgroundJobState.Queued or BackgroundJobState.Running);
        int failedJobs = jobs.Count(job => job.State == BackgroundJobState.Failed);
        BackgroundJobsSummaryText.Text = jobs.Count == 0
            ? "No filesystem import jobs have been queued yet."
            : failedJobs > 0
                ? $"{activeJobs} active import jobs, {failedJobs} failed imports. Select a row to inspect its log."
                : $"{activeJobs} active import jobs. Select a row to inspect its log.";
    }

    private async void OnViewBackgroundJobLogClick(object? sender, RoutedEventArgs e)
    {
        BackgroundJobRow? selectedRow = BackgroundJobsGrid?.SelectedItem as BackgroundJobRow
            ?? _backgroundJobs.FirstOrDefault();
        if (selectedRow is null)
        {
            await new NetworkInfoWindow("Background Job Log", "No background job has been selected or queued yet.").ShowDialog(this);
            return;
        }

        string details = _app.BackgroundJobs.ReadJobLog(selectedRow.JobId);
        await new NetworkInfoWindow($"Job Log: {selectedRow.Title}", details).ShowDialog(this);
    }

    private async void OnViewDicomTraceLogClick(object? sender, RoutedEventArgs e)
    {
        string logPath = _app.NetworkSettingsService.CurrentSettings.DicomCommunicationLogPath;
        string details = File.Exists(logPath)
            ? File.ReadAllText(logPath)
            : $"No DICOM communication trace exists yet at:\n{logPath}";
        await new NetworkInfoWindow("DICOM Communication Trace", details).ShowDialog(this);
    }

    private void OpenStudyInViewerWindows(
        StudyDetails details,
        RemoteStudyRetrievalSession? retrievalSession,
        PriorStudyLookupMode priorLookupMode,
        IReadOnlyList<PriorStudySummary> priorStudies)
    {
        CloseManagedViewerWindows();

        int viewerCount = Math.Clamp(_viewerWindowCount, 1, 4);
        bool priorsAvailable = priorStudies.Count > 0;

        for (int index = 0; index < viewerCount; index++)
        {
            PriorStudySummary? assignedPriorStudy = index > 0 && index - 1 < priorStudies.Count
                ? priorStudies[index - 1]
                : null;
            bool startBlank = assignedPriorStudy is not null || (priorsAvailable && index > 0);
            var viewer = new StudyViewerWindow(
                new ViewerStudyContext
                {
                    StudyDetails = details,
                    RemoteRetrievalSession = index == 0 ? retrievalSession : null,
                    LoadPriorStudiesAsync = cancellationToken => _app.PriorStudyLookupService.FindPriorStudiesAsync(details.Study, priorLookupMode, cancellationToken),
                    LoadPriorStudyPreviewAsync = (priorStudy, onUpdated, cancellationToken) => _app.PriorStudyLookupService.LoadPriorStudyPreviewAsync(priorStudy, onUpdated, cancellationToken),
                    InitialPriorStudies = priorStudies,
                    InitialAssignedPriorStudy = assignedPriorStudy,
                    StartBlank = startBlank,
                    LayoutRows = 1,
                    LayoutColumns = 1,
                },
                $"StudyViewerWindow{index + 1}",
                index + 1);
            viewer.Closed += OnManagedViewerClosed;
            _managedViewerWindows.Add(viewer);
            viewer.Show();
        }

        string statusMessage = priorsAvailable && viewerCount > 1
            ? $"Opened study in Viewer 1 and assigned available prior studies to the additional viewers."
            : viewerCount == 1
                ? "Opened study in Viewer 1."
                : $"Opened study in {viewerCount} viewer windows.";
        SetStatus(statusMessage);
    }

    private async Task OpenSelectedPatientStudiesAsync()
    {
        PatientRow? selectedPatient = PatientGrid?.SelectedItem as PatientRow;
        if (selectedPatient is null)
        {
            SetStatus("Select a patient first.");
            ShowToast("Select a patient first.", ToastSeverity.Warning);
            return;
        }

        SetStatus($"Loading all studies for {selectedPatient.PatientName}...");

        List<PatientStudyLaunchCandidate> candidates = await LoadPatientStudyLaunchCandidatesAsync(selectedPatient);
        if (candidates.Count == 0)
        {
            SetStatus($"No studies found for {selectedPatient.PatientName}.");
            ShowToast($"No studies found for {selectedPatient.PatientName}.", ToastSeverity.Warning);
            return;
        }

        PatientStudyLaunchCandidate primaryCandidate = candidates[0];
        (StudyDetails? details, RemoteStudyRetrievalSession? retrievalSession) = await ResolveViewerLaunchStudyAsync(primaryCandidate);
        if (details is null)
        {
            SetStatus($"The newest study for {selectedPatient.PatientName} could not be loaded.");
            ShowToast($"The newest study for {selectedPatient.PatientName} could not be loaded.", ToastSeverity.Error);
            return;
        }

        if (!primaryCandidate.IsRemote && details.Study.Availability != StudyAvailability.Imported)
        {
            bool queued = await _app.ImportService.QueueStudyImportAsync(details);
            if (queued)
            {
                ShowToast($"Opening {selectedPatient.PatientName}. Local files are being copied into the imagebox in the background.", ToastSeverity.Info, TimeSpan.FromSeconds(6));
            }
        }

        List<PriorStudySummary> priorStudies = candidates
            .Skip(1)
            .Select(candidate => ToPriorStudySummary(candidate.Study, candidate.RemoteResult))
            .ToList();

        PriorStudyLookupMode priorLookupMode = primaryCandidate.IsRemote
            ? PriorStudyLookupMode.RemoteArchive
            : PriorStudyLookupMode.LocalRepository;

        OpenStudyInViewerWindows(details, retrievalSession, priorLookupMode, priorStudies);

        int additionalCount = Math.Max(0, candidates.Count - 1);
        SetStatus(additionalCount == 0
            ? $"Opened the newest study for {selectedPatient.PatientName}."
            : $"Opened the newest study for {selectedPatient.PatientName} and queued {additionalCount} additional studies for comparison viewers/history.");
    }

    private async Task<List<PatientStudyLaunchCandidate>> LoadPatientStudyLaunchCandidatesAsync(PatientRow selectedPatient)
    {
        StudyQuery query = BuildPatientStudyQuery(selectedPatient);

        List<StudyListItem> localStudies = [];
        try
        {
            localStudies = await _app.Repository.SearchStudiesAsync(query);
        }
        catch
        {
            localStudies = [];
        }

        List<RemoteStudySearchResult> remoteStudies = [];
        try
        {
            remoteStudies = await _app.RemoteStudyBrowserService.SearchStudiesAsync(query);
        }
        catch
        {
            remoteStudies = [];
        }

        Dictionary<string, PatientStudyLaunchCandidate> candidatesByStudyUid = new(StringComparer.Ordinal);

        foreach (StudyListItem study in localStudies.Where(study => MatchesPatient(selectedPatient, study)))
        {
            candidatesByStudyUid[study.StudyInstanceUid] = new PatientStudyLaunchCandidate(study, null);
        }

        foreach (RemoteStudySearchResult result in remoteStudies.Where(result => MatchesPatient(selectedPatient, result.Study)))
        {
            if (candidatesByStudyUid.ContainsKey(result.Study.StudyInstanceUid))
            {
                continue;
            }

            candidatesByStudyUid[result.Study.StudyInstanceUid] = new PatientStudyLaunchCandidate(result.Study, result);
        }

        return candidatesByStudyUid.Values
            .OrderByDescending(candidate => candidate.Study.StudyDate, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.IsRemote)
            .ThenBy(candidate => candidate.Study.StudyInstanceUid, StringComparer.Ordinal)
            .ToList();
    }

    private static StudyQuery BuildPatientStudyQuery(PatientRow patient)
    {
        string patientId = patient.PatientId?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(patientId))
        {
            return new StudyQuery
            {
                PatientId = patientId,
            };
        }

        return new StudyQuery
        {
            PatientName = patient.PatientName?.Trim() ?? string.Empty,
            PatientBirthDate = NormalizePatientBirthDate(patient.PatientBirthDate),
        };
    }

    private async Task<(StudyDetails? Details, RemoteStudyRetrievalSession? RetrievalSession)> ResolveViewerLaunchStudyAsync(PatientStudyLaunchCandidate candidate)
    {
        if (!candidate.IsRemote)
        {
            StudyDetails? details = candidate.Study.StudyKey > 0
                ? await _app.Repository.GetStudyDetailsAsync(candidate.Study.StudyKey)
                : await _app.Repository.GetStudyDetailsByStudyInstanceUidAsync(candidate.Study.StudyInstanceUid);
            return (details, null);
        }

        RemoteStudySearchResult remoteStudy = candidate.RemoteResult!;
        try
        {
            (StudyDetails details, List<RemoteSeriesPreview> seriesPreviews) = await _app.RemoteStudyBrowserService.LoadStudyPreviewAsync(remoteStudy);
            RemoteStudyRetrievalSession retrievalSession = await _app.RemoteStudyBrowserService.CreateRetrievalSessionAsync(
                remoteStudy,
                details,
                seriesPreviews,
                CancellationToken.None);
            retrievalSession.StatusChanged += OnRemoteRetrievalStatusChanged;
            return (retrievalSession.StudyDetails, retrievalSession);
        }
        catch (Exception ex)
        {
            ShowToast($"Remote study preview failed: {ex.Message}", ToastSeverity.Error, TimeSpan.FromSeconds(8));
            return (null, null);
        }
    }

    private static PriorStudySummary ToPriorStudySummary(StudyListItem study, RemoteStudySearchResult? remoteResult) => new()
    {
        StudyKey = study.StudyKey,
        StudyInstanceUid = study.StudyInstanceUid,
        StudyDescription = study.StudyDescription,
        Modalities = study.Modalities,
        StudyDate = study.StudyDate,
        SourceLabel = remoteResult?.Archive.Name ?? study.StoragePath,
        IsRemote = remoteResult is not null,
        ArchiveId = remoteResult?.Archive.Id ?? string.Empty,
        IsNewerThanCurrentStudy = false,
    };

    private static bool MatchesPatient(PatientRow patient, StudyListItem study)
    {
        if (!string.IsNullOrWhiteSpace(patient.PatientId) && !string.IsNullOrWhiteSpace(study.PatientId))
        {
            return string.Equals(patient.PatientId.Trim(), study.PatientId.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        bool sameName = string.Equals(patient.PatientName.Trim(), study.PatientName.Trim(), StringComparison.OrdinalIgnoreCase);
        if (!sameName)
        {
            return false;
        }

        string patientBirthDate = NormalizePatientBirthDate(patient.PatientBirthDate);
        return string.IsNullOrWhiteSpace(patientBirthDate)
            || string.IsNullOrWhiteSpace(study.PatientBirthDate)
            || string.Equals(patientBirthDate, study.PatientBirthDate.Trim(), StringComparison.Ordinal);
    }

    private static string NormalizePatientBirthDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string trimmed = value.Trim();
        if (DateOnly.TryParse(trimmed, out DateOnly parsed))
        {
            return parsed.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
        }

        return trimmed.Replace(".", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("/", string.Empty, StringComparison.Ordinal);
    }

    private void CloseManagedViewerWindows()
    {
        foreach (StudyViewerWindow viewer in _managedViewerWindows.ToList())
        {
            viewer.Closed -= OnManagedViewerClosed;
            viewer.Close();
        }

        _managedViewerWindows.Clear();
    }

    private void OnManagedViewerClosed(object? sender, EventArgs e)
    {
        if (sender is not StudyViewerWindow viewer)
        {
            return;
        }

        viewer.Closed -= OnManagedViewerClosed;
        _managedViewerWindows.Remove(viewer);
    }

    private async Task RefreshNetworkInfoPanelAsync()
    {
        if (RemoteArchivePrimaryText is null
            || RemoteArchiveSecondaryText is null
            || RemoteArchiveBadge is null
            || RemoteArchiveBadgeText is null
            || StorageScpPrimaryText is null
            || StorageScpSecondaryText is null
            || StorageScpBadge is null
            || StorageScpBadgeText is null
            || NetworkConfigurationHintBorder is null
            || NetworkConfigurationHintText is null)
        {
            return;
        }

        int refreshVersion = Interlocked.Increment(ref _networkInfoRefreshVersion);
        DicomNetworkSettings settings = _app.NetworkSettingsService.CurrentSettings;
        RemoteArchiveEndpoint? archive = settings.GetSelectedArchive();

        NetworkConfigurationHintBorder.IsVisible = archive is null;
        NetworkConfigurationHintText.Text = archive is null
            ? $"No archive configured. Local SCP: AE {settings.LocalAeTitle} / {settings.LocalPort}."
            : string.Empty;

        ApplyHealthBadge(RemoteArchiveBadge, RemoteArchiveBadgeText, archive is null ? "Setup needed" : "Checking", HealthTone.Warning);
        RemoteArchivePrimaryText.Text = archive is null
            ? "Configure an archive for query and send."
            : $"{archive.Name} • {archive.Host}:{archive.Port} • AE {archive.RemoteAeTitle}";
        RemoteArchiveSecondaryText.Text = archive is null
            ? "Use Configure to add the endpoint."
            : "Testing reachability...";

        bool scpRunning = _app.StorageScpService.IsRunning;
        string scpStatus = _app.StorageScpService.LastStatus;
        ApplyHealthBadge(StorageScpBadge, StorageScpBadgeText, scpRunning ? "Listening" : "Stopped", scpRunning ? HealthTone.Success : HealthTone.Warning);
        StorageScpPrimaryText.Text = $"AE {settings.LocalAeTitle} • Port {settings.LocalPort} • {_app.StorageScpService.ReceivedFiles} received files";
        StorageScpSecondaryText.Text = $"Inbox {CompactPath(settings.InboxDirectory)} • Trace {(settings.EnableDicomCommunicationLogging ? "On" : "Off")} • {CompactStatus(scpStatus)}";

        if (archive is null)
        {
            return;
        }

        (bool reachable, string detail) = await CheckArchiveConnectivityAsync(archive);
        if (refreshVersion != _networkInfoRefreshVersion)
        {
            return;
        }

        ApplyHealthBadge(RemoteArchiveBadge, RemoteArchiveBadgeText, reachable ? "Online" : "Offline", reachable ? HealthTone.Success : HealthTone.Error);
        RemoteArchiveSecondaryText.Text = reachable
            ? $"Reachable • {CompactStatus(detail)}"
            : $"Unreachable • {CompactStatus(detail)}";
    }

    private async Task RefreshDatabaseInfoPanelAsync()
    {
        if (LocalDatabasePrimaryText is null
            || LocalDatabaseSecondaryText is null
            || LocalDatabaseBadge is null
            || LocalDatabaseBadgeText is null
            || DiskHealthPrimaryText is null
            || DiskHealthSecondaryText is null
            || DiskHealthBadge is null
            || DiskHealthBadgeText is null)
        {
            return;
        }

        int refreshVersion = Interlocked.Increment(ref _databaseInfoRefreshVersion);
        bool localDbExists = File.Exists(_app.Paths.DatabasePath);
        FileInfo? dbInfo = localDbExists ? new FileInfo(_app.Paths.DatabasePath) : null;
        int localStudyCount;
        try
        {
            localStudyCount = (await _app.Repository.SearchStudiesAsync(new StudyQuery())).Count;
        }
        catch
        {
            localStudyCount = 0;
        }

        if (refreshVersion != _databaseInfoRefreshVersion)
        {
            return;
        }

        ApplyHealthBadge(LocalDatabaseBadge, LocalDatabaseBadgeText, localDbExists ? "Healthy" : "Missing", localDbExists ? HealthTone.Success : HealthTone.Error);
        LocalDatabasePrimaryText.Text = localDbExists && dbInfo is not null
            ? $"{localStudyCount} studies • {FormatFileSize(dbInfo.Length)} • {Path.GetFileName(_app.Paths.DatabasePath)}"
            : "Database file not found.";
        LocalDatabaseSecondaryText.Text = localDbExists && dbInfo is not null
            ? $"Updated {dbInfo.LastWriteTime:dd.MM.yy HH:mm} • {CompactPath(_app.Paths.DatabasePath)}"
            : CompactPath(_app.Paths.DatabasePath);

        DriveHealth diskHealth = GetDriveHealth(_app.Paths.DatabasePath, _app.NetworkSettingsService.CurrentSettings.InboxDirectory);
        ApplyHealthBadge(DiskHealthBadge, DiskHealthBadgeText, diskHealth.Label, diskHealth.Tone);
        DiskHealthPrimaryText.Text = diskHealth.PrimaryText;
        DiskHealthSecondaryText.Text = diskHealth.SecondaryText;
    }

    private static (bool reachable, string detail) SetConnectivityResult(bool reachable, string detail) => (reachable, detail);

    private async Task<(bool reachable, string detail)> CheckArchiveConnectivityAsync(RemoteArchiveEndpoint archive)
    {
        using var client = new TcpClient();
        try
        {
            Task connectTask = client.ConnectAsync(archive.Host, archive.Port);
            Task completed = await Task.WhenAny(connectTask, Task.Delay(TimeSpan.FromSeconds(1.5)));
            if (completed != connectTask)
            {
                return SetConnectivityResult(false, "Timed out during TCP connect.");
            }

            await connectTask;
            return SetConnectivityResult(true, $"{archive.Host}:{archive.Port} accepts TCP connections.");
        }
        catch (Exception ex)
        {
            return SetConnectivityResult(false, ex.Message);
        }
    }

    private DriveHealth GetDriveHealth(string databasePath, string inboxDirectory)
    {
        try
        {
            string dbRoot = Path.GetPathRoot(databasePath) ?? string.Empty;
            var dbDrive = !string.IsNullOrWhiteSpace(dbRoot) ? new DriveInfo(dbRoot) : null;

            string inboxRoot = Path.GetPathRoot(inboxDirectory) ?? string.Empty;
            var inboxDrive = !string.IsNullOrWhiteSpace(inboxRoot) ? new DriveInfo(inboxRoot) : null;

            if (dbDrive is null && inboxDrive is null)
            {
                return new DriveHealth("Unknown", HealthTone.Warning, "Drive information is unavailable.", "Could not resolve the database or inbox drive.");
            }

            DriveInfo primary = dbDrive ?? inboxDrive!;
            double freePercent = primary.TotalSize <= 0 ? 0 : (double)primary.AvailableFreeSpace / primary.TotalSize;
            HealthTone tone = freePercent switch
            {
                < 0.10 => HealthTone.Error,
                < 0.20 => HealthTone.Warning,
                _ => HealthTone.Success,
            };

            string label = tone switch
            {
                HealthTone.Error => "Low space",
                HealthTone.Warning => "Watch",
                _ => "Healthy",
            };

            string primaryText = $"DB drive {primary.Name} • {FormatFileSize(primary.AvailableFreeSpace)} free of {FormatFileSize(primary.TotalSize)}";
            string secondaryText = inboxDrive is not null && !string.Equals(inboxDrive.Name, primary.Name, StringComparison.OrdinalIgnoreCase)
                ? $"Inbox {inboxDrive.Name} • {FormatFileSize(inboxDrive.AvailableFreeSpace)} free"
                : $"Free space ratio: {freePercent:P0}";

            return new DriveHealth(label, tone, primaryText, secondaryText);
        }
        catch (Exception ex)
        {
            return new DriveHealth("Unknown", HealthTone.Warning, "Drive information is unavailable.", ex.Message);
        }
    }

    private static string FormatFileSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        int unitIndex = 0;
        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }

        return unitIndex == 0 ? $"{size:0} {units[unitIndex]}" : $"{size:0.0} {units[unitIndex]}";
    }

    private static string CompactPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        string fileName = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        string parent = Path.GetDirectoryName(path) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return path;
        }

        string parentName = Path.GetFileName(parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrWhiteSpace(parentName) ? fileName : $"{parentName}/{fileName}";
    }

    private static string CompactStatus(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string singleLine = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return singleLine.Length <= 52 ? singleLine : singleLine[..49] + "...";
    }

    private static void ApplyHealthBadge(Border badgeBorder, TextBlock badgeText, string label, HealthTone tone)
    {
        badgeText.Text = label;
        (badgeBorder.Background, badgeText.Foreground) = tone switch
        {
            HealthTone.Success => (new SolidColorBrush(Color.Parse("#FFDFF6E5")), new SolidColorBrush(Color.Parse("#FF1B6E34"))),
            HealthTone.Warning => (new SolidColorBrush(Color.Parse("#FFFCEFD6")), new SolidColorBrush(Color.Parse("#FF8A5A00"))),
            HealthTone.Error => (new SolidColorBrush(Color.Parse("#FFF7DEDE")), new SolidColorBrush(Color.Parse("#FF9C2D2D"))),
            _ => (new SolidColorBrush(Color.Parse("#FFE7EDF2")), new SolidColorBrush(Color.Parse("#FF41515D"))),
        };
    }

    private enum HealthTone
    {
        Neutral,
        Success,
        Warning,
        Error,
    }

    private sealed record DriveHealth(string Label, HealthTone Tone, string PrimaryText, string SecondaryText);

    private sealed class BackgroundJobRow
    {
        public Guid JobId { get; init; }
        public string TypeLabel { get; init; } = string.Empty;
        public string Title { get; init; } = string.Empty;
        public string StateLabel { get; init; } = string.Empty;
        public string ProgressLabel { get; init; } = string.Empty;
        public string StatusText { get; init; } = string.Empty;
    }

    private async void OnStudyDoubleTapped(object? sender, TappedEventArgs e) => await OpenSelectedStudyAsync();

    private async Task DeleteSelectedStudyAsync()
    {
        if (_browserMode != BrowserMode.Database)
        {
            StatusText.Text = "Delete Study is only available in Database mode.";
            return;
        }

        List<StudyListItem> selectedStudies = GetSelectedStudies();
        if (selectedStudies.Count == 0)
        {
            StatusText.Text = "Select a study first.";
            return;
        }

        var confirmWindow = new ConfirmDeleteStudyWindow(selectedStudies);
        bool confirmed = await confirmWindow.ShowDialog<bool>(this);
        if (!confirmed)
        {
            StatusText.Text = selectedStudies.Count > 1 ? "Delete studies cancelled." : "Delete study cancelled.";
            return;
        }

        try
        {
            foreach (StudyListItem selectedStudy in selectedStudies)
            {
                await _app.StudyDeletionService.DeleteStudyAsync(selectedStudy);
            }

            string statusMessage = selectedStudies.Count == 1
                ? $"Deleted study {selectedStudies[0].PatientName} ({selectedStudies[0].DisplayStudyDate}) from the K-PACS imagebox."
                : $"Deleted {selectedStudies.Count} studies from the K-PACS imagebox.";
            await RefreshCurrentModeAsync(statusMessage);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Delete study failed: {ex.Message}";
        }
    }

    private List<StudyListItem> GetSelectedStudies()
    {
        System.Collections.IList? selectedItems = StudyGrid.SelectedItems;
        List<StudyListItem> selectedStudies = selectedItems?.OfType<StudyListItem>().ToList() ?? [];
        if (selectedStudies.Count == 0 && StudyGrid.SelectedItem is StudyListItem selectedStudy)
        {
            selectedStudies.Add(selectedStudy);
        }

        return selectedStudies
            .GroupBy(study => study.SelectionId)
            .Select(group => group.First())
            .ToList();
    }

    private StudyListItem? GetPrimarySelectedStudy() => GetSelectedStudies().FirstOrDefault();

    private void RestoreStudySelection(IReadOnlyCollection<string> selectionIds, bool selectFirstIfNoMatch = true)
    {
        if (_studies.Count == 0)
        {
            StudyGrid.SelectedItem = null;
            UpdateStudyActionAvailability();
            return;
        }

        List<StudyListItem> matches = _studies.Where(study => selectionIds.Contains(study.SelectionId)).ToList();
        if (matches.Count == 0)
        {
            if (selectFirstIfNoMatch)
            {
                StudyGrid.SelectedItem = _studies.FirstOrDefault();
            }
            else
            {
                System.Collections.IList? gridSelectedItems = StudyGrid.SelectedItems;
                gridSelectedItems?.Clear();
                StudyGrid.SelectedItem = null;
            }

            UpdateStudyActionAvailability();
            return;
        }

        System.Collections.IList? selectedItems = StudyGrid.SelectedItems;
        if (selectedItems is not null)
        {
            selectedItems.Clear();
            foreach (StudyListItem study in matches)
            {
                selectedItems.Add(study);
            }
        }

        StudyGrid.SelectedItem = matches[0];
        UpdateStudyActionAvailability();
    }

    private void UpdateStudyActionAvailability()
    {
        if (ViewActionButton is null || SendActionButton is null || RelayActionButton is null || ModifyActionButton is null)
        {
            return;
        }

        int selectedCount = GetSelectedStudies().Count;
        bool singleSelected = selectedCount == 1;
        bool anySelected = selectedCount > 0;
        StudyListItem? selectedStudy = singleSelected ? GetPrimarySelectedStudy() : null;
        bool sendEnabled = anySelected
            && !_filesystemScanInProgress
            && (_browserMode == BrowserMode.Database || _browserMode == BrowserMode.Filesystem);

        ViewActionButton.IsEnabled = singleSelected;
        SendActionButton.IsEnabled = sendEnabled;
        RelayActionButton.IsEnabled = sendEnabled;
        ModifyActionButton.IsEnabled = _browserMode == BrowserMode.Database
            && singleSelected
            && selectedStudy?.Availability == StudyAvailability.Imported;
    }

    private void BeginFilesystemScan(string folderPath)
    {
        _filesystemScanInProgress = true;
        _filesystemPreviewDetails = new Dictionary<string, StudyDetails>(StringComparer.Ordinal);
        _filesystemScannedStudies = [];
        _seriesRows.Clear();

        if (FilesystemScanProgressPanel is not null)
        {
            FilesystemScanProgressPanel.IsVisible = true;
        }

        if (FilesystemScanProgressBar is not null)
        {
            FilesystemScanProgressBar.IsIndeterminate = true;
            FilesystemScanProgressBar.Value = 0;
        }

        if (FilesystemScanProgressText is not null)
        {
            FilesystemScanProgressText.Text = $"Scanning {folderPath}...";
        }

        LoadFilesystemPreviewStudies($"Scanning folder: {folderPath}", applySearchFilters: false);
        UpdateStudyActionAvailability();
    }

    private void UpdateFilesystemScanProgress(FilesystemScanProgress progress)
    {
        if (FilesystemScanProgressText is not null)
        {
            FilesystemScanProgressText.Text = $"{progress.ScannedFiles} files scanned, {progress.SkippedFiles} skipped, {progress.StudyCount} studies found.";
        }

        if (progress.UpdatedStudy is not null)
        {
            _filesystemPreviewDetails[progress.UpdatedStudy.Study.StudyInstanceUid] = progress.UpdatedStudy;
            _filesystemScannedStudies = _filesystemPreviewDetails.Values.Select(study => study.Study).ToList();
            LoadFilesystemPreviewStudies(progress.Message, applySearchFilters: false);
        }
        else if (!string.IsNullOrWhiteSpace(progress.Message))
        {
            SetStatus(progress.Message);
        }
    }

    private void FinishFilesystemScan(string statusMessage)
    {
        _filesystemScanInProgress = false;

        if (FilesystemScanProgressPanel is not null)
        {
            FilesystemScanProgressPanel.IsVisible = true;
        }

        if (FilesystemScanProgressBar is not null)
        {
            FilesystemScanProgressBar.IsIndeterminate = false;
            FilesystemScanProgressBar.Value = 100;
        }

        if (FilesystemScanProgressText is not null)
        {
            FilesystemScanProgressText.Text = $"Scan complete. {_filesystemPreviewDetails.Count} studies are ready for preview and send.";
        }

        UpdateStudyActionAvailability();
        SetStatus(statusMessage);
    }

    private void OnPatientStudySplitterPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        CapturePatientPaneWidth();
        SaveBrowserLayoutSettings();
    }

    private void OnStudySeriesSplitterPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        CaptureSeriesPaneHeight();
        SaveBrowserLayoutSettings();
    }

    private void CapturePatientPaneWidth()
    {
        if (BrowserContentGrid?.ColumnDefinitions.Count > 1)
        {
            double width = BrowserContentGrid.ColumnDefinitions[1].ActualWidth;
            if (width > 0)
            {
                _patientPaneWidth = Math.Clamp(width, MinPatientPaneWidth, MaxPatientPaneWidth);
            }
        }
    }

    private void CaptureSeriesPaneHeight()
    {
        if (BrowserContentGrid?.RowDefinitions.Count > 2)
        {
            double height = BrowserContentGrid.RowDefinitions[2].ActualHeight;
            if (height > 0)
            {
                _seriesPaneHeight = Math.Clamp(height, MinSeriesPaneHeight, MaxSeriesPaneHeight);
            }
        }
    }

    private void LoadBrowserLayoutSettings()
    {
        try
        {
            if (!File.Exists(_browserLayoutSettingsPath))
            {
                return;
            }

            BrowserLayoutSettings? settings = JsonSerializer.Deserialize<BrowserLayoutSettings>(File.ReadAllText(_browserLayoutSettingsPath));
            if (settings is not null)
            {
                _patientPaneWidth = Math.Clamp(settings.PatientPaneWidth, MinPatientPaneWidth, MaxPatientPaneWidth);
                _seriesPaneHeight = Math.Clamp(settings.SeriesPaneHeight, MinSeriesPaneHeight, MaxSeriesPaneHeight);
                _showPatientPanel = settings.ShowPatientPanel;
                _browserMode = settings.LastBrowserMode;
                _filesystemRootPath = string.IsNullOrWhiteSpace(settings.FilesystemRootPath) ? null : settings.FilesystemRootPath;
                _viewerWindowCount = Math.Clamp(settings.ViewerWindowCount, 1, 4);
                _onboardingCompletedVersion = Math.Max(0, settings.OnboardingCompletedVersion);
                _onboardingDismissedVersion = Math.Max(0, settings.OnboardingDismissedVersion);
            }
        }
        catch
        {
            _patientPaneWidth = DefaultPatientPaneWidth;
            _seriesPaneHeight = DefaultSeriesPaneHeight;
            _showPatientPanel = true;
            _browserMode = BrowserMode.Database;
            _filesystemRootPath = null;
            _viewerWindowCount = 1;
            _onboardingCompletedVersion = 0;
            _onboardingDismissedVersion = 0;
        }
    }

    private void SaveBrowserLayoutSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_browserLayoutSettingsPath) ?? _app.Paths.ApplicationDirectory);
            BrowserLayoutSettings settings = new()
            {
                PatientPaneWidth = Math.Clamp(_patientPaneWidth, MinPatientPaneWidth, MaxPatientPaneWidth),
                SeriesPaneHeight = Math.Clamp(_seriesPaneHeight, MinSeriesPaneHeight, MaxSeriesPaneHeight),
                ShowPatientPanel = _showPatientPanel,
                LastBrowserMode = _browserMode,
                FilesystemRootPath = _filesystemRootPath,
                ViewerWindowCount = Math.Clamp(_viewerWindowCount, 1, 4),
                OnboardingCompletedVersion = _onboardingCompletedVersion,
                OnboardingDismissedVersion = _onboardingDismissedVersion,
            };

            File.WriteAllText(_browserLayoutSettingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
        }
    }

    private async Task EnsureFilesystemRootLoadedAsync()
    {
        if (!string.IsNullOrWhiteSpace(_filesystemRootPath) && Directory.Exists(_filesystemRootPath))
        {
            await LoadFilesystemRootAsync(_filesystemRootPath);
            return;
        }

        await LoadComputerRootAsync();
    }

    private static bool HasAnyLocalInstances(StudyDetails details) =>
        details.Series.SelectMany(series => series.Instances)
            .Any(instance => !string.IsNullOrWhiteSpace(instance.FilePath) && File.Exists(instance.FilePath));

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

    private static bool IsEmptyNetworkQuery(StudyQuery query)
    {
        return string.IsNullOrWhiteSpace(query.PatientId)
            && string.IsNullOrWhiteSpace(query.PatientName)
            && string.IsNullOrWhiteSpace(query.PatientBirthDate)
            && string.IsNullOrWhiteSpace(query.AccessionNumber)
            && string.IsNullOrWhiteSpace(query.ReferringPhysician)
            && string.IsNullOrWhiteSpace(query.StudyDescription)
            && string.IsNullOrWhiteSpace(query.QuickSearch)
            && query.FromStudyDate is null
            && query.ToStudyDate is null
            && query.Modalities.Count == 0;
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

    private static int GetBrowserModeTabIndex(BrowserMode mode) => mode switch
    {
        BrowserMode.Network => 0,
        BrowserMode.Database => 1,
        BrowserMode.Filesystem => 2,
        BrowserMode.Email => 3,
        _ => 1,
    };



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

    private enum OnboardingPrimaryAction
    {
        None,
        OpenNetworkConfiguration,
        ChooseFilesystemRoot,
        OpenSelectedStudy,
    }

    private sealed record OnboardingStep(
        string Badge,
        string Title,
        string Summary,
        string[] Bullets,
        string Hint,
        string ToastMessage,
        string StatusText,
        BrowserMode? Mode,
        OnboardingPrimaryAction PrimaryAction = OnboardingPrimaryAction.None,
        string? PrimaryActionLabel = null);

    private static IReadOnlyList<OnboardingStep> CreateOnboardingSteps() =>
    [
        new(
            Badge: "Welcome",
            Title: "Get oriented in a few steps",
            Summary: "K-PACS lets you query remote archives, import from folders, review studies, change display settings in the viewer, delete local studies, pseudonymize datasets, and send data back out.",
            Bullets:
            [
                "Use the top mode tabs to switch between Network, Database, and Filesystem workflows.",
                "Use the left Actions column for View, Send, Modify, and this Guide button.",
                "Nothing changes until you explicitly search, import, send, delete, or pseudonymize a study."
            ],
            Hint: "The tour follows the normal workflow from connection setup to import, viewing, cleanup, and anonymization.",
            ToastMessage: "Welcome to K-PACS. This tour walks through the core workflow and can be reopened with Guide.",
            StatusText: "Welcome to K-PACS. The onboarding tour is ready.",
            Mode: null),
        new(
            Badge: "Network",
            Title: "Set up the DICOM connection first",
            Summary: "Remote archive access and DICOM send require the local AE title, listening port, archive host, and remote AE title to be configured correctly.",
            Bullets:
            [
                "Open Network mode and click Cfg to edit the DICOM connection.",
                "Set the local AE title and local port used by Storage SCP / Inbox.",
                "Add or select a remote archive endpoint before searching or sending."
            ],
            Hint: "Use the network health cards to confirm archive reachability and whether the Storage SCP is listening.",
            ToastMessage: "Start in Network mode: configure the local AE, port, and remote archive before querying or sending studies.",
            StatusText: "Onboarding: configure the DICOM connection in Network mode.",
            Mode: BrowserMode.Network,
            PrimaryAction: OnboardingPrimaryAction.OpenNetworkConfiguration,
            PrimaryActionLabel: "Open DICOM setup"),
        new(
            Badge: "Network",
            Title: "Search and retrieve remote studies",
            Summary: "Use the search filters to query the remote archive, then open one result or queue it for retrieval into the local imagebox.",
            Bullets:
            [
                "Enter at least one filter such as patient name, ID, study date, accession number, or modality.",
                "Click Search to query the configured remote archive.",
                "Select a result and use View to open it or Send after it is stored locally."
            ],
            Hint: "The status bar and toasts explain whether a query was empty, successful, or blocked by missing filters.",
            ToastMessage: "Remote querying uses the filters on the left. Enter at least one filter before pressing Search.",
            StatusText: "Onboarding: remote archive search happens in Network mode.",
            Mode: BrowserMode.Network),
        new(
            Badge: "Filesystem",
            Title: "Import studies from folders or DICOM media",
            Summary: "Filesystem mode scans a folder tree or DICOMDIR source, shows completed studies in the grid, and imports selected studies into SQLite in the background.",
            Bullets:
            [
                "Switch to Filesystem mode and choose a root folder or expand Computer.",
                "Right-click a folder in the tree and choose Scan folder.",
                "After preview studies appear, select one and use Import to copy it into the imagebox."
            ],
            Hint: "The Filesystem tab also shows import background jobs so you can keep working while a copy runs.",
            ToastMessage: "Filesystem mode scans folders first, then imports the selected preview study into the local imagebox.",
            StatusText: "Onboarding: scan media in Filesystem mode, then import the selected preview study.",
            Mode: BrowserMode.Filesystem,
            PrimaryAction: OnboardingPrimaryAction.ChooseFilesystemRoot,
            PrimaryActionLabel: "Choose folder root"),
        new(
            Badge: "Database",
            Title: "Open studies in the viewer",
            Summary: "The Database mode lists imported local studies. From there you can open one study in one or more viewer windows.",
            Bullets:
            [
                "Select a patient and one study in Database mode.",
                "Double-click the study row or press View in the Actions column.",
                "Use Viewer windows to choose whether the next study opens in 1 to 4 separate viewer windows."
            ],
            Hint: "Viewing is enabled only when a single study is selected.",
            ToastMessage: "To view a study, select one row in Database mode and double-click it or press View.",
            StatusText: "Onboarding: open a single imported study from Database mode.",
            Mode: BrowserMode.Database,
            PrimaryAction: OnboardingPrimaryAction.OpenSelectedStudy,
            PrimaryActionLabel: "Open selected study"),
        new(
            Badge: "Viewer",
            Title: "Adjust monitor and display settings in the viewer",
            Summary: "Inside the viewer, the floating toolbar controls stack scrolling, zoom-pan, window/level, measurements, layout presets, LUTs, and overlay visibility.",
            Bullets:
            [
                "Use Window to open presets such as Brain, Abdomen, Lung, Bone, and Reset.",
                "Use LUT buttons such as Grayscale, Inverted, Hot Iron, Rainbow, Gold, and Bone for monitor presentation.",
                "Use Scroll/Stack, Zoom-Pan, Layout, and Overlay to change how the study is displayed."
            ],
            Hint: "If a study is already selected, the action button can open it so you can try the viewer toolbar immediately.",
            ToastMessage: "Viewer display settings live in the floating toolbar: Window presets, LUTs, layout, zoom, and overlay controls.",
            StatusText: "Onboarding: viewer display settings are available in the floating toolbar.",
            Mode: BrowserMode.Database,
            PrimaryAction: OnboardingPrimaryAction.OpenSelectedStudy,
            PrimaryActionLabel: "Open selected study"),
        new(
            Badge: "Database",
            Title: "Delete studies carefully",
            Summary: "Deleting is only available for local studies in Database mode. The action removes the study from the local imagebox after confirmation.",
            Bullets:
            [
                "Switch to Database mode and select one or more local studies.",
                "Right-click the study grid and choose Delete Study.",
                "Confirm the deletion in the dialog before files are removed."
            ],
            Hint: "Delete is intentionally limited to the local Database mode so preview or remote studies cannot be removed by mistake.",
            ToastMessage: "Delete Study is available from the Database grid context menu after selecting one or more local studies.",
            StatusText: "Onboarding: deletion is available only for local studies in Database mode.",
            Mode: BrowserMode.Database),
        new(
            Badge: "Database",
            Title: "Pseudonymize / anonymize imported studies",
            Summary: "Use Modify in Database mode to pseudonymize a local study and replace patient-identifying fields with new values.",
            Bullets:
            [
                "Select one imported local study in Database mode.",
                "Click Modify in the Actions column to open the pseudonymization dialog.",
                "Enter replacement patient data and apply the changes to the study files and index."
            ],
            Hint: "The Modify action is enabled only when one imported study is selected.",
            ToastMessage: "Modify opens the pseudonymization dialog for one imported study in Database mode.",
            StatusText: "Onboarding: pseudonymization is available through Modify in Database mode.",
            Mode: BrowserMode.Database),
        new(
            Badge: "Send",
            Title: "Send local studies back to a remote archive",
            Summary: "After a study is local, use Send to queue a background DICOM transmission to the configured remote archive.",
            Bullets:
            [
                "Select one or more local studies in Database mode, or wait for Filesystem import scans to finish before sending preview studies.",
                "Make sure a remote archive is configured in Network mode first.",
                "Use the Filesystem info panel to monitor import jobs, and the job log to inspect failures."
            ],
            Hint: "Send uses the same network configuration as remote querying, so a working archive setup helps both workflows.",
            ToastMessage: "Send is available for local studies once a remote archive is configured in Network mode.",
            StatusText: "Onboarding: sending uses the configured remote archive and local study files.",
            Mode: BrowserMode.Database)
    ];

    private sealed record PatientStudyLaunchCandidate(StudyListItem Study, RemoteStudySearchResult? RemoteResult)
    {
        public bool IsRemote => RemoteResult is not null;
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

    private sealed class BrowserLayoutSettings
    {
        public double PatientPaneWidth { get; init; } = DefaultPatientPaneWidth;
        public double SeriesPaneHeight { get; init; } = DefaultSeriesPaneHeight;
        public bool ShowPatientPanel { get; init; } = true;
        public BrowserMode LastBrowserMode { get; init; } = BrowserMode.Database;
        public string? FilesystemRootPath { get; init; }
        public int ViewerWindowCount { get; init; } = 1;
        public int OnboardingCompletedVersion { get; init; }
        public int OnboardingDismissedVersion { get; init; }
    }
}

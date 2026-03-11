using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using FellowOakDicom;
using KPACS.DCMClasses;
using KPACS.Viewer.Models;
using KPACS.Viewer.Services;
using System.Globalization;

namespace KPACS.Viewer;

public partial class App : Application
{
    public ImageboxPaths Paths { get; private set; } = null!;
    public ImageboxRepository Repository { get; private set; } = null!;
    public BackgroundJobService BackgroundJobs { get; private set; } = null!;
    public DicomImportService ImportService { get; private set; } = null!;
    public DicomFilesystemScanService FilesystemScanService { get; private set; } = null!;
    public DicomPseudonymizationService PseudonymizationService { get; private set; } = null!;
    public DicomStudyDeletionService StudyDeletionService { get; private set; } = null!;
    public WindowPlacementService WindowPlacementService { get; private set; } = null!;
    public NetworkSettingsService NetworkSettingsService { get; private set; } = null!;
    public StorageScpService StorageScpService { get; private set; } = null!;
    public DicomRemoteStudyBrowserService RemoteStudyBrowserService { get; private set; } = null!;
    public PriorStudyLookupService PriorStudyLookupService { get; private set; } = null!;

    public override void Initialize()
    {
        var culture = CultureInfo.GetCultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;

        AvaloniaXamlLoader.Load(this);
        new DicomSetupBuilder().Build();

        var bootstrap = new ImageboxBootstrap();
        Paths = bootstrap.EnsurePaths();
        Repository = new ImageboxRepository(Paths.DatabasePath);
        Repository.InitializeAsync().GetAwaiter().GetResult();
        BackgroundJobs = new BackgroundJobService(Paths.ApplicationDirectory);
        ImportService = new DicomImportService(Paths, Repository, BackgroundJobs);
        FilesystemScanService = new DicomFilesystemScanService();
        PseudonymizationService = new DicomPseudonymizationService(Repository);
        StudyDeletionService = new DicomStudyDeletionService(Paths, Repository);
        WindowPlacementService = new WindowPlacementService(Path.Combine(Paths.ApplicationDirectory, "window-placement.json"));
        NetworkSettingsService = new NetworkSettingsService(Path.Combine(Paths.ApplicationDirectory, "network-settings.json"), Paths.ApplicationDirectory);
        DicomCommunicationTrace.Configure(
            NetworkSettingsService.CurrentSettings.EnableDicomCommunicationLogging,
            NetworkSettingsService.CurrentSettings.DicomCommunicationLogPath);
        StorageScpService = new StorageScpService(ImportService);
        RemoteStudyBrowserService = new DicomRemoteStudyBrowserService(NetworkSettingsService, Repository, BackgroundJobs);
        PriorStudyLookupService = new PriorStudyLookupService(Repository, RemoteStudyBrowserService);
        StorageScpService.ApplySettingsAsync(NetworkSettingsService.CurrentSettings).GetAwaiter().GetResult();
        NetworkSettingsService.SettingsChanged += settings =>
        {
            DicomCommunicationTrace.Configure(settings.EnableDicomCommunicationLogging, settings.DicomCommunicationLogPath);
            StorageScpService.ApplySettingsAsync(settings).GetAwaiter().GetResult();
        };
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow(this);
        }
        base.OnFrameworkInitializationCompleted();
    }
}

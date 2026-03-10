using System.Text;
using System.Threading.Channels;
using FellowOakDicom;
using FellowOakDicom.Network;
using KPACS.DCMClasses;
using KPACS.Viewer.Models;
using Microsoft.Extensions.Logging;

namespace KPACS.Viewer.Services;

public sealed class StorageScpService : IDisposable
{
    private readonly DicomImportService _importService;
    private readonly Channel<string> _importQueue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
    });
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly Task _importWorker;
    private readonly object _syncRoot = new();
    private IDicomServer? _server;
    private DicomNetworkSettings? _activeSettings;
    private long _receivedFiles;

    public StorageScpService(DicomImportService importService)
    {
        _importService = importService;
        _importWorker = Task.Run(ProcessImportQueueAsync);
    }

    internal static StorageScpService? ActiveInstance { get; private set; }

    public bool IsRunning { get; private set; }

    public long ReceivedFiles => Interlocked.Read(ref _receivedFiles);

    public string LastStatus { get; private set; } = "Storage SCP is stopped.";

    public event Action? StatusChanged;

    public Task ApplySettingsAsync(DicomNetworkSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Directory.CreateDirectory(settings.InboxDirectory);

        lock (_syncRoot)
        {
            _server?.Dispose();
            ActiveInstance = this;
            _activeSettings = settings;
            _server = DicomServerFactory.Create<StorageScpProvider>(settings.LocalPort);
            IsRunning = true;
            UpdateStatus($"Storage SCP listening on port {settings.LocalPort} as AE {settings.LocalAeTitle}.");
            DicomCommunicationTrace.Log("DICOM-SCP", $"Configured listener AE={settings.LocalAeTitle} port={settings.LocalPort} inbox={settings.InboxDirectory}.");
        }

        return Task.CompletedTask;
    }

    internal async Task<DicomCStoreResponse> HandleStoreAsync(DicomCStoreRequest request)
    {
        if (_activeSettings is null)
        {
            return new DicomCStoreResponse(request, DicomStatus.ProcessingFailure);
        }

        try
        {
            DicomDataset dataset = request.File?.Dataset ?? request.Dataset;
            string studyUid = dataset.GetSingleValueOrDefault(DicomTag.StudyInstanceUID, "unknown-study");
            string seriesUid = dataset.GetSingleValueOrDefault(DicomTag.SeriesInstanceUID, "unknown-series");
            string sopUid = dataset.GetSingleValueOrDefault(DicomTag.SOPInstanceUID, Guid.NewGuid().ToString("N"));
            string sopClassUid = dataset.GetSingleValueOrDefault(DicomTag.SOPClassUID, "unknown-sop-class");
            DicomCommunicationTrace.Log("DICOM-SCP", $"RECV C-STORE request study={studyUid} series={seriesUid} sop={sopUid} sopClass={sopClassUid}.");
            string extension = request.File?.File?.Name is string fileName
                ? Path.GetExtension(fileName)
                : string.Empty;
            if (string.IsNullOrWhiteSpace(extension))
            {
                extension = ".dcm";
            }

            string directory = Path.Combine(_activeSettings.InboxDirectory, Sanitize(studyUid), Sanitize(seriesUid));
            Directory.CreateDirectory(directory);
            string filePath = Path.Combine(directory, Sanitize(sopUid) + extension);

            if (request.File is not null)
            {
                await request.File.SaveAsync(filePath);
            }
            else
            {
                await new DicomFile(dataset).SaveAsync(filePath);
            }

            await _importQueue.Writer.WriteAsync(filePath, _disposeCts.Token);
            Interlocked.Increment(ref _receivedFiles);
            DicomCommunicationTrace.Log("DICOM-SCP", $"Stored incoming instance to {filePath}.");
            UpdateStatus($"Received {ReceivedFiles} instances. Last study: {studyUid}.");
            return new DicomCStoreResponse(request, DicomStatus.Success);
        }
        catch (Exception ex)
        {
            DicomCommunicationTrace.LogException("DICOM-SCP", "C-STORE receive failed", ex);
            UpdateStatus($"Storage SCP receive failed: {ex.Message}");
            return new DicomCStoreResponse(request, DicomStatus.ProcessingFailure);
        }
    }

    private async Task ProcessImportQueueAsync()
    {
        await foreach (string filePath in _importQueue.Reader.ReadAllAsync(_disposeCts.Token))
        {
            try
            {
                ImportResult result = await _importService.ImportPathAsync(filePath, _disposeCts.Token);
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }

                string summary = string.Join("  ", result.Messages.Where(message => !string.IsNullOrWhiteSpace(message)));
                UpdateStatus(string.IsNullOrWhiteSpace(summary)
                    ? $"Imported {ReceivedFiles} received instances into the local imagebox."
                    : summary);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                DicomCommunicationTrace.LogException("DICOM-SCP", $"Import failed for {filePath}", ex);
                UpdateStatus($"Storage SCP import failed: {ex.Message}");
            }
        }
    }

    private void UpdateStatus(string message)
    {
        LastStatus = message;
        StatusChanged?.Invoke();
    }

    private static string Sanitize(string value)
    {
        char[] invalidChars = Path.GetInvalidFileNameChars();
        return new string(value.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray());
    }

    public void Dispose()
    {
        _disposeCts.Cancel();
        lock (_syncRoot)
        {
            _server?.Dispose();
            _server = null;
            IsRunning = false;
        }
    }

    private sealed class StorageScpProvider : DicomService, IDicomServiceProvider, IDicomCEchoProvider, IDicomCStoreProvider
    {
        private const string StandardStorageUidRoot = "1.2.840.10008.5.1.4.1.1.";

        public StorageScpProvider(INetworkStream stream, Encoding fallbackEncoding, ILogger log, DicomServiceDependencies dependencies)
            : base(stream, fallbackEncoding, log, dependencies)
        {
        }

        public async Task OnReceiveAssociationRequestAsync(DicomAssociation association)
        {
            DicomCommunicationTrace.Log("DICOM-SCP", $"Association request calling={association.CallingAE.Trim()} called={association.CalledAE.Trim()} contexts={association.PresentationContexts.Count}.");
            foreach (DicomPresentationContext presentationContext in association.PresentationContexts)
            {
                if (presentationContext.AbstractSyntax == DicomUID.Verification)
                {
                    presentationContext.AcceptTransferSyntaxes(DicomTransferSyntax.ExplicitVRLittleEndian, DicomTransferSyntax.ImplicitVRLittleEndian);
                    DicomCommunicationTrace.Log("DICOM-SCP", $"Accepted Verification abstract={presentationContext.AbstractSyntax.UID}.");
                    continue;
                }

                if (IsSupportedStorageAbstractSyntax(presentationContext.AbstractSyntax))
                {
                    presentationContext.AcceptTransferSyntaxes(
                        DicomTransferSyntax.ExplicitVRLittleEndian,
                        DicomTransferSyntax.ImplicitVRLittleEndian,
                        DicomTransferSyntax.ExplicitVRBigEndian,
                        DicomTransferSyntax.JPEGProcess1,
                        DicomTransferSyntax.JPEGProcess2_4,
                        DicomTransferSyntax.JPEGLSLossless,
                        DicomTransferSyntax.JPEGLSNearLossless,
                        DicomTransferSyntax.JPEG2000Lossless,
                        DicomTransferSyntax.JPEG2000Lossy);
                    DicomCommunicationTrace.Log("DICOM-SCP", $"Accepted Storage abstract={presentationContext.AbstractSyntax.UID}."
                    );
                    continue;
                }

                presentationContext.SetResult(DicomPresentationContextResult.RejectAbstractSyntaxNotSupported);
                DicomCommunicationTrace.Log("DICOM-SCP", $"Rejected abstract={presentationContext.AbstractSyntax.UID} as unsupported.");
            }

            DicomCommunicationTrace.Log("DICOM-SCP", $"Association accepted for calling={association.CallingAE.Trim()}.");
            await SendAssociationAcceptAsync(association);
        }

        private static bool IsSupportedStorageAbstractSyntax(DicomUID abstractSyntax)
        {
            if (abstractSyntax.StorageCategory != DicomStorageCategory.None)
            {
                return true;
            }

            string? uid = abstractSyntax.UID;
            return !string.IsNullOrWhiteSpace(uid)
                && uid.StartsWith(StandardStorageUidRoot, StringComparison.Ordinal);
        }

        public Task OnReceiveAssociationReleaseRequestAsync()
        {
            DicomCommunicationTrace.Log("DICOM-SCP", "Association release request received.");
            return SendAssociationReleaseResponseAsync();
        }

        public void OnReceiveAbort(DicomAbortSource source, DicomAbortReason reason)
        {
            DicomCommunicationTrace.Log("DICOM-SCP", $"Association abort source={source} reason={reason}.");
        }

        public void OnConnectionClosed(Exception? exception)
        {
            if (exception is null)
            {
                DicomCommunicationTrace.Log("DICOM-SCP", "Connection closed.");
                return;
            }

            DicomCommunicationTrace.LogException("DICOM-SCP", "Connection closed with exception", exception);
        }

        public Task<DicomCEchoResponse> OnCEchoRequestAsync(DicomCEchoRequest request)
        {
            DicomCommunicationTrace.Log("DICOM-SCP", "Received C-ECHO request.");
            return Task.FromResult(new DicomCEchoResponse(request, DicomStatus.Success));
        }

        public Task<DicomCStoreResponse> OnCStoreRequestAsync(DicomCStoreRequest request)
        {
            DicomCommunicationTrace.Log("DICOM-SCP", "Dispatching C-STORE request to active service.");
            StorageScpService service = ActiveInstance ?? throw new InvalidOperationException("Storage SCP service is not active.");
            return service.HandleStoreAsync(request);
        }

        public Task OnCStoreRequestExceptionAsync(string tempFileName, Exception e)
        {
            if (!string.IsNullOrWhiteSpace(tempFileName) && File.Exists(tempFileName))
            {
                File.Delete(tempFileName);
            }

            DicomCommunicationTrace.LogException("DICOM-SCP", "C-STORE request exception", e);
            ActiveInstance?.UpdateStatus($"Storage SCP request exception: {e.Message}");
            return Task.CompletedTask;
        }
    }
}

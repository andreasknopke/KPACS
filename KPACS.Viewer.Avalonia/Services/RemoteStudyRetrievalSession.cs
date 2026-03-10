using System.Threading.Channels;
using FellowOakDicom;
using FellowOakDicom.Network;
using FellowOakDicom.Network.Client;
using KPACS.DCMClasses;
using KPACS.DCMClasses.Models;
using KPACS.Viewer.Models;

namespace KPACS.Viewer.Services;

public sealed class RemoteStudyRetrievalSession : IDisposable
{
    private readonly ImageboxRepository _repository;
    private readonly DicomNetworkClient _client;
    private readonly string _destinationAeTitle;
    private readonly RemoteStudySearchResult _result;
    private readonly Dictionary<string, RemoteSeriesPreview> _seriesPreviewByUid;
    private readonly Channel<string> _prioritySeriesQueue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
    });
    private readonly HashSet<string> _queuedPrioritySeries = new(StringComparer.Ordinal);
    private readonly HashSet<string> _seriesInFlight = new(StringComparer.Ordinal);
    private readonly HashSet<string> _imageInFlight = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _imageMoveGate = new(1, 1);
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly Task _backgroundSeriesTask;
    private readonly object _syncRoot = new();
    private volatile bool _backgroundRetrievalEnabled;
    private bool _isFaulted;
    private string? _faultMessage;

    public RemoteStudyRetrievalSession(
        ImageboxRepository repository,
        DicomNetworkClient client,
        string destinationAeTitle,
        RemoteStudySearchResult result,
        StudyDetails mergedStudy,
        IReadOnlyCollection<RemoteSeriesPreview> seriesPreviews)
    {
        _repository = repository;
        _client = client;
        _destinationAeTitle = destinationAeTitle;
        _result = result;
        StudyDetails = mergedStudy;
        SeriesPreviews = seriesPreviews.ToList();
        _seriesPreviewByUid = SeriesPreviews
            .Where(preview => !string.IsNullOrWhiteSpace(preview.LegacySeries.SerInstUid))
            .ToDictionary(preview => preview.LegacySeries.SerInstUid, StringComparer.Ordinal);
        _backgroundSeriesTask = Task.Run(ProcessBackgroundSeriesQueueAsync);
    }

    public StudyDetails StudyDetails { get; }

    public IReadOnlyList<RemoteSeriesPreview> SeriesPreviews { get; }

    public string StudyInstanceUid => StudyDetails.Study.StudyInstanceUid;

    public string LastStatus { get; private set; } = "Remote retrieval session created.";

    public bool IsFaulted => _isFaulted;

    public string? FaultMessage => _faultMessage;

    public bool IsBackgroundRetrievalEnabled => _backgroundRetrievalEnabled;

    public event Action? StudyChanged;

    public event Action<string>? StatusChanged;

    public async Task WarmupForViewerOpenAsync(CancellationToken cancellationToken = default)
    {
        SeriesRecord? initialSeries = StudyDetails.Series
            .OrderBy(series => series.SeriesNumber)
            .ThenBy(series => series.SeriesDescription)
            .FirstOrDefault();

        if (initialSeries is null)
        {
            return;
        }

        await PrioritizeSeriesAsync(initialSeries.SeriesInstanceUid, GetRepresentativeIndex(initialSeries), 6, 0, cancellationToken);
        await WaitForAnyLocalImageAsync(initialSeries.SeriesInstanceUid, TimeSpan.FromSeconds(8), cancellationToken);

        if (!_isFaulted)
        {
            await PrioritizeAdjacentSeriesAsync(initialSeries.SeriesInstanceUid, 1, cancellationToken);
        }
    }

    public void StartBackgroundRetrieval()
    {
        if (_isFaulted || _backgroundRetrievalEnabled)
        {
            return;
        }

        _backgroundRetrievalEnabled = true;
        PublishStatus("Continuing study download in the background...");
    }

    public Task RequestFocusedImageAsync(string seriesInstanceUid, int focusIndex, int direction = 0, CancellationToken cancellationToken = default)
    {
        if (_isFaulted || string.IsNullOrWhiteSpace(seriesInstanceUid))
        {
            return Task.CompletedTask;
        }

        _ = RequestImageWindowAsync(seriesInstanceUid, focusIndex, 0, direction, cancellationToken, fallbackToSeries: false);
        return Task.CompletedTask;
    }

    public Task PrioritizeSeriesAsync(string seriesInstanceUid, int focusIndex, int radius = 6, int direction = 0, CancellationToken cancellationToken = default)
    {
        if (_isFaulted || string.IsNullOrWhiteSpace(seriesInstanceUid))
        {
            return Task.CompletedTask;
        }

        EnqueuePrioritySeries(seriesInstanceUid);
        _ = RequestImageWindowAsync(seriesInstanceUid, focusIndex, radius, direction, cancellationToken);
        return Task.CompletedTask;
    }

    public Task PrioritizeAdjacentSeriesAsync(string anchorSeriesInstanceUid, int neighborCount = 1, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(anchorSeriesInstanceUid) || neighborCount <= 0)
        {
            return Task.CompletedTask;
        }

        List<SeriesRecord> orderedSeries = GetOrderedSeries();
        int anchorIndex = orderedSeries.FindIndex(series => string.Equals(series.SeriesInstanceUid, anchorSeriesInstanceUid, StringComparison.Ordinal));
        if (anchorIndex < 0)
        {
            return Task.CompletedTask;
        }

        for (int offset = 1; offset <= neighborCount; offset++)
        {
            int nextIndex = anchorIndex + offset;
            int previousIndex = anchorIndex - offset;

            if (nextIndex < orderedSeries.Count)
            {
                SeriesRecord nextSeries = orderedSeries[nextIndex];
                _ = PrioritizeSeriesAsync(nextSeries.SeriesInstanceUid, GetRepresentativeIndex(nextSeries), 2, 0, cancellationToken);
            }

            if (previousIndex >= 0)
            {
                SeriesRecord previousSeries = orderedSeries[previousIndex];
                _ = PrioritizeSeriesAsync(previousSeries.SeriesInstanceUid, GetRepresentativeIndex(previousSeries), 2, 0, cancellationToken);
            }
        }

        return Task.CompletedTask;
    }

    public async Task RefreshStudyAsync(CancellationToken cancellationToken = default)
    {
        StudyDetails? localStudy = await _repository.GetStudyDetailsByStudyInstanceUidAsync(StudyInstanceUid, cancellationToken);
        if (MergeLocalStudy(localStudy))
        {
            StudyChanged?.Invoke();
        }
    }

    private async Task ProcessBackgroundSeriesQueueAsync()
    {
        while (!_disposeCts.IsCancellationRequested)
        {
            try
            {
                if (_isFaulted)
                {
                    break;
                }

                string? nextSeriesUid = await DequeueNextSeriesAsync(_disposeCts.Token);
                if (string.IsNullOrWhiteSpace(nextSeriesUid))
                {
                    await Task.Delay(500, _disposeCts.Token);
                    continue;
                }

                await MoveSeriesAsync(nextSeriesUid, _disposeCts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                PublishStatus($"Background series retrieval failed: {ex.Message}");
                await Task.Delay(750, _disposeCts.Token);
            }
        }
    }

    private async Task<string?> DequeueNextSeriesAsync(CancellationToken cancellationToken)
    {
        if (_isFaulted)
        {
            return null;
        }

        while (_prioritySeriesQueue.Reader.TryRead(out string? queuedSeriesUid))
        {
            lock (_syncRoot)
            {
                _queuedPrioritySeries.Remove(queuedSeriesUid);
            }

            if (!IsSeriesComplete(queuedSeriesUid))
            {
                return queuedSeriesUid;
            }
        }

        if (!_backgroundRetrievalEnabled)
        {
            await Task.Delay(250, cancellationToken);
            return null;
        }

        SeriesRecord? nextIncompleteSeries = StudyDetails.Series
            .Where(series => !IsSeriesComplete(series.SeriesInstanceUid))
            .OrderBy(series => series.SeriesNumber)
            .ThenBy(series => series.SeriesDescription)
            .FirstOrDefault();

        if (nextIncompleteSeries is not null)
        {
            return nextIncompleteSeries.SeriesInstanceUid;
        }

        await _prioritySeriesQueue.Reader.WaitToReadAsync(cancellationToken);
        return null;
    }

    private async Task MoveSeriesAsync(string seriesInstanceUid, CancellationToken cancellationToken)
    {
        if (_isFaulted)
        {
            return;
        }

        lock (_syncRoot)
        {
            if (!_seriesInFlight.Add(seriesInstanceUid))
            {
                return;
            }
        }

        try
        {
            PublishStatus($"Retrieving series {seriesInstanceUid} in background...");
            bool success = await ExecuteMoveSeriesAsync(seriesInstanceUid, cancellationToken);
            if (!success)
            {
                HaltRetrieval(_faultMessage ?? $"Remote retrieval failed for series {seriesInstanceUid}.");
                return;
            }

            await RefreshStudyAsync(cancellationToken);
            PublishStatus($"Finished background retrieval for series {seriesInstanceUid}.");
        }
        finally
        {
            lock (_syncRoot)
            {
                _seriesInFlight.Remove(seriesInstanceUid);
            }
        }
    }

    private async Task RequestImageWindowAsync(string seriesInstanceUid, int focusIndex, int radius, int direction, CancellationToken cancellationToken, bool fallbackToSeries = true)
    {
        if (_isFaulted || !_seriesPreviewByUid.TryGetValue(seriesInstanceUid, out RemoteSeriesPreview? preview) || preview.Images.Count == 0)
        {
            return;
        }

        List<int> indices = BuildPriorityIndices(focusIndex, radius, preview.Images.Count, direction);
        foreach (int index in indices)
        {
            cancellationToken.ThrowIfCancellationRequested();

            SeriesRecord? series = StudyDetails.Series.FirstOrDefault(item => string.Equals(item.SeriesInstanceUid, seriesInstanceUid, StringComparison.Ordinal));
            if (series is null || index < 0 || index >= series.Instances.Count)
            {
                continue;
            }

            InstanceRecord instance = series.Instances[index];
            if (IsLocal(instance))
            {
                continue;
            }

            string imageKey = instance.SopInstanceUid;
            lock (_syncRoot)
            {
                if (!_imageInFlight.Add(imageKey))
                {
                    continue;
                }
            }

            _ = Task.Run(async () =>
            {
                await _imageMoveGate.WaitAsync(_disposeCts.Token);
                try
                {
                    if (_isFaulted)
                    {
                        return;
                    }

                    PublishStatus($"Retrieving image {index + 1} from series {seriesInstanceUid}...");
                    ImageInfo remoteImage = preview.Images[index];
                    bool success = await MoveImageAsync(seriesInstanceUid, remoteImage.SopInstUid, _disposeCts.Token);
                    if (!success)
                    {
                        if (fallbackToSeries)
                        {
                            PublishStatus(_faultMessage ?? $"Priority image retrieval failed for {remoteImage.SopInstUid}. Falling back to full series retrieval.");
                            EnqueuePrioritySeries(seriesInstanceUid);
                        }
                        else
                        {
                            PublishStatus(_faultMessage ?? $"Thumbnail image retrieval failed for {remoteImage.SopInstUid}.");
                        }

                        return;
                    }

                    await RefreshStudyAsync(_disposeCts.Token);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    PublishStatus($"Image-priority retrieval failed: {ex.Message}");
                }
                finally
                {
                    _imageMoveGate.Release();
                    lock (_syncRoot)
                    {
                        _imageInFlight.Remove(imageKey);
                    }
                }
            }, _disposeCts.Token);
        }
    }

    private async Task<bool> MoveImageAsync(string seriesInstanceUid, string sopInstanceUid, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sopInstanceUid))
        {
            return true;
        }

        string? failureMessage = null;

        for (int attempt = 1; attempt <= 2; attempt++)
        {
            bool success = false;
            failureMessage = null;
            DicomCommunicationTrace.Log("DICOM-SCU", $"[{_client.LocalAET.Trim()} -> {_client.RemoteAET.Trim()} {_client.IP}:{_client.Port}] SEND C-MOVE image attempt={attempt} dest={_destinationAeTitle} [study={StudyInstanceUid}, series={seriesInstanceUid}, sopInstance={sopInstanceUid}]");

            var requestClient = DicomClientFactory.Create(_client.IP, _client.Port, false, _client.LocalAET.Trim(), _client.RemoteAET.Trim());
            var request = new DicomCMoveRequest(_destinationAeTitle, StudyInstanceUid, seriesInstanceUid, sopInstanceUid);
            request.OnResponseReceived += (_, response) =>
            {
                DicomCommunicationTrace.Log("DICOM-SCU", $"[{_client.LocalAET.Trim()} -> {_client.RemoteAET.Trim()} {_client.IP}:{_client.Port}] RECV C-MOVE image status={response.Status} [study={StudyInstanceUid}, series={seriesInstanceUid}, sopInstance={sopInstanceUid}]");
                if (response.Status == DicomStatus.Pending)
                {
                    return;
                }

                if (response.Status == DicomStatus.Success)
                {
                    success = true;
                    return;
                }

                failureMessage = $"C-MOVE image failed for {sopInstanceUid} with status {response.Status}.";
            };

            await requestClient.AddRequestAsync(request);
            try
            {
                await requestClient.SendAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                DicomCommunicationTrace.LogException("DICOM-SCU", $"[{_client.LocalAET.Trim()} -> {_client.RemoteAET.Trim()} {_client.IP}:{_client.Port}] C-MOVE image failed", ex);
                failureMessage = $"C-MOVE image failed: {ex.Message}";
            }

            if (success)
            {
                _faultMessage = null;
                return true;
            }

            if (attempt < 2 && IsTransientConnectionFailure(failureMessage))
            {
                PublishStatus($"Retrying image retrieval for {sopInstanceUid} after transient connection refusal...");
                await Task.Delay(250, cancellationToken);
                continue;
            }

            break;
        }

        _faultMessage = failureMessage ?? $"C-MOVE image failed for {sopInstanceUid}: the remote PACS did not confirm the retrieve request.";
        return false;
    }

    private static bool IsTransientConnectionFailure(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        return message.Contains("10061", StringComparison.OrdinalIgnoreCase)
            || message.Contains("connection refused", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Verbindung verweigerte", StringComparison.OrdinalIgnoreCase);
    }

    private async Task WaitForAnyLocalImageAsync(string seriesInstanceUid, TimeSpan timeout, CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RefreshStudyAsync(cancellationToken);

            SeriesRecord? series = StudyDetails.Series.FirstOrDefault(item => string.Equals(item.SeriesInstanceUid, seriesInstanceUid, StringComparison.Ordinal));
            if (series is not null && CountLocalInstances(series) > 0)
            {
                return;
            }

            await Task.Delay(300, cancellationToken);
        }
    }

    private bool MergeLocalStudy(StudyDetails? localStudy)
    {
        bool changed = false;
        Dictionary<string, SeriesRecord> localSeriesByUid = localStudy?.Series.ToDictionary(series => series.SeriesInstanceUid, StringComparer.Ordinal)
            ?? new Dictionary<string, SeriesRecord>(StringComparer.Ordinal);

        foreach (SeriesRecord series in StudyDetails.Series)
        {
            if (!localSeriesByUid.TryGetValue(series.SeriesInstanceUid, out SeriesRecord? localSeries))
            {
                continue;
            }

            Dictionary<string, InstanceRecord> localInstancesByUid = localSeries.Instances.ToDictionary(instance => instance.SopInstanceUid, StringComparer.Ordinal);
            foreach (InstanceRecord instance in series.Instances)
            {
                if (!localInstancesByUid.TryGetValue(instance.SopInstanceUid, out InstanceRecord? localInstance))
                {
                    continue;
                }

                if (!string.Equals(instance.FilePath, localInstance.FilePath, StringComparison.OrdinalIgnoreCase)
                    || instance.InstanceNumber != localInstance.InstanceNumber
                    || instance.FrameCount != localInstance.FrameCount)
                {
                    instance.FilePath = localInstance.FilePath;
                    instance.InstanceNumber = localInstance.InstanceNumber;
                    instance.FrameCount = localInstance.FrameCount;
                    changed = true;
                }
            }

            foreach (InstanceRecord localInstance in localSeries.Instances)
            {
                if (series.Instances.Any(instance => string.Equals(instance.SopInstanceUid, localInstance.SopInstanceUid, StringComparison.Ordinal)))
                {
                    continue;
                }

                series.Instances.Add(CloneInstance(localInstance));
                changed = true;
            }

            series.Instances.Sort(static (left, right) =>
            {
                int byNumber = left.InstanceNumber.CompareTo(right.InstanceNumber);
                return byNumber != 0 ? byNumber : string.Compare(left.SopInstanceUid, right.SopInstanceUid, StringComparison.Ordinal);
            });
        }

        return changed;
    }

    private bool IsSeriesComplete(string seriesInstanceUid)
    {
        SeriesRecord? series = StudyDetails.Series.FirstOrDefault(item => string.Equals(item.SeriesInstanceUid, seriesInstanceUid, StringComparison.Ordinal));
        if (series is null)
        {
            return true;
        }

        int total = Math.Max(series.InstanceCount, series.Instances.Count);
        return total > 0 && CountLocalInstances(series) >= total;
    }

    private void EnqueuePrioritySeries(string seriesInstanceUid)
    {
        lock (_syncRoot)
        {
            if (!_queuedPrioritySeries.Add(seriesInstanceUid))
            {
                return;
            }
        }

        _prioritySeriesQueue.Writer.TryWrite(seriesInstanceUid);
    }

    private static List<int> BuildPriorityIndices(int focusIndex, int radius, int count, int direction)
    {
        var indices = new List<int>();
        if (count <= 0)
        {
            return indices;
        }

        int center = Math.Clamp(focusIndex, 0, count - 1);
        indices.Add(center);

        if (direction > 0)
        {
            int aheadRadius = Math.Max(radius, radius * 2);
            int behindRadius = Math.Max(2, radius / 2);
            for (int offset = 1; offset <= aheadRadius; offset++)
            {
                int next = center + offset;
                if (next < count)
                {
                    indices.Add(next);
                }
            }
            for (int offset = 1; offset <= behindRadius; offset++)
            {
                int previous = center - offset;
                if (previous >= 0)
                {
                    indices.Add(previous);
                }
            }
            return indices;
        }

        if (direction < 0)
        {
            int aheadRadius = Math.Max(radius, radius * 2);
            int behindRadius = Math.Max(2, radius / 2);
            for (int offset = 1; offset <= aheadRadius; offset++)
            {
                int previous = center - offset;
                if (previous >= 0)
                {
                    indices.Add(previous);
                }
            }
            for (int offset = 1; offset <= behindRadius; offset++)
            {
                int next = center + offset;
                if (next < count)
                {
                    indices.Add(next);
                }
            }
            return indices;
        }

        for (int offset = 1; offset <= radius; offset++)
        {
            int next = center + offset;
            int previous = center - offset;
            if (next < count)
            {
                indices.Add(next);
            }
            if (previous >= 0)
            {
                indices.Add(previous);
            }
        }

        return indices;
    }

    private List<SeriesRecord> GetOrderedSeries() =>
        StudyDetails.Series
            .OrderBy(series => series.SeriesNumber)
            .ThenBy(series => series.SeriesDescription)
            .ToList();

    private async Task<bool> ExecuteMoveSeriesAsync(string seriesInstanceUid, CancellationToken cancellationToken)
    {
        bool success = await _client.MoveSeriesAsync(StudyInstanceUid, seriesInstanceUid, _destinationAeTitle, cancellationToken: cancellationToken);
        if (!success)
        {
            _faultMessage = $"C-MOVE series failed for {seriesInstanceUid}. Check the configured Storage SCP destination and remote PACS logs.";
        }

        return success;
    }

    private void HaltRetrieval(string message)
    {
        if (_isFaulted)
        {
            return;
        }

        _isFaulted = true;
        _faultMessage = string.IsNullOrWhiteSpace(message) ? "Remote retrieval failed." : message;
        PublishStatus(_faultMessage.StartsWith("Remote retrieval failed", StringComparison.OrdinalIgnoreCase)
            ? _faultMessage
            : $"Remote retrieval failed: {_faultMessage}");
    }

    private void PublishStatus(string message)
    {
        LastStatus = message;
        StatusChanged?.Invoke(message);
    }

    private static int CountLocalInstances(SeriesRecord series) =>
        series.Instances.Count(IsLocal);

    private static bool IsLocal(InstanceRecord instance) =>
        !string.IsNullOrWhiteSpace(instance.FilePath) && File.Exists(instance.FilePath);

    private static int GetRepresentativeIndex(SeriesRecord series)
    {
        int total = Math.Max(series.InstanceCount, series.Instances.Count);
        return total <= 0 ? 0 : total / 2;
    }

    private static InstanceRecord CloneInstance(InstanceRecord source)
    {
        return new InstanceRecord
        {
            InstanceKey = source.InstanceKey,
            SeriesKey = source.SeriesKey,
            SopInstanceUid = source.SopInstanceUid,
            SopClassUid = source.SopClassUid,
            FilePath = source.FilePath,
            InstanceNumber = source.InstanceNumber,
            FrameCount = source.FrameCount,
        };
    }

    public void Dispose()
    {
        _disposeCts.Cancel();
    }
}
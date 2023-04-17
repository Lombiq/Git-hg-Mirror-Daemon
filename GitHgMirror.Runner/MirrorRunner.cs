using GitHgMirror.CommonTypes;
using GitHgMirror.Runner.Extensions;
using GitHgMirror.Runner.Services;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace GitHgMirror.Runner
{
    public sealed class MirrorRunner : IDisposable
    {
        private readonly TimeSpan _syncWaitTimeout = TimeSpan.FromMinutes(10);
        private readonly MirroringSettings _settings;
        private readonly EventLog _eventLog;
        private readonly ApiService _apiService;

        private readonly ConcurrentQueue<int> _mirrorQueue = new ConcurrentQueue<int>();
        private readonly List<Task> _mirrorTasks = new List<Task>();
        private readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();
        private readonly System.Timers.Timer _pageCountAdjustTimer;

        private int _pageCount;

        public MirrorRunner(MirroringSettings settings, EventLog eventLog)
        {
            _settings = settings;
            _eventLog = eventLog;
            _apiService = new ApiService(settings);

            _pageCountAdjustTimer = new System.Timers.Timer(settings.SecondsBetweenConfigurationCountChecks * 1000);
            _pageCountAdjustTimer.Elapsed += AdjustPageCount;
        }

        public void Start()
        {
            if (!Directory.Exists(_settings.RepositoriesDirectoryPath))
            {
                Directory.CreateDirectory(_settings.RepositoriesDirectoryPath);
            }

            for (int i = 0; i < _settings.MaxDegreeOfParallelism; i++)
            {
                CreateNewMirrorTask();
            }

            // Mirroring will actually start once the page count was adjusted the first time. Note that startup time will
            // increase with the increase of the number mirroring configurations. However this is close to being
            // negligible for unless the amount of pages is big (it takes <1ms with ~100 pages).
            // Using a queue is much more reliable than utilizing QueuedTaskScheduler with as many tasks as pages (that
            // was used before 31.03.2017).
            _pageCountAdjustTimer.Start();
        }

        public void Stop()
        {
            _pageCountAdjustTimer.Stop();
            _cancellationTokenSource.Cancel();
            Task.WhenAll(_mirrorTasks.ToArray()).Wait();
        }

        public void Dispose()
        {
            Stop();
            _pageCountAdjustTimer.Dispose();
            _cancellationTokenSource.Dispose();
        }

        private void AdjustPageCount(object sender, System.Timers.ElapsedEventArgs e)
        {
            try
            {
                var newPageCount = (int)Math.Ceiling(_apiService.Get<int>("Count") / (double)_settings.BatchSize);

                // We only care if the page count increased; if it decreased then processing those queue items will just
                // do nothing (sine they'll fetch empty pages). Removing queue items based on their value would need a
                // lock on the whole queue, thus let's avoid that.
                if (newPageCount <= _pageCount)
                {
                    _eventLog.WriteEntry(
                        "Checked page count whether to adjust it but this wasn't needed (current page count: " +
                        _pageCount.ToString(CultureInfo.InvariantCulture) + ", new page count: " +
                        newPageCount.ToString(CultureInfo.InvariantCulture) + ").");

                    return;
                }

                _cancellationTokenSource.Token.ThrowIfCancellationRequested();

                for (int i = _pageCount; i < newPageCount; i++)
                {
                    _mirrorQueue.Enqueue(i);
                }

                _eventLog.WriteEntry(
                    "Adjusted page count: old page count was " + _pageCount.ToString(CultureInfo.InvariantCulture) +
                    ", new page count is " + newPageCount.ToString(CultureInfo.InvariantCulture) + ".");

                _pageCount = newPageCount;
            }
            catch (Exception ex) when (!ex.IsFatalOrCancellation())
            {
                // Swallowing non-fatal exceptions like when the page count can't be retrieved.
                _eventLog.WriteEntry(
                    "Adjusting page counts failed and will be re-tried next time. Exception: " + ex,
                    EventLogEntryType.Error);
            }
        }

        private void CreateNewMirrorTask() =>
            _mirrorTasks.Add(Task.Run(
                async () =>
                {
                    var syncWaitTimeout = TimeSpan.Zero;

                    // Checking for new queue items until canceled.
                    while (!await _cancellationTokenSource.Token.WaitAsync(syncWaitTimeout))
                    {
                        if (_mirrorQueue.TryDequeue(out var pageNum))
                        {
                            syncWaitTimeout = _syncWaitTimeout;
                            _eventLog.WriteEntry("Starting processing page " + pageNum + ".");

                            if (pageNum < _pageCount)
                            {
                                try
                                {
                                    var skip = pageNum * _settings.BatchSize;
                                    var configurations = _apiService
                                        .Get<List<MirroringConfiguration>>("?skip=" + skip + "&take=" + _settings.BatchSize);

                                    _eventLog.WriteEntry(
                                        "Page " + pageNum + " has " + configurations.Count +
                                        " mirroring configurations. Starting mirrorings.");

                                    for (int c = 0; c < configurations.Count; c++)
                                    {
                                        const string ReportPath = "Report";
                                        using var mirror = new Mirror(_eventLog);
                                        _cancellationTokenSource.Token.ThrowIfCancellationRequested();

                                        var configuration = configurations[c];

                                        if (!Mirror.IsCloned(configuration, _settings))
                                        {
                                            _apiService.Post(ReportPath, new MirroringStatusReport
                                            {
                                                ConfigurationId = configuration.Id,
                                                Status = MirroringStatus.Cloning,
                                            });
                                        }

                                        try
                                        {
                                            Debug.WriteLine("Mirroring page " + pageNum + ": " + configuration);
                                            _eventLog.WriteEntry(
                                                "Starting to execute mirroring \"" + configuration + "\" on page " + pageNum + ".");

                                            // Hg and git push commands randomly hang without any apparent reason (when
                                            // just pushing small payloads). To prevent such a hang causing repositories
                                            // stop syncing and Tasks being blocked forever there is a timeout for
                                            // mirroring. Such a kill timeout is not a nice solution but the hangs are
                                            // unexplainable.
                                            var mirrorExecutionTask =
                                                Task.Run(() => mirror.MirrorRepositories(configuration, _settings, _cancellationTokenSource.Token));
                                            // Necessary for the timeout value.
#pragma warning disable VSTHRD103 // Call async methods when in an async method
                                            if (mirrorExecutionTask.Wait(_settings.MirroringTimoutSeconds * 1000))
#pragma warning restore VSTHRD103 // Call async methods when in an async method
                                            {
                                                _apiService.Post(ReportPath, new MirroringStatusReport
                                                {
                                                    ConfigurationId = configuration.Id,
                                                    Status = MirroringStatus.Syncing,
                                                });
                                            }
                                            else
                                            {
                                                _apiService.Post(ReportPath, new MirroringStatusReport
                                                {
                                                    ConfigurationId = configuration.Id,
                                                    Status = MirroringStatus.Failed,
                                                    Message =
                                                        "Mirroring didn't finish after " + _settings.MirroringTimoutSeconds +
                                                        "s so was terminated. Possible causes include one of the repos " +
                                                        "being too slow to access (could be a temporary issue with the " +
                                                        "hosting provider) or simply being too big.",
                                                });

                                                _eventLog.WriteEntry(
                                                    $"Mirroring the hg repository {configuration.HgCloneUri} and git " +
                                                    $"repository {configuration.GitCloneUri} in the direction {configuration.Direction} " +
                                                    $"has hung and was forcefully terminated after {_settings.MirroringTimoutSeconds}s.",
                                                    EventLogEntryType.Error);
                                            }
                                        }
                                        catch (AggregateException ex)
                                        {
                                            if (!(ex.InnerException is MirroringException mirroringException)) throw;

                                            _eventLog.WriteEntry(
                                                $"An exception occurred while processing a mirroring between the hg repository " +
                                                $"{configuration.HgCloneUri} and git repository {configuration.GitCloneUri}" +
                                                $" in the direction {configuration.Direction}." +
                                                $"{Environment.NewLine}Exception: {mirroringException}",
                                                EventLogEntryType.Error);

                                            _apiService.Post(ReportPath, new MirroringStatusReport
                                            {
                                                ConfigurationId = configuration.Id,
                                                Status = MirroringStatus.Failed,
                                                Message = mirroringException.InnerException.Message,
                                            });
                                        }

                                        _eventLog.WriteEntry(
                                            "Finished executing mirroring \"" + configuration + "\" on page " + pageNum + ".");
                                    }

                                    if (!configurations.Any())
                                    {
                                        // Waiting only half of the count check time to not to wait excessively. This
                                        // way there is a good chance that the next such execution will be skipped
                                        // completely, due to the page count being adjusted in the meantime.
                                        var waitMilliseconds = _settings.SecondsBetweenConfigurationCountChecks / 2 * 1000;

                                        _eventLog.WriteEntry(
                                            "Page " + pageNum + " didn't contain any mirroring configurations, though it should have. Waiting " +
                                            waitMilliseconds.ToString(CultureInfo.InvariantCulture) + "ms.");

                                        // If there is no configuration on this page then that's due to some obscure
                                        // issue: this shouldn't happen because empty pages can only exist if mirroring
                                        // configs were removed, but then this whole block should be skipped (that is,
                                        // if the page count was since updated, which is done every minute). However
                                        // we've seen the page fetch request succeed with an empty result even when
                                        // responding with HTTP 500...
                                        await Task.Delay(waitMilliseconds);
                                    }
                                }
                                catch (Exception ex) when (!ex.IsFatalOrCancellation())
                                {
                                    if ((ex as AggregateException)?.InnerException.IsFatalOrCancellation() == false)
                                    {
                                        const int waitMilliseconds = 30000;

                                        _eventLog.WriteEntry(
                                            "Unhandled exception while running mirrorings: " + ex +
                                            " Waiting " + waitMilliseconds + "ms before trying the next page in this Task.",
                                            EventLogEntryType.Error);

                                        await Task.Delay(waitMilliseconds);
                                    }
                                }
                            }
                            else
                            {
                                _eventLog.WriteEntry(
                                    "Page " + pageNum + " is an empty page (due to configs having been removed). Nothing to do.");
                            }

                            _eventLog.WriteEntry("Finished processing page " + pageNum + ".");

                            _mirrorQueue.Enqueue(pageNum);
                        }
                        else
                        {
                            // If there is no queue item present, wait 10s, then re-try.
                            syncWaitTimeout = TimeSpan.FromSeconds(10);
                        }
                    }
                },
                _cancellationTokenSource.Token));
    }
}

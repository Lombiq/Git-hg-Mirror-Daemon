using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GitHgMirror.CommonTypes;
using GitHgMirror.Runner.Services;
using Newtonsoft.Json;

namespace GitHgMirror.Runner
{
    public class MirrorRunner
    {
        private readonly MirroringSettings _settings;
        private readonly EventLog _eventLog;
        private readonly ApiService _apiService;

        private readonly ConcurrentQueue<int> _mirrorQueue = new ConcurrentQueue<int>();
        private int _pageCount = 0;
        private readonly List<Task> _mirrorTasks = new List<Task>();
        private readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();
        private readonly System.Timers.Timer _pageCountAdjustTimer;


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

            // Mirroring wil actually start once the page count was adjusted the first time. Note that startup time will
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


        private void AdjustPageCount(object sender, System.Timers.ElapsedEventArgs e)
        {
            try
            {
                var newPageCount = FetchConfigurationPageCount();

                // We only care if the page count increased; if it decreased then processing those queue items will just
                // do nothing (sine they'll fetch empty pages).
                if (newPageCount <= _pageCount) return;

                for (int i = _pageCount; i < newPageCount; i++)
                {
                    _mirrorQueue.Enqueue(i);
                }

                _pageCount = newPageCount;
            }
            catch (Exception ex) when (!ex.IsFatal())
            {
                // Swallowing non-fatal exceptions like when the page count can't be retrieved.
                _eventLog.WriteEntry(
                    "Adjusting page counts failed and will be re-tried next time. Exception: " + ex, 
                    EventLogEntryType.Error);
            }
        }

        private void CreateNewMirrorTask()
        {
            _mirrorTasks.Add(Task.Run(async () =>
            {
                // Checking for new queue items until cancelled.
                while (!_cancellationTokenSource.IsCancellationRequested)
                {
                    int pageNum;

                    if (_mirrorQueue.TryDequeue(out pageNum))
                    {
                        try
                        {
                            var configurations = FetchConfigurations(pageNum);

                            for (int c = 0; c < configurations.Count; c++)
                            {
                                using (var mirror = new Mirror(_eventLog))
                                {
                                    _cancellationTokenSource.Token.ThrowIfCancellationRequested();

                                    var configuration = configurations[c];

                                    if (!mirror.IsCloned(configuration, _settings))
                                    {
                                        _apiService.Post("Report", new MirroringStatusReport
                                        {
                                            ConfigurationId = configuration.Id,
                                            Status = MirroringStatus.Cloning
                                        });
                                    }

                                    try
                                    {
                                        Debug.WriteLine("Mirroring page " + pageNum + ": " + configuration);

                                        // Hg and git push command randomly hang without any apparent reason (when just
                                        // pushing small payloads). To prevent such a hang causing repositories stop
                                        // syncing and Tasks being blocked forever there is a timout for mirroring.
                                        // Such a kill timeout is not a nice solution but the hangs are unexplainable.
                                        var mirrorExecutionTask = 
                                            Task.Run(() =>  mirror.MirrorRepositories(configuration, _settings));
                                        var mirroringTimoutSeconds = 10 * 60 * 60; // Ten hours.
                                        if (mirrorExecutionTask.Wait(mirroringTimoutSeconds * 1000))
                                        {
                                            _apiService.Post("Report", new MirroringStatusReport
                                            {
                                                ConfigurationId = configuration.Id,
                                                Status = MirroringStatus.Syncing
                                            });
                                        }
                                        else
                                        {
                                            _eventLog.WriteEntry(string.Format(
                                                "Mirroring the hg repository {0} and git repository {1} in the direction {2} has hung and was forcefully terminated after {3}s.",
                                                configuration.HgCloneUri, configuration.GitCloneUri, configuration.Direction, mirroringTimoutSeconds),
                                                EventLogEntryType.Error);
                                        }
                                    }
                                    catch (AggregateException ex)
                                    {
                                        var mirroringException = (MirroringException)ex.InnerException;

                                        _eventLog.WriteEntry(string.Format(
                                            "An exception occured while processing a mirroring between the hg repository {0} and git repository {1} in the direction {2}." +
                                            Environment.NewLine + "Exception: {3}",
                                            configuration.HgCloneUri, configuration.GitCloneUri, configuration.Direction, mirroringException),
                                            EventLogEntryType.Error);

                                        _apiService.Post("Report", new MirroringStatusReport
                                        {
                                            ConfigurationId = configuration.Id,
                                            Status = MirroringStatus.Failed,
                                            Message = mirroringException.InnerException.Message
                                        });
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            if (ex.IsFatal() || ex is OperationCanceledException) throw;
                            _eventLog.WriteEntry(
                                "Unhandled exception while running mirrorings: " + ex.ToString(),
                                EventLogEntryType.Error);
                        }

                        _mirrorQueue.Enqueue(pageNum);
                    }
                    else
                    {
                        // If there is no queue item present, wait 10s, then re-try.
                        await Task.Delay(10000);
                    }
                }

            }, _cancellationTokenSource.Token));
        }

        private int FetchConfigurationPageCount()
        {
            return (int)Math.Ceiling(_apiService.Get<int>("Count") / (double)_settings.BatchSize);
        }

        /// <summary>
        /// Page is zero-indexed.
        /// </summary>
        private List<MirroringConfiguration> FetchConfigurations(int page)
        {
            var skip = page * _settings.BatchSize;
            return _apiService.Get<List<MirroringConfiguration>>("?skip=" + skip + "&take=" + _settings.BatchSize);
        }
    }
}
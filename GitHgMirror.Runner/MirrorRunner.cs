using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GitHgMirror.CommonTypes;
using Newtonsoft.Json;

namespace GitHgMirror.Runner
{
    public class MirrorRunner
    {
        private readonly Settings _settings;
        private readonly EventLog _eventLog;
        private readonly ApiService _apiService;

        private readonly List<Task> _mirrorTasks = new List<Task>();
        private readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();
        private readonly System.Timers.Timer _tasksRefreshTimer;


        public MirrorRunner(Settings settings, EventLog eventLog)
        {
            _settings = settings;
            _eventLog = eventLog;
            _apiService = new ApiService(settings);

            _tasksRefreshTimer = new System.Timers.Timer(settings.SecondsBetweenConfigurationCountChecks * 1000);
            _tasksRefreshTimer.Elapsed += AdjustTasksToPageCount;
        }


        public void Start()
        {
            if (!Directory.Exists(_settings.RepositoriesDirectoryPath))
            {
                Directory.CreateDirectory(_settings.RepositoriesDirectoryPath);
            }

            for (int i = 0; i < FetchConfigurationPageCount(); i++)
            {
                CreateNewTaskForPage(i);
            }

            _tasksRefreshTimer.Start();
        }

        public void Stop()
        {
            _tasksRefreshTimer.Stop();
            _cancellationTokenSource.Cancel();
        }


        private void AdjustTasksToPageCount(object sender, System.Timers.ElapsedEventArgs e)
        {
            var pageCount = FetchConfigurationPageCount();

            // We only care if the page count increased; if it decresed there are tasks just periodically checking whether their page has
            // any items.
            if (pageCount <= _mirrorTasks.Count) return;

            for (int i = _mirrorTasks.Count; i < pageCount; i++)
            {
                CreateNewTaskForPage(i);
            }
        }

        private void CreateNewTaskForPage(int page)
        {
            _mirrorTasks.Add(Task.Factory.StartNew(async pageObject =>
            {
                var pageNum = (int)pageObject;

                // Refreshing will run until cancelled
                while (!_cancellationTokenSource.IsCancellationRequested)
                {
                    try
                    {
                        var configurations = FetchConfigurations(pageNum);

                        for (int c = 0; c < configurations.Count; c++)
                        {
                            using (var mirror = new Mirror(_settings, _eventLog))
                            {
                                if (_cancellationTokenSource.IsCancellationRequested)
                                {
                                    _cancellationTokenSource.Token.ThrowIfCancellationRequested();
                                }

                                var configuration = configurations[c];

                                if (!mirror.IsCloned(configuration))
                                {
                                    _apiService.Post("Report", new MirroringStatusReport
                                    {
                                        ConfigurationId = configuration.Id,
                                        Status = MirroringStatus.Cloning
                                    });
                                }

                                try
                                {
                                    mirror.MirrorRepositories(configuration);

                                    _apiService.Post("Report", new MirroringStatusReport
                                    {
                                        ConfigurationId = configuration.Id,
                                        Status = MirroringStatus.Syncing
                                    });
                                }
                                catch (MirroringException ex)
                                {
                                    _eventLog.WriteEntry(string.Format("An exception occured while processing a mirroring between the hg repository {0} and git repository {1} in the direction {2}." + Environment.NewLine + "Exception: {3}", configuration.HgCloneUri, configuration.GitCloneUri, configuration.Direction, ex), EventLogEntryType.Error);
                                    _apiService.Post("Report", new MirroringStatusReport
                                    {
                                        ConfigurationId = configuration.Id,
                                        Status = MirroringStatus.Failed,
                                        Message = ex.Message
                                    });
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        if (ex.IsFatal() || ex is MirroringException || ex is OperationCanceledException) throw;
                        _eventLog.WriteEntry("Unhandled exception while running mirrorings: " + ex, EventLogEntryType.Error);
                    }

                    await Task.Delay(600000, _cancellationTokenSource.Token); // Wait a bit between loops
                }

                _cancellationTokenSource.Token.ThrowIfCancellationRequested();
            }, page, _cancellationTokenSource.Token));
        }

        private int FetchConfigurationPageCount()
        {
            return (int)Math.Ceiling((double)_apiService.Get<int>("Count") / (double)_settings.BatchSize);
        }

        /// <summary>
        /// Page is zero-indexed
        /// </summary>
        private List<MirroringConfiguration> FetchConfigurations(int page)
        {
            var skip = page * _settings.BatchSize;
            return _apiService.Get<List<MirroringConfiguration>>("?skip=" + skip + "&take=" + _settings.BatchSize);
        }
    }
}
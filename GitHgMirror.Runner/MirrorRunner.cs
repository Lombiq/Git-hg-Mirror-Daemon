using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Schedulers;
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

        private readonly QueuedTaskScheduler _taskScheduler;
        private readonly List<Task> _mirrorTasks = new List<Task>();
        private readonly object _mirrorTasksLock = new object();
        private readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();
        private readonly System.Timers.Timer _tasksRefreshTimer;


        public MirrorRunner(MirroringSettings settings, EventLog eventLog)
        {
            _settings = settings;
            _eventLog = eventLog;
            _apiService = new ApiService(settings);

            _taskScheduler = new QueuedTaskScheduler(TaskScheduler.Default, settings.MaxDegreeOfParallelism);


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

            var mirrorTaskCount = 0;
            lock (_mirrorTasksLock)
            {
                mirrorTaskCount = _mirrorTasks.Count;
            }

            // We only care if the page count increased; if it decreased there are tasks just periodically checking 
            // whether their page has any items.
            if (pageCount <= mirrorTaskCount) return;

            for (int i = mirrorTaskCount; i < pageCount; i++)
            {
                CreateNewTaskForPage(i);
            }
        }

        private void CreateNewTaskForPage(int page)
        {
            lock (_mirrorTasksLock)
            {
                var newTask = Task.Factory.StartNew(pageObject =>
                {
                    var pageNum = (int)pageObject;

                    try
                    {
                        var configurations = FetchConfigurations(pageNum);

                        for (int c = 0; c < configurations.Count; c++)
                        {
                            using (var mirror = new Mirror(_eventLog))
                            {
                                if (_cancellationTokenSource.IsCancellationRequested)
                                {
                                    _cancellationTokenSource.Token.ThrowIfCancellationRequested();
                                }

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

                                    mirror.MirrorRepositories(configuration, _settings);

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

                    _cancellationTokenSource.Token.ThrowIfCancellationRequested();

                    CreateNewTaskForPage(pageNum);
                }, page, _cancellationTokenSource.Token, TaskCreationOptions.PreferFairness, _taskScheduler);


                if (_mirrorTasks.Count >= page + 1)
                {
                    _mirrorTasks[page] = newTask;
                }
                else
                {
                    _mirrorTasks.Add(newTask);
                }
            }
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
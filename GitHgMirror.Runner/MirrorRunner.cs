using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GitHgMirror.CommonTypes;

namespace GitHgMirror.Runner
{
    public class MirrorRunner
    {
        private readonly Settings _settings;
        private readonly EventLog _eventLog;

        private readonly List<Task> _mirrorTasks = new List<Task>();
        private readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();
        private readonly System.Timers.Timer _tasksRefreshTimer;


        public MirrorRunner(Settings settings, EventLog eventLog)
        {
            _settings = settings;
            _eventLog = eventLog;

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

                var mirror = new Mirror(_settings, _eventLog);

                // Refreshing will run until cancelled
                while (!_cancellationTokenSource.IsCancellationRequested)
                {
                    var configurations = FetchConfigurations(pageNum);

                    for (int c = 0; c < configurations.Count; c++)
                    {
                        if (_cancellationTokenSource.IsCancellationRequested)
                        {
                            mirror.Dispose();
                            _cancellationTokenSource.Token.ThrowIfCancellationRequested();
                        }

                        var configuration = configurations[c];

                        try
                        {
                            mirror.MirrorRepositories(configuration);
                        }
                        catch (MirroringException ex)
                        {
                            _eventLog.WriteEntry(String.Format("An exception occured while processing a mirroring between the hg repository {0} and git repository {1} in the direction {2}." + Environment.NewLine + "Exception: {3}", configuration.HgCloneUri, configuration.GitCloneUri, configuration.Direction, ex), EventLogEntryType.Error);
                        }
                    }

                    await Task.Delay(30000, _cancellationTokenSource.Token); // Wait a bit between loops
                }

                mirror.Dispose();
                _cancellationTokenSource.Token.ThrowIfCancellationRequested();
            }, page, _cancellationTokenSource.Token));
        }

        private int FetchConfigurationPageCount()
        {
            var count = 3; // Will come from webservice
            return (int)Math.Ceiling((double)count / (double)_settings.BatchSize);
        }

        /// <summary>
        /// Page is zero-indexed
        /// </summary>
        private List<MirroringConfiguration> FetchConfigurations(int page)
        {
            return new[]
            {
                new MirroringConfiguration
                {
                    HgCloneUri = new Uri("https://bitbucket.org/lehoczky_zoltan/hg-test"),
                    GitCloneUri = new Uri("git://github.com/Piedone/git-test"),
                    Direction = MirroringDirection.GitToHg
                },
                new MirroringConfiguration
                {
                    HgCloneUri = new Uri("https://bitbucket.org/Lombiq/orchard-hg"),
                    GitCloneUri = new Uri("git+https://git01.codeplex.com/orchard.git"),
                    Direction = MirroringDirection.GitToHg
                },
                new MirroringConfiguration
                {
                    HgCloneUri = new Uri("https://bitbucket.org/Lombiq/orchard-mvc-mini-profiler-module-hg"),
                    GitCloneUri = new Uri("git+https://git01.codeplex.com/orchardprofiler.git"),
                    Direction = MirroringDirection.GitToHg
                }
            }.Skip(page * _settings.BatchSize).Take(_settings.BatchSize).ToList();
        }
    }
}
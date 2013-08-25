using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GitHgMirrorCommonTypes;

namespace GitHgMirrorRunner
{
    public class Runner
    {
        private readonly Settings _settings;
        private readonly EventLog _eventLog;

        private readonly List<Task> _mirrorTasks = new List<Task>();
        private readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();


        public Runner(Settings settings, EventLog eventLog)
        {
            _settings = settings;
            _eventLog = eventLog;
        }


        public void Start()
        {
            if (!Directory.Exists(_settings.RepositoriesDirectoryPath))
            {
                Directory.CreateDirectory(_settings.RepositoriesDirectoryPath);
            }
            

            var currentPage = 1;
            _mirrorTasks.Add(Task.Factory.StartNew(page =>
                {
                    var mirror = new Mirror(_settings, _eventLog);

                    var testConfig = new MirroringConfiguration
                    {
                        HgCloneUri = new Uri("https://bitbucket.org/lehoczky_zoltan/hg-test"),
                        GitCloneUri = new Uri("git://github.com/Piedone/git-test"),
                        Direction = MirroringDirection.GitToHg
                    };

                    var configurations = new[] { testConfig };

                    foreach (var configuration in configurations)
                    {
                        if (_cancellationTokenSource.IsCancellationRequested) return;

                        try
                        {
                            mirror.MirrorRepositories(testConfig);
                        }
                        catch (Exception ex)
                        {
                            _eventLog.WriteEntry(String.Format("An exception occured while processing a mirroring between the hg repository {0} and git repository {1} in the direction {2}." + Environment.NewLine + "Exception: {3}", configuration.HgCloneUri, configuration.GitCloneUri, configuration.Direction, ex), EventLogEntryType.Error);
                        }
                    }
                }, currentPage, _cancellationTokenSource.Token));
        }

        public void Stop()
        {
            _cancellationTokenSource.Cancel();
        }
    }
}
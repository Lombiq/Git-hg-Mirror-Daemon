using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

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

            var mirror = new Mirror(_settings, _eventLog);

            var hgCloneUri = new Uri("https://bitbucket.org/lehoczky_zoltan/hg-test");
            var gitCloneUri = new Uri("git://github.com/Piedone/git-test");
            var direction = MirrorDirection.GitToHg;
            try
            {
                mirror.MirrorRepositories(hgCloneUri, gitCloneUri, direction);
            }
            catch (Exception ex)
            {
                _eventLog.WriteEntry(String.Format("An exception occured while processing a mirroring between the hg repository {0} and git repository {1} in the direction {2}." + Environment.NewLine + "Exception: {3}", hgCloneUri, gitCloneUri, direction, ex), EventLogEntryType.Error); 
            }

            for (int i = 0; i < _settings.ParallelisationDegree; i++)
            {
                _mirrorTasks.Add(Task.Run(() =>
                    {
                        if (_cancellationTokenSource.IsCancellationRequested)
                        {
                            
                        }
                    }, _cancellationTokenSource.Token));
            }
        }

        public void Stop()
        {
            _cancellationTokenSource.Cancel();
        }
    }
}
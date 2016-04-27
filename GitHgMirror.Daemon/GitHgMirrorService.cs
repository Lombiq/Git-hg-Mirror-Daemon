using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GitHgMirror.Runner;

namespace GitHgMirror.Daemon
{
    public partial class GitHgMirrorService : ServiceBase
    {
        private MirroringSettings _settings;
        private MirrorRunner _runner;
        private UntouchedRepositoriesCleaner _cleaner;
        private ManualResetEvent _waitHandle = new ManualResetEvent(false);


        public GitHgMirrorService()
        {
            InitializeComponent();
        }


        protected override void OnStart(string[] args)
        {
            serviceEventLog.MaximumKilobytes = 65536;
            serviceEventLog.WriteEntry("GitHgMirrorDaemon started.");

            _settings = new MirroringSettings
            {
                ApiEndpointUrl = new Uri("http://githgmirror.com/api/GitHgMirror.Common/Mirrorings"),
                ApiPassword = "Fsdfp342LE8%!",
                RepositoriesDirectoryPath = @"C:\GitHgMirror\Repositories",
                MaxDegreeOfParallelism = 6,
                // This way no sync waits for another one to finish in a batch but they run independently of each other,
                // the throughput only being limited by MaxDegreeOfParallelism.
                BatchSize = 1
            };

            var startTimer = new System.Timers.Timer(10000);
            startTimer.Elapsed += timer_Elapsed;
            startTimer.Enabled = true;

            _cleaner = new UntouchedRepositoriesCleaner(_settings, serviceEventLog);
            var cleanerTimer = new System.Timers.Timer(3600000 * 2); // Two hours
            cleanerTimer.Elapsed += (sender, e) =>
                {
                    _cleaner.Clean();
                };
            cleanerTimer.Enabled = true;
        }

        protected override void OnStop()
        {
            serviceEventLog.WriteEntry("GitHgMirrorDaemon stopped. Stopping mirroring.");

            _runner.Stop();
            _waitHandle.Set();

            serviceEventLog.WriteEntry("Mirroring stopped.");
        }


        void timer_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            ((System.Timers.Timer)sender).Enabled = false;

            serviceEventLog.WriteEntry("Starting mirroring.");

            _runner = new MirrorRunner(_settings, serviceEventLog);

            _runner.Start();

            serviceEventLog.WriteEntry("Mirroring started.");

            _waitHandle.WaitOne();
        }
    }
}

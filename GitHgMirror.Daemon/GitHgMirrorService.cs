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
        private MirrorRunner _runner;
        private ManualResetEvent _waitHandle = new ManualResetEvent(false);


        public GitHgMirrorService()
        {
            InitializeComponent();
        }


        protected override void OnStart(string[] args)
        {
            serviceEventLog.WriteEntry("GitHgMirrorDaemon started.");

            var timer = new System.Timers.Timer(10000);
            timer.Elapsed += timer_Elapsed;
            timer.Enabled = true;
        }

        protected override void OnStop()
        {
            serviceEventLog.WriteEntry("GitHgMirrorDaemon stopped. Stopping mirroring.");

            _runner.Stop();

            serviceEventLog.WriteEntry("Mirroring stopped.");
        }


        void timer_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            ((System.Timers.Timer)sender).Enabled = false;

            serviceEventLog.WriteEntry("Starting mirroring.");

            var settings = new Settings
            {
                ApiEndpointUrl = new Uri("http://githgmirror.com/api/GitHgMirror.Common/Mirrorings"),
                ApiPassword = "Fsdfp342LE8%!",
                RepositoriesDirectoryPath = @"C:\GitHgMirror\Repositories"
            };

            _runner = new MirrorRunner(settings, serviceEventLog);

            _runner.Start();

            serviceEventLog.WriteEntry("Mirroring started.");

            _waitHandle.WaitOne();
        }
    }
}

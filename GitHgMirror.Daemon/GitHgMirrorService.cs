using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Configuration;
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
            if (!EventLog.Exists("Git-hg Mirror Daemon"))
            {
                EventLog.CreateEventSource(new EventSourceCreationData("GitHgMirror.Daemon", "Git-hg Mirror Daemon"));
            }

            // Keep in mind that the event log consumes memory so unless you kep this well beyond what you can spare the
            // server will run out of RAM.
            serviceEventLog.MaximumKilobytes = 65536;
            serviceEventLog.WriteEntry("GitHgMirrorDaemon started.");

            _settings = new MirroringSettings
            {
                ApiEndpointUrl = new Uri(ConfigurationManager.AppSettings[Constants.ApiEndpointUrl]),
                ApiPassword = ConfigurationManager.ConnectionStrings[Constants.ApiPasswordKey]?.ConnectionString ?? string.Empty,
                RepositoriesDirectoryPath = ConfigurationManager.AppSettings[Constants.RepositoriesDirectoryPath],
                MaxDegreeOfParallelism = int.Parse(ConfigurationManager.AppSettings[Constants.MaxDegreeOfParallelism]),
                BatchSize = int.Parse(ConfigurationManager.AppSettings[Constants.BatchSize])
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

            var started = false;
            while (!started)
            {
                try
                {
                    _runner.Start();

                    serviceEventLog.WriteEntry("Mirroring started.");

                    _waitHandle.WaitOne();
                    started = true;
                }
                catch (Exception ex)
                {
                    serviceEventLog.WriteEntry(
                        "Starting mirroring failed with the following exception: " + ex.ToString() +
                        Environment.NewLine +
                        "A new start will be attempted in 30s.",
                        EventLogEntryType.Error);

                    Thread.Sleep(30000);
                }
            }
        }
    }
}

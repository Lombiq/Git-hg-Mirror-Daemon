using GitHgMirror.Runner;
using System;
using System.Configuration;
using System.Diagnostics;
using System.Globalization;
using System.ServiceProcess;
using System.Threading;

namespace GitHgMirror.Daemon
{
    public partial class GitHgMirrorService : ServiceBase
    {
        private readonly ManualResetEvent _waitHandle = new ManualResetEvent(false);
        private readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();

        private MirroringSettings _settings;
        private MirrorRunner _runner;
        private UntouchedRepositoriesCleaner _cleaner;
        private System.Timers.Timer _startTimer;
        private System.Timers.Timer _cleanTimer;



        public GitHgMirrorService() => InitializeComponent();


        protected override void OnStart(string[] args)
        {
            if (!EventLog.Exists("Git-hg Mirror Daemon"))
            {
                EventLog.CreateEventSource(new EventSourceCreationData("GitHgMirror.Daemon", "Git-hg Mirror Daemon"));
            }

            // Keep in mind that the event log consumes memory so unless you keep this well beyond what you can spare
            // the server will run out of RAM.
            serviceEventLog.MaximumKilobytes = 196608; // 192MB
            serviceEventLog.WriteEntry("GitHgMirrorDaemon started.");

            _settings = new MirroringSettings
            {
                ApiEndpointUrl = new Uri(ConfigurationManager.AppSettings[Constants.ApiEndpointUrl]),
                ApiPassword = ConfigurationManager.ConnectionStrings[Constants.ApiPasswordKey]?.ConnectionString ?? string.Empty,
                RepositoriesDirectoryPath = ConfigurationManager.AppSettings[Constants.RepositoriesDirectoryPath],
                BatchSize = int.Parse(ConfigurationManager.AppSettings[Constants.BatchSize], CultureInfo.InvariantCulture),
            };

            _startTimer = new System.Timers.Timer(10000);
            _startTimer.Elapsed += StartTimerElapsed;
            _startTimer.Enabled = true;

            _cleaner = new UntouchedRepositoriesCleaner(_settings, serviceEventLog);
            _cleanTimer = new System.Timers.Timer(3_600_000 * 2); // Two hours
            _cleanTimer.Elapsed += (sender, e) => _cleaner.Clean(_cancellationTokenSource.Token);
            _cleanTimer.Enabled = true;
        }

        protected override void OnStop()
        {
            serviceEventLog.WriteEntry("GitHgMirrorDaemon stopped. Stopping mirroring.");

            _cancellationTokenSource.Cancel();
            _runner.Stop();
            _waitHandle.Set();

            serviceEventLog.WriteEntry("Mirroring stopped.");
        }


        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }

            base.Dispose(disposing);

            _cancellationTokenSource.Dispose();
            _waitHandle.Dispose();
            _runner?.Dispose();
            _startTimer?.Dispose();
            _cleanTimer?.Dispose();
        }


        private void StartTimerElapsed(object sender, System.Timers.ElapsedEventArgs e)
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
                        "Starting mirroring failed with the following exception: " + ex +
                        Environment.NewLine +
                        "A new start will be attempted in 30s.",
                        EventLogEntryType.Error);

                    Thread.Sleep(30000);
                }
            }
        }
    }
}

using GitHgMirror.Runner;
using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;

namespace GitHgMirror.Tester
{
    class Program
    {
        private static ManualResetEvent _waitHandle = new ManualResetEvent(false);


        static void Main(string[] args)
        {
            // If true then a unique event log will be used for all copies of this executable. This helps if you want
            // to run the app in multiple instances from source and not let the events show up across copies.
            var useUniqueEventlog = true;

            var eventLogName = "Git-hg Mirror Daemon";
            var eventSourceName = "GitHgMirror.Tester";

            if (useUniqueEventlog)
            {
                var suffix = "-" + Assembly.GetExecutingAssembly().Location.GetHashCode();
                // "Only the first eight characters of a custom log name are significant" so we need to make the name
                // unique withing 8 characters.
                eventLogName = "GHM" + suffix;
                eventSourceName += suffix;
            }

            if (!EventLog.Exists(eventLogName))
            {
                EventLog.CreateEventSource(new EventSourceCreationData(eventSourceName, eventLogName));
            }

            using (var eventLog = new EventLog(eventLogName, ".", eventSourceName))
            {
                eventLog.EnableRaisingEvents = true;

                eventLog.EntryWritten += (sender, e) =>
                    {
                        Console.WriteLine(e.Entry.Message);
                    };


                var settings = new MirroringSettings
                {
                    ApiEndpointUrl = new Uri("http://githgmirror.com.127-0-0-1.org.uk/api/GitHgMirror.Common/Mirrorings"),
                    ApiPassword = "Fsdfp342LE8%!",
                    RepositoriesDirectoryPath = @"C:\GitHgMirror\Repos",
                    MaxDegreeOfParallelism = 1,
                    BatchSize = 1
                };

                // Uncomment if you want to also test repo cleaning.
                //new UntouchedRepositoriesCleaner(settings, eventLog).Clean(new CancellationTokenSource().Token);

                var runner = new MirrorRunner(settings, eventLog);

                // On exit with Ctrl+C
                Console.CancelKeyPress += (sender, e) =>
                    {
                        runner.Stop();
                    };

                runner.Start();

                _waitHandle.WaitOne();
            }
        }
    }
}

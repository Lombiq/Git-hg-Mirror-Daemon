using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GitHgMirror.Runner;

namespace GitHgMirror.Tester
{
    class Program
    {
        private static ManualResetEvent _waitHandle = new ManualResetEvent(false);

        static void Main(string[] args)
        {
            if (!EventLog.Exists("Git-hg Mirror Daemon"))
            {
                EventLog.CreateEventSource(new EventSourceCreationData("GitHgMirror.Tester", "Git-hg Mirror Daemon")); 
            }

            using (var eventLog = new EventLog("Git-hg Mirror Daemon", ".", "GitHgMirror.Tester"))
            {
                eventLog.EnableRaisingEvents = true;

                eventLog.EntryWritten += (sender, e) =>
                    {
                        Console.WriteLine(e.Entry.Message);
                    };


                var settings = new Settings
                {
                    ApiEndpointUrl = new Uri("http://githgmirror.com.127-0-0-1.org.uk/api/GitHgMirror.Common/Mirrorings"),
                    ApiPassword = "Fsdfp342LE8%!",
                    RepositoriesDirectoryPath = @"C:\GitHgMirror\Repositories",
                    BatchSize = 1
                };

                new UntouchedRepositoriesCleaner(settings, eventLog).Clean();

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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GitHgMirrorRunner;

namespace GitHgMirrorTester
{
    class Program
    {
        private static ManualResetEvent _waitHandle = new ManualResetEvent(false);

        static void Main(string[] args)
        {
            using (var eventLog = new System.Diagnostics.EventLog("Git-hg Mirror Daemon", ".", "GitHgMirrorTester"))
            {
                eventLog.EnableRaisingEvents = true;

                eventLog.EntryWritten += (sender, e) =>
                    {
                        Console.WriteLine(e.Entry.Message);
                    };


                var settings = new Settings
                {
                    RepositoriesDirectoryPath = @"D:\GitHgMirror\Repositories"
                };

                var runner = new Runner(settings, eventLog);

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

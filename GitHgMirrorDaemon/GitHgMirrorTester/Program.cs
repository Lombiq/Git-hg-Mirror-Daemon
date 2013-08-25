using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using GitHgMirrorRunner;

namespace GitHgMirrorTester
{
    class Program
    {
        static void Main(string[] args)
        {
            using (var eventLog = new System.Diagnostics.EventLog())
            {
                eventLog.EntryWritten += (sender, e) =>
                    {
                        Console.WriteLine(e.Entry.Message);
                    };

                var settings = new Settings
                {
                };
                var runner = new Runner(settings, eventLog);
                runner.Start();

                Console.ReadKey();
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GitHgMirror.Runner
{
    public class UntouchedRepositoriesCleaner
    {
        private readonly Settings _settings;
        private readonly EventLog _eventLog;


        public UntouchedRepositoriesCleaner(Settings settings, EventLog eventLog)
        {
            _settings = settings;
            _eventLog = eventLog;
        }


        public void Clean()
        {
            _eventLog.WriteEntry("Starting cleaning untouched repositories");

            var count = 0;
            if (Directory.Exists(_settings.RepositoriesDirectoryPath))
            {
                foreach (var letterDirectory in Directory.EnumerateDirectories(_settings.RepositoriesDirectoryPath))
                {
                    foreach (var repositoryDirectory in Directory.EnumerateDirectories(letterDirectory))
                    {
                        if (Directory.GetLastAccessTimeUtc(repositoryDirectory) < DateTime.UtcNow.Subtract(new TimeSpan(24, 0, 0)))
                        {
                            Directory.Delete(repositoryDirectory, true);
                            count++;
                        }
                    }
                } 
            }

            _eventLog.WriteEntry("Finished cleaning untouched repositories, " + count + " folders removed");
        }
    }
}

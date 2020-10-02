using GitHgMirror.Runner.Helpers;
using GitHgMirror.Runner.Services;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace GitHgMirror.Runner
{
    public class UntouchedRepositoriesCleaner
    {
        private readonly MirroringSettings _settings;
        private readonly EventLog _eventLog;


        public UntouchedRepositoriesCleaner(MirroringSettings settings, EventLog eventLog)
        {
            _settings = settings;
            _eventLog = eventLog;
        }


        public void Clean(CancellationToken cancellationToken)
        {
            _eventLog.WriteEntry("Starting cleaning untouched repositories.");

            var count = 0;
            if (Directory.Exists(_settings.RepositoriesDirectoryPath))
            {
                foreach (var letterDirectory in Directory.EnumerateDirectories(_settings.RepositoriesDirectoryPath))
                {
                    foreach (var repositoryDirectory in Directory.EnumerateDirectories(letterDirectory))
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        if (RepositoryInfoFileHelper.GetLastUpdatedDateTimeUtc(repositoryDirectory) < DateTime.UtcNow.Subtract(new TimeSpan(24, 0, 0)) &&
                            !File.Exists(Mirror.GetRepositoryLockFilePath(repositoryDirectory)))
                        {
                            _eventLog.WriteEntry("Attempting to remove untouched repository folder: " + repositoryDirectory);

                            try
                            {
                                try
                                {
                                    Directory.Delete(repositoryDirectory, true);
                                }
                                catch (UnauthorizedAccessException)
                                {
                                    var killResult = DirectoryUtil.KillProcessesLockingFiles(repositoryDirectory);

                                    _eventLog.WriteEntry(
                                        "Removing the untouched repository folder: " + repositoryDirectory +
                                        " initially failed, so trying to kill processes that are locking files in it and " +
                                        "setting all files not to be read-only resulted in the following." +
                                        " Processes killed: " + (killResult.KilledProcesseFileNames.Any() ? string.Join(", ", killResult.KilledProcesseFileNames) : "no processes") +
                                        " Read-only files: " + (killResult.ReadOnlyFilePaths.Any() ? string.Join(", ", killResult.ReadOnlyFilePaths) : "no files"),
                                        EventLogEntryType.Warning);

                                    Directory.Delete(repositoryDirectory, true);
                                }

                                RepositoryInfoFileHelper.DeleteFileIfExists(repositoryDirectory);

                                _eventLog.WriteEntry("Removed untouched repository folder: " + repositoryDirectory);
                            }
                            catch (Exception ex) when (!ex.IsFatalOrCancellation())
                            {
                                _eventLog.WriteEntry(
                                    "Removing the untouched repository folder \"" + repositoryDirectory +
                                    "\" failed with the following exception: " + ex,
                                    EventLogEntryType.Error);
                            }

                            count++;
                        }
                    }
                }
            }

            _eventLog.WriteEntry("Finished cleaning untouched repositories, " + count + " folders removed.");
        }
    }
}

using GitHgMirror.NonAnalyzed;
using System.Collections.Generic;
using System.IO;

namespace GitHgMirror.Runner.Helpers
{
    internal static class DirectoryUtil
    {
        public static LockingProcessKillResult KillProcessesLockingFiles(string directoryPath)
        {
            var killedProcesseFileNames = new List<string>();
            var readOnlyFilePaths = new List<string>();

            var files = Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories);
            foreach (var file in files)
            {
                var lockingProcesses = FileUtil.WhoIsLocking(file);
                foreach (var process in lockingProcesses)
                {
                    killedProcesseFileNames.Add(process.MainModule.FileName);
                    process.Kill();
                }

                if (File.GetAttributes(file).HasFlag(FileAttributes.ReadOnly))
                {
                    readOnlyFilePaths.Add(file);
                    File.SetAttributes(file, FileAttributes.Normal);
                }
            }

            return new LockingProcessKillResult
            {
                KilledProcesseFileNames = killedProcesseFileNames,
                ReadOnlyFilePaths = readOnlyFilePaths,
            };
        }


        public class LockingProcessKillResult
        {
            public IEnumerable<string> KilledProcesseFileNames { get; set; }
            public IEnumerable<string> ReadOnlyFilePaths { get; set; }
        }
    }
}

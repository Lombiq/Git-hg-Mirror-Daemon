using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GitHgMirror.Runner.Helpers
{
    public static class RepositoryInfoFileHelper
    {
        public static void CreateOrUpdateFile(string cloneDirectoryPath, string mirroringDescriptor)
        {
            // Not placing the debug info file into the clone directory because that would bother Mercurial.
            File.WriteAllText(
                GetFilePath(cloneDirectoryPath),
                mirroringDescriptor + Environment.NewLine + DateTime.UtcNow.ToString());
        }

        public static DateTime GetLastUpdatedDateTimeUtc(string cloneDirectoryPath)
        {
            var filePath = GetFilePath(cloneDirectoryPath);

            if (!File.Exists(filePath))
            {
                return DateTime.MinValue;
            }

            var fileLines = File.ReadAllLines(filePath);

            if (fileLines.Length < 2)
            {
                return DateTime.MinValue;
            }

            return DateTime.Parse(fileLines[1]);
        }


        private static string GetFilePath(string cloneDirectoryPath)
        {
            return cloneDirectoryPath + "-info.txt";
        }
    }
}

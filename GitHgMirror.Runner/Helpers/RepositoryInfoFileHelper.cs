using System;
using System.Globalization;
using System.IO;

namespace GitHgMirror.Runner.Helpers
{
    public static class RepositoryInfoFileHelper
    {
        public static void CreateOrUpdateFile(string cloneDirectoryPath, string mirroringDescriptor) =>
            // Not placing the debug info file into the clone directory because that would bother Mercurial.
            File.WriteAllText(
                GetFilePath(cloneDirectoryPath),
                mirroringDescriptor + Environment.NewLine + DateTime.UtcNow.ToString(CultureInfo.InvariantCulture));

        public static DateTime GetLastUpdatedDateTimeUtc(string cloneDirectoryPath)
        {
            var filePath = GetFilePath(cloneDirectoryPath);

            if (!File.Exists(filePath))
            {
                return DateTime.MinValue;
            }

            var fileLines = File.ReadAllLines(filePath);

            return fileLines.Length < 2 ? DateTime.MinValue : DateTime.Parse(fileLines[1], CultureInfo.InvariantCulture);
        }

        public static void DeleteFileIfExists(string cloneDirectoryPath)
        {
            var filePath = GetFilePath(cloneDirectoryPath);

            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }


        private static string GetFilePath(string cloneDirectoryPath) => cloneDirectoryPath + "-info.txt";
    }
}

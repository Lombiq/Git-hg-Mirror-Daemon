using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GitHgMirror.Daemon
{
    public static class Constants
    {
        /// <summary>
        /// Configuation key for the API endpoint URL used to access the GitHgMirror API when fetching mirroring 
        /// configurations.
        /// </summary>
        public static string ApiEndpointUrl = "GitHgMirror.Daemon.ApiEndpointUrl";

        /// <summary>
        /// Configuration key for password used to access the GitHgMirror API when fetching mirroring configurations.
        /// </summary>
        public static string ApiPasswordKey = "GitHgMirror.Daemon.ApiPassword";

        /// <summary>
        /// Configuration key for the path pointing to the directory where mirrored repositories are stored.
        /// </summary>
        public static string RepositoriesDirectoryPath = "GitHgMirror.Daemon.RepositoriesDirectoryPath";

        /// <summary>
        /// Configuration key for the maximal degree of parallelism mirrorings are executed.
        /// </summary>
        public static string MaxDegreeOfParallelism = "GitHgMirror.Daemon.MaxDegreeOfParallelism";

        /// <summary>
        /// Configuration key for how many mirrorings are executed in the same task after each other.
        /// </summary>
        public static string BatchSize = "GitHgMirror.Daemon.BatchSize";
    }
}

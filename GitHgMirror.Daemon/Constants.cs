namespace GitHgMirror.Daemon
{
    public static class Constants
    {
        /// <summary>
        /// Configuration key for the API endpoint URL used to access the GitHgMirror API when fetching mirroring
        /// configurations.
        /// </summary>
        public const string ApiEndpointUrl = "GitHgMirror.Daemon.ApiEndpointUrl";

        /// <summary>
        /// Configuration key for password used to access the GitHgMirror API when fetching mirroring configurations.
        /// </summary>
        public const string ApiPasswordKey = "GitHgMirror.Daemon.ApiPassword";

        /// <summary>
        /// Configuration key for the path pointing to the directory where mirrored repositories are stored.
        /// </summary>
        public const string RepositoriesDirectoryPath = "GitHgMirror.Daemon.RepositoriesDirectoryPath";

        /// <summary>
        /// Configuration key for the maximal degree of parallelism mirrorings are executed.
        /// </summary>
        public const string MaxDegreeOfParallelism = "GitHgMirror.Daemon.MaxDegreeOfParallelism";

        /// <summary>
        /// Configuration key for how many mirrorings are executed in the same task after each other.
        /// </summary>
        public const string BatchSize = "GitHgMirror.Daemon.BatchSize";
    }
}

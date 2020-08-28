namespace GitHgMirror.Runner
{
    public static class CommandExceptionExtensions
    {
        /// <summary>
        /// Checks if the error is something like in the example that can mean that due to slow network we can't clone 
        /// or pull a large repo.
        /// </summary>
        /// <example>
        /// transaction abort!
        /// rollback completed
        /// abort: stream ended unexpectedly (got 12300 bytes, expected 14312)
        /// </example>
        public static bool IsHgConnectionTerminatedError(this CommandException ex)
        {
            var error = ex.Error;

            return
                error.Contains("abort: stream ended unexpectedly (got ") ||
                error.Contains("abort: connection ended unexpectedly") ||
                error.Contains("abort: error: A connection attempt failed because the connected party did not properly respond");
        }

        /// <summary>
        /// Git communicates some messages via the error stream, checking them here.
        /// </summary>
        public static bool IsGitExceptionRealError(this CommandException ex)
        {
            return
                // If there is nothing to push git will return this message in the error stream.
                !ex.Error.Contains("Everything up-to-date") &&
                // A new branch was added.
                !ex.Error.Contains("* [new branch]") &&
                // Branches were deleted in git.
                !ex.Error.Contains("[deleted]") &&
                // A new tag was added.
                !ex.Error.Contains("* [new tag]") &&
                // The branch head was moved (shown during push).
                !(ex.Error.Contains("..") && ex.Error.Contains(" -> ")) &&
                // The branch head was moved (shown during fetch).
                !(ex.Error.Contains("* branch") && ex.Error.Contains(" -> ")) &&
                // Git GC is running.
                !ex.Error.Contains("Auto packing the repository in background for optimum performance.") &&
                // An existing tag was updated.
                !ex.Error.Contains("[tag update]");
        }
    }
}

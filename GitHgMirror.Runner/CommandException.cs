using System;
using System.Diagnostics.CodeAnalysis;

namespace GitHgMirror.Runner
{
    [SuppressMessage("Design", "CA1032:Implement standard exception constructors", Justification = "This exception is used in very particular cases.")]
    [SuppressMessage("Usage", "CA2237:Mark ISerializable types with serializable", Justification = "Doesn't need to be serializable.")]
    [SuppressMessage("Major Code Smell", "S3925:\"ISerializable\" should be implemented correctly", Justification = "Doesn't need to be serializable.")]
    public class CommandException : Exception
    {
        public string Output { get; private set; }
        public string Error { get; private set; }


        public CommandException(string message, string output, string error)
            : base(message)
        {
            Output = output;
            Error = error;
        }

        public CommandException(string message, string output, string error, Exception innerException)
            : base(message, innerException)
        {
            Output = output;
            Error = error;
        }


        public override string ToString() =>
            typeof(CommandException).FullName + ": " +
            Message + Environment.NewLine +
            "Output: " + Output + Environment.NewLine +
            "Error: " + Error + Environment.NewLine;

        /// <summary>
        /// Checks if the error is something like in the example that can mean that due to slow network we can't clone
        /// or pull a large repo.
        /// </summary>
        /// <example>
        /// <code>
        /// transaction abort!
        /// rollback completed
        /// abort: stream ended unexpectedly (got 12300 bytes, expected 14312)
        /// </code>
        /// </example>
        public bool IsHgConnectionTerminatedError()
        {
            var error = Error;

            return
                error.Contains("abort: stream ended unexpectedly (got ") ||
                error.Contains("abort: connection ended unexpectedly") ||
                error.Contains("abort: error: A connection attempt failed because the connected party did not properly respond");
        }

        /// <summary>
        /// Git communicates some messages via the error stream, checking them here.
        /// </summary>
        public bool IsGitExceptionRealError() =>
            // If there is nothing to push git will return this message in the error stream.
            // It's readable.
#pragma warning disable S1067 // Expressions should not be too complex
            !Error.Contains("Everything up-to-date") &&
            // A new branch was added.
            !Error.Contains("* [new branch]") &&
            // Branches were deleted in git.
            !Error.Contains("[deleted]") &&
            // A new tag was added.
            !Error.Contains("* [new tag]") &&
            // The branch head was moved (shown during push).
            !(Error.Contains("..") && Error.Contains(" -> ")) &&
            // The branch head was moved (shown during fetch).
            !(Error.Contains("* branch") && Error.Contains(" -> ")) &&
            // Git GC is running.
            !Error.Contains("Auto packing the repository in background for optimum performance.") &&
            // An existing tag was updated.
            !Error.Contains("[tag update]");
#pragma warning restore S1067 // Expressions should not be too complex
    }
}

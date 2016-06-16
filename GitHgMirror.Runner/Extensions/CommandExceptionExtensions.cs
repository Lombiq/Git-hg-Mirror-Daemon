using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

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
        public static bool IsHgConnectionTerminatedError(this CommandException exception)
        {
            var error = exception.Error;

            return 
                error.Contains("abort: stream ended unexpectedly (got ") ||
                error.Contains("abort: connection ended unexpectedly");
        }
    }
}

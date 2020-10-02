using System;

namespace GitHgMirror.Runner
{
    internal static class ExceptionExtensions
    {
        // Taken from http://www.codeproject.com/Articles/7557/Exception-Handling-in-C-with-the-quot-Do-Not-Catch and
        // refactored.
        public static bool IsFatal(this Exception ex)
        {
            // It's readable.
#pragma warning disable S1067 // Expressions should not be too complex
            if (ex is OutOfMemoryException
                || ex is AppDomainUnloadedException
                || ex is BadImageFormatException
                || ex is CannotUnloadAppDomainException
                || ex is InvalidProgramException
                || ex is System.Threading.ThreadAbortException) return true;
#pragma warning restore S1067 // Expressions should not be too complex

            return false;
        }

        public static bool IsFatalOrCancellation(this Exception ex) => ex.IsFatal() || ex is OperationCanceledException;
    }
}

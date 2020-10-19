using System;

namespace GitHgMirror.Runner
{
    internal static class ExceptionExtensions
    {
        // Taken from http://www.codeproject.com/Articles/7557/Exception-Handling-in-C-with-the-quot-Do-Not-Catch and
        // refactored.
        public static bool IsFatal(this Exception ex) =>
            ex is OutOfMemoryException
                || ex is AppDomainUnloadedException
                || ex is BadImageFormatException
                || ex is CannotUnloadAppDomainException
                || ex is InvalidProgramException
                || ex is System.Threading.ThreadAbortException;

        public static bool IsFatalOrCancellation(this Exception ex) => ex.IsFatal() || ex is OperationCanceledException;
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GitHgMirror.Runner
{
    static class ExceptionExtensions
    {
        // Taken from http://www.codeproject.com/Articles/7557/Exception-Handling-in-C-with-the-quot-Do-Not-Catch and refactored
        public static bool IsFatal(this Exception ex)
        {
            if (ex is OutOfMemoryException
                || ex is AppDomainUnloadedException
                || ex is BadImageFormatException
                || ex is CannotUnloadAppDomainException
                || ex is InvalidProgramException
                || ex is System.Threading.ThreadAbortException) return true;

            return false;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GitHgMirror.Runner
{
    public class MirroringException : AggregateException
    {
        public MirroringException(string message)
            : base(message)
        {
        }

        public MirroringException(string message, params Exception[] innerExceptions)
            : base(message, innerExceptions)
        {
        }


        public override string ToString()
        {
            return
                typeof(MirroringException).FullName + ": " + Message + Environment.NewLine +
                StackTrace + Environment.NewLine +
                string.Join(Environment.NewLine, InnerExceptions.Select(exception => Environment.NewLine + "---> " + exception.ToString()));
        }
    }
}

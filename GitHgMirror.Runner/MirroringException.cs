using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GitHgMirror.Runner
{
    public class MirroringException : Exception
    {
        public MirroringException(string message)
            : base(message)
        {
        }

        public MirroringException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}

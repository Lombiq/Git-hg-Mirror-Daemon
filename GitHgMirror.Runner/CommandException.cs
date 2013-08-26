using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GitHgMirror.Runner
{
    public class CommandException : Exception
    {
        public string Output { get; private set; }
        public string Error { get; private set; }


        public CommandException(string message, string output, string error) : base(message)
        {
            Output = output;
            Error = error;
        }

        public CommandException(string message, string output, string error, Exception innerException) : base(message, innerException)
        {
            Output = output;
            Error = error;
        }


        public override string ToString()
        {
            return
                Message + Environment.NewLine +
                "Output: " + Output + Environment.NewLine +
                "Error: " + Error + Environment.NewLine;
        }
    }
}

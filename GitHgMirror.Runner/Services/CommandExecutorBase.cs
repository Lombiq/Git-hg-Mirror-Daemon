using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GitHgMirror.Runner.Services
{
    internal abstract class CommandExecutorBase : IDisposable
    {
        protected readonly EventLog _eventLog;

        protected readonly CommandRunner _commandRunner = new CommandRunner();


        protected CommandExecutorBase(EventLog eventLog)
        {
            _eventLog = eventLog;
        }


        public virtual void Dispose()
        {
            _commandRunner.Dispose();
        }


        protected string RunCommandAndLogOutput(string command)
        {
            var output = _commandRunner.RunCommand(command);

            Debug.WriteLine(output);

            // A string written to the event log cannot exceed supposedly 32766, actually fewer characters (Yes! There
            // will be a Win32Exception thrown otherwise.) so going with the magic number of 31878 here, see: 
            // https://social.msdn.microsoft.com/Forums/en-US/b7d8e3c6-3607-4a5c-aca2-f828000d25be/not-able-to-write-log-messages-in-event-log-on-windows-2008-server?forum=netfx64bit
            if (output.Length > 31878)
            {
                var truncatedMessage =
                    "... " +
                    Environment.NewLine +
                    "The output exceeds 31878 characters, thus can't be written to the event log and was truncated.";

                _eventLog.WriteEntry(output.Substring(0, 31878 - truncatedMessage.Length) + truncatedMessage);
            }
            else _eventLog.WriteEntry(output);

            return output;
        }

        protected void CdDirectory(string directoryPath)
        {
            RunCommandAndLogOutput("cd " + directoryPath);
        }
    }
}

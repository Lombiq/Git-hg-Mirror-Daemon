using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GitHgMirror.Runner
{
    public class CommandRunner : IDisposable
    {
        private Process _process;
        private string _error = string.Empty;


        /// <summary>
        /// Runs a command line command through the Windows command line
        /// </summary>
        /// <param name="command">The command string</param>
        /// <returns>Output</returns>
        /// <exception cref="CommandException">Thrown if the command fails</exception>
        public string RunCommand(string command)
        {
            StartProcessIfNotRunning();


            _process.StandardInput.WriteLine(command);

            var output = ReadOutputUntilBlankLine();

            if (!String.IsNullOrEmpty(_error))
            {
                var error = _error;
                _error = string.Empty;
                throw new CommandException(String.Format("Executing command \"{0}\" failed with the output \"{1}\" and error \"{2}\".", command, output, error), output, error);
            }

            return output;
        }

        public void Dispose()
        {
            if (_process == null || _process.HasExited) return;

            _process.Dispose();
            //_hgProcess.Kill(); // Not sure this is a good thing although this should only happen if there is no operation running anyway.
        }


        private void StartProcessIfNotRunning()
        {
            if (_process != null && !_process.HasExited) return;

            _process = new Process();

            _process.StartInfo.UseShellExecute = false;
            _process.StartInfo.RedirectStandardOutput = true;
            _process.StartInfo.RedirectStandardError = true;
            _process.StartInfo.RedirectStandardInput = true;
            _process.StartInfo.FileName = "cmd";
            _process.StartInfo.WorkingDirectory = @"C:\";

            _process.ErrorDataReceived += (sender, e) =>
            {
                _error += e.Data;
            };

            _process.Start();

            _process.BeginErrorReadLine();

            ReadOutputUntilBlankLine();
        }

        private string ReadOutputUntil(Predicate<List<string>> stopCondition)
        {
            var lines = new List<string>();

            while (!stopCondition(lines))
            {
                lines.Add(_process.StandardOutput.ReadLine());
            }

            return String.Join(Environment.NewLine, lines);
        }

        private string ReadOutputUntilBlankLine()
        {
            return ReadOutputUntil(lines => lines.Count > 0 && lines.Last() == string.Empty);
        }
    }
}

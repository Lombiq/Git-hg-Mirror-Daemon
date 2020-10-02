using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace GitHgMirror.Runner.Services
{
    public sealed class CommandRunner : IDisposable
    {
        private static readonly ManualResetEvent _waitHandle = new ManualResetEvent(false);

        private Process _process;
        private string _error = string.Empty;


        /// <summary>
        /// Runs a command line command through the Windows command line.
        /// </summary>
        /// <param name="command">The command string.</param>
        /// <returns>Output of the command.</returns>
        /// <exception cref="CommandException">Thrown if the command fails.</exception>
        public string RunCommand(string command)
        {
            StartProcessIfNotRunning();

            _error = string.Empty;

            _process.StandardInput.WriteLine(command);

            var output = ReadOutputUntilBlankLine();

            // Waiting for error lines to appear. Sometimes if a command fails it won't be included in this error output
            // but rather it will appear in later outputs for some reason. That's why we wait a bit here.
            _waitHandle.WaitOne(1000);
            _waitHandle.Reset();

            if (!string.IsNullOrWhiteSpace(_error))
            {
                // Waiting for more error lines to appear.
                for (int i = 0; i < 10; i++)
                {
                    _waitHandle.WaitOne(1000);
                    _waitHandle.Reset();
                }

                var error = _error;
                _error = string.Empty;
                throw new CommandException(
                    // False alarm.
#pragma warning disable S2302 // "nameof" should be used
                    $"Executing command \"{command}\" failed with the output \"{output}\" and error \"{error}\".", output, error);
#pragma warning restore S2302 // "nameof" should be used
            }

            return output;
        }

        public void Dispose()
        {
            if (_process == null || _process.HasExited) return;

            _process.Kill();
            _process.Dispose();
            _process = null;
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
                _error += Environment.NewLine + e.Data;
                _waitHandle.Set();
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

            return string.Join(Environment.NewLine, lines);
        }

        private string ReadOutputUntilBlankLine() => ReadOutputUntil(lines => lines.Count > 0 && string.IsNullOrEmpty(lines.Last()));
    }
}

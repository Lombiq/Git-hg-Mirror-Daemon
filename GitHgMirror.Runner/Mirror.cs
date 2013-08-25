
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GitHgMirror.CommonTypes;

namespace GitHgMirror.Runner
{
    class Mirror
    {
        private readonly Settings _settings;
        private readonly EventLog _eventLog;
        private Process _hgProcess;


        public Mirror(Settings settings, EventLog eventLog)
        {
            _settings = settings;
            _eventLog = eventLog;
        }


        public void MirrorRepositories(MirroringConfiguration configuration)
        {
            StartHgProcessIfNotRunning();


            var cloneDirectoryPath = Path.Combine(_settings.RepositoriesDirectoryPath, ToDirectoryName(configuration.HgCloneUri) + " - " + ToDirectoryName(configuration.GitCloneUri));

            if (!Directory.Exists(cloneDirectoryPath))
            {
                Directory.CreateDirectory(cloneDirectoryPath);
                _hgProcess.StandardInput.WriteLine("hg clone " + configuration.HgCloneUri + " \"" + cloneDirectoryPath + "\"");
            }

            _hgProcess.StandardInput.WriteLine("cd \"" + cloneDirectoryPath + "\"");
            _hgProcess.StandardInput.WriteLine(Path.GetPathRoot(cloneDirectoryPath).Replace("\\", string.Empty)); // Changing directory to other drive if necessary
            if (configuration.Direction == MirroringDirection.GitToHg)
            {
                _hgProcess.StandardInput.WriteLine("hg pull " + configuration.GitCloneUri);
                _hgProcess.StandardInput.WriteLine("hg push " + configuration.HgCloneUri);
            }

            _hgProcess.WaitForExit();
        }

        private void StartHgProcessIfNotRunning()
        {
            if (_hgProcess != null) return;

            _hgProcess = new Process();

            _hgProcess.StartInfo.UseShellExecute = false;
            _hgProcess.StartInfo.RedirectStandardOutput = true;
            _hgProcess.StartInfo.RedirectStandardError = true;
            _hgProcess.StartInfo.RedirectStandardInput = true;
            _hgProcess.StartInfo.FileName = "cmd";
            _hgProcess.StartInfo.WorkingDirectory = @"C:\";

            _hgProcess.OutputDataReceived += (sender, e) =>
            {
                _eventLog.WriteEntry(e.Data);
            };
            _hgProcess.ErrorDataReceived += (sender, e) =>
            {
                _eventLog.WriteEntry(e.Data);
                throw new MirroringException("Error from the hg process: " + e.Data);
            };
            _hgProcess.Exited += (sender, e) =>
            {
                _eventLog.WriteEntry("exit");
            };

            _hgProcess.Start();

            _hgProcess.BeginOutputReadLine();
            _hgProcess.BeginErrorReadLine();
        }


        private static string ToDirectoryName(Uri cloneUri)
        {
            return cloneUri.Host.Replace("_", "__") + "_" + cloneUri.PathAndQuery.Replace("_", "__").Replace('/', '_');
        }
    }
}

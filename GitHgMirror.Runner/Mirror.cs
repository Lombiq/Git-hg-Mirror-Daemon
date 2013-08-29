
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
    class Mirror : IDisposable
    {
        private readonly Settings _settings;
        private readonly EventLog _eventLog;

        private readonly CommandRunner _commandRunner = new CommandRunner();


        public Mirror(Settings settings, EventLog eventLog)
        {
            _settings = settings;
            _eventLog = eventLog;
        }


        public void MirrorRepositories(MirroringConfiguration configuration)
        {
            try
            {
                var repositoryDirectoryName = ToDirectoryName(configuration.HgCloneUri) + " - " + ToDirectoryName(configuration.GitCloneUri);
                var cloneDirectoryParentPath = Path.Combine(_settings.RepositoriesDirectoryPath, repositoryDirectoryName[0].ToString()); // A subfolder per clone dir start letter
                if (!Directory.Exists(cloneDirectoryParentPath))
                {
                    Directory.CreateDirectory(cloneDirectoryParentPath);
                }
                var cloneDirectoryPath = Path.Combine(cloneDirectoryParentPath, repositoryDirectoryName);
                var quotedHgCloneUrl = configuration.HgCloneUri.ToString().EncloseInQuotes();
                var quotedGitCloneUrl = configuration.GitCloneUri.ToString().EncloseInQuotes();


                try
                {
                    // This is a workaround for this bug: https://bitbucket.org/durin42/hg-git/issue/49/pull-results-in-keyerror
                    // Cloning from git works but pulling a modified git repo fails, so we have to re-clone everytime...
                    if (configuration.Direction == MirroringDirection.GitToHg)
                    {
                        if (Directory.Exists(cloneDirectoryPath))
                        {
                            Directory.Delete(cloneDirectoryPath, true); 
                        }

                        RunCommandAndLogOutput("hg clone --noupdate " + quotedGitCloneUrl + " " + cloneDirectoryPath.EncloseInQuotes() + "");
                        RunCommandAndLogOutput("cd \"" + cloneDirectoryPath + "\"");
                        RunCommandAndLogOutput(Path.GetPathRoot(cloneDirectoryPath).Replace("\\", string.Empty)); // Changing directory to other drive if necessary
                        RunCommandAndLogOutput("hg push " + quotedHgCloneUrl);

                        return;
                    }

                    if (!Directory.Exists(cloneDirectoryPath))
                    {
                        Directory.CreateDirectory(cloneDirectoryPath);
                        RunCommandAndLogOutput("hg clone --noupdate " + quotedHgCloneUrl + " " + cloneDirectoryPath.EncloseInQuotes() + "");
                    }
                    else
                    {
                        Directory.SetLastAccessTimeUtc(cloneDirectoryPath, DateTime.UtcNow);
                    }
                }
                catch (CommandException ex)
                {
                    throw new MirroringException(String.Format("An exception occured while cloning the repositories {0} and {1} in direction {2}. Cloning will re-started next time.", configuration.HgCloneUri, configuration.GitCloneUri, configuration.Direction), ex);
                }


                RunCommandAndLogOutput("cd \"" + cloneDirectoryPath + "\"");
                RunCommandAndLogOutput(Path.GetPathRoot(cloneDirectoryPath).Replace("\\", string.Empty)); // Changing directory to other drive if necessary

                switch (configuration.Direction)
                {
                    case MirroringDirection.GitToHg:
                        RunCommandAndLogOutput("hg pull " + quotedGitCloneUrl);
                        RunCommandAndLogOutput("hg push " + quotedHgCloneUrl);
                        break;
                    case MirroringDirection.HgToGit:
                        RunCommandAndLogOutput("hg pull " + quotedHgCloneUrl);
                        RunCommandAndLogOutput("hg push " + quotedGitCloneUrl);
                        break;
                    case MirroringDirection.TwoWay:
                        RunCommandAndLogOutput("hg pull " + quotedGitCloneUrl);
                        RunCommandAndLogOutput("hg pull " + quotedHgCloneUrl);
                        RunCommandAndLogOutput("hg push " + quotedGitCloneUrl);
                        RunCommandAndLogOutput("hg push " + quotedHgCloneUrl);
                        break;
                }
            }
            catch (CommandException ex)
            {
                throw new MirroringException(String.Format("An exception occured while mirroring the repositories {0} and {1} in direction {2},", configuration.HgCloneUri, configuration.GitCloneUri, configuration.Direction), ex);
            }
        }

        public bool IsCloned(MirroringConfiguration configuration)
        {
            var repositoryDirectoryName = ToDirectoryName(configuration.HgCloneUri) + " - " + ToDirectoryName(configuration.GitCloneUri);
            var cloneDirectoryParentPath = Path.Combine(_settings.RepositoriesDirectoryPath, repositoryDirectoryName[0].ToString()); // A subfolder per clone dir start letter
            var cloneDirectoryPath = Path.Combine(cloneDirectoryParentPath, repositoryDirectoryName);
            return Directory.Exists(cloneDirectoryPath);
        }

        public void Dispose()
        {
            _commandRunner.Dispose();
        }


        private void RunCommandAndLogOutput(string command)
        {
            _eventLog.WriteEntry(_commandRunner.RunCommand(command));
        }


        private static string ToDirectoryName(Uri cloneUri)
        {
            return cloneUri.Host.Replace("_", "__") + "_" + cloneUri.PathAndQuery.Replace("_", "__").Replace('/', '_');
        }
    }
}

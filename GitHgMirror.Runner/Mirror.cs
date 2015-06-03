
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
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
            var repositoryDirectoryName = GetCloneDirectoryName(configuration);
            var cloneDirectoryParentPath = Path.Combine(_settings.RepositoriesDirectoryPath, repositoryDirectoryName[0].ToString()); // A subfolder per clone dir start letter
            var cloneDirectoryPath = Path.Combine(cloneDirectoryParentPath, repositoryDirectoryName);

            try
            {
                if (!Directory.Exists(cloneDirectoryParentPath))
                {
                    Directory.CreateDirectory(cloneDirectoryParentPath);
                }
                var quotedHgCloneUrl = configuration.HgCloneUri.ToString().EncloseInQuotes();
                var quotedGitCloneUrl = configuration.GitCloneUri.ToString().EncloseInQuotes();


                if (!Directory.Exists(cloneDirectoryPath))
                {
                    Directory.CreateDirectory(cloneDirectoryPath);
                    RunCommandAndLogOutput("hg clone --noupdate " + quotedHgCloneUrl + " " + cloneDirectoryPath.EncloseInQuotes() + "");
                }
                else
                {
                    Directory.SetLastAccessTimeUtc(cloneDirectoryPath, DateTime.UtcNow);
                }


                RunCommandAndLogOutput("cd \"" + cloneDirectoryPath + "\"");
                RunCommandAndLogOutput(Path.GetPathRoot(cloneDirectoryPath).Replace("\\", string.Empty)); // Changing directory to other drive if necessary

                switch (configuration.Direction)
                {
                    case MirroringDirection.GitToHg:
                        RunCommandAndLogOutput("hg pull " + quotedGitCloneUrl);
                        PushWithBookmarks(quotedHgCloneUrl);
                        break;
                    case MirroringDirection.HgToGit:
                        RunCommandAndLogOutput("hg pull " + quotedHgCloneUrl);
                        if (configuration.GitUrlIsHgUrl)
                        {
                            PushWithBookmarks(quotedGitCloneUrl);
                        }
                        else
                        {
                            PushToGit(configuration.GitCloneUri);
                        }
                        break;
                    case MirroringDirection.TwoWay:
                        RunCommandAndLogOutput("hg pull " + quotedGitCloneUrl);
                        RunCommandAndLogOutput("hg pull " + quotedHgCloneUrl);
                        PushWithBookmarks(quotedHgCloneUrl);
                        if (configuration.GitUrlIsHgUrl)
                        {
                            PushWithBookmarks(quotedGitCloneUrl);
                        }
                        else
                        {
                            PushToGit(configuration.GitCloneUri);
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                if (!(ex is CommandException) && !(ex is IOException)) throw;

                _commandRunner.Dispose(); // Should dispose so the folder is not locked.

                var mirroringException = new MirroringException(string.Format("An error occured while running Mercurial commands when mirroring the repositories {0} and {1} in direction {2}. Cloning will re-started next time.", configuration.HgCloneUri, configuration.GitCloneUri, configuration.Direction), ex);
                
                try
                {
                    if (Directory.Exists(cloneDirectoryPath)) Directory.Delete(cloneDirectoryPath, true);
                }
                catch (IOException ioException)
                {
                    throw new MirroringException("An error occured while running Mercurial mirroring commands and subsequently during clean-up after the error.", new AggregateException("Multiple errors occured during mirroring.", mirroringException, ioException));
                }

                throw mirroringException;
            }
        }

        public bool IsCloned(MirroringConfiguration configuration)
        {
            var repositoryDirectoryName = GetCloneDirectoryName(configuration);
            var cloneDirectoryParentPath = Path.Combine(_settings.RepositoriesDirectoryPath, repositoryDirectoryName[0].ToString()); // A subfolder per clone dir start letter
            var cloneDirectoryPath = Path.Combine(cloneDirectoryParentPath, repositoryDirectoryName);
            return Directory.Exists(cloneDirectoryPath);
        }

        public void Dispose()
        {
            _commandRunner.Dispose();
        }


        private void PushToGit(Uri gitCloneUri)
        {
            var gitUriBuilder = new UriBuilder(gitCloneUri);
            var userName = gitUriBuilder.UserName;
            var password = gitUriBuilder.Password;
            gitUriBuilder.UserName = null;
            gitUriBuilder.Password = null;
            var gitUri = gitUriBuilder.Uri;
            RunCommandAndLogOutput("hg --config auth.rc.prefix=" + ("https://" + gitUri.Host).EncloseInQuotes() + " --config auth.rc.username=" + userName.EncloseInQuotes() + " --config auth.rc.password=" + password.EncloseInQuotes() + " push " + gitUri.ToString().EncloseInQuotes() + " --force");
        }

        private string RunCommandAndLogOutput(string command)
        {
            var output = _commandRunner.RunCommand(command);
            _eventLog.WriteEntry(output);
            return output;
        }

        private void PushWithBookmarks(string quotedHgCloneUrl)
        {
            var bookmarksOutput = RunCommandAndLogOutput("hg bookmarks");

            // There will be at least one bookmark, "master" with a git repo. However with hg-hg mirroring maybe there are no bookmarks.
            if (bookmarksOutput.Contains("no bookmarks set"))
            {
                RunCommandAndLogOutput("hg push --new-branch --force " + quotedHgCloneUrl);
            }
            else
            {
                var bookmarks = bookmarksOutput
                    .Split(Environment.NewLine.ToArray())
                    .Skip(1) // The first line is the command itself
                    .Where(line => line != string.Empty)
                    .Select(line => "-B " + Regex.Match(line, @"\s([a-z0-9/.-]+)\s", RegexOptions.IgnoreCase).Groups[1].Value);
                RunCommandAndLogOutput("hg push --new-branch --force " + string.Join(" ", bookmarks) + " " + quotedHgCloneUrl);
            }
        }


        private static string GetCloneDirectoryName(MirroringConfiguration configuration)
        {
            return (configuration.HgCloneUri + " - " + configuration.GitCloneUri).GetHashCode().ToString();
        }
    }
}

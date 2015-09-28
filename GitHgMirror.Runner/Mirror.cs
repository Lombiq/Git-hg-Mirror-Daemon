
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
                // Changing directory to other drive if necessary.
                RunCommandAndLogOutput(Path.GetPathRoot(cloneDirectoryPath).Replace("\\", string.Empty));


                if (!Directory.Exists(cloneDirectoryParentPath))
                {
                    Directory.CreateDirectory(cloneDirectoryParentPath);
                }
                var quotedHgCloneUrl = configuration.HgCloneUri.ToString().EncloseInQuotes();
                var quotedGitCloneUrl = configuration.GitCloneUri.ToString().EncloseInQuotes();
                var quotedCloneDirectoryPath = cloneDirectoryPath.EncloseInQuotes();
                var isCloned = IsCloned(configuration);

                Action cdCloneDirectory = () => RunCommandAndLogOutput("cd " + quotedCloneDirectoryPath);

                if (isCloned)
                {
                    Directory.SetLastAccessTimeUtc(cloneDirectoryPath, DateTime.UtcNow);
                    cdCloneDirectory();
                }
                else
                {
                    DeleteDirectoryIfExists(cloneDirectoryPath);
                    Directory.CreateDirectory(cloneDirectoryPath);
                }


                switch (configuration.Direction)
                {
                    case MirroringDirection.GitToHg:
                        if (isCloned)
                        {
                            PullFromGit(configuration.GitCloneUri);
                            cdCloneDirectory();
                            RunCommandAndLogOutput("hg gimport");
                        }
                        else
                        {
                            CloneGit(configuration.GitCloneUri, quotedCloneDirectoryPath);
                            cdCloneDirectory();
                        }
                        PushWithBookmarks(quotedHgCloneUrl);
                        break;
                    case MirroringDirection.HgToGit:
                        if (isCloned)
                        {
                            RunCommandAndLogOutput("hg pull " + quotedHgCloneUrl);
                            CreateBookmarksForBranches();
                            RunCommandAndLogOutput("hg gexport");
                        }
                        else
                        {
                            CloneHg(quotedHgCloneUrl, quotedCloneDirectoryPath);
                            cdCloneDirectory();
                            if (!configuration.GitUrlIsHgUrl)
                            {
                                CreateBookmarksForBranches();

                                RunCommandAndLogOutput("hg gexport");
                            }
                        }

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
                        if (!isCloned)
                        {
                            if (configuration.GitUrlIsHgUrl)
                            {
                                CloneHg(quotedGitCloneUrl, quotedCloneDirectoryPath);
                            }
                            else
                            {
                                CloneGit(configuration.GitCloneUri, quotedCloneDirectoryPath);
                            }
                            cdCloneDirectory();
                        }
                        else
                        {
                            if (configuration.GitUrlIsHgUrl)
                            {
                                RunCommandAndLogOutput("hg pull " + quotedGitCloneUrl);
                            }
                            else
                            {
                                PullFromGit(configuration.GitCloneUri);
                            }
                        }

                        RunCommandAndLogOutput("hg pull " + quotedHgCloneUrl);

                        if (!configuration.GitUrlIsHgUrl)
                        {
                            RunCommandAndLogOutput("hg gexport");
                            RunCommandAndLogOutput("hg gimport");
                        }

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

                var mirroringException = new MirroringException(string.Format("An error occured while running Mercurial commands when mirroring the repositories {0} and {1} in direction {2}. Mirroring will re-started next time.", configuration.HgCloneUri, configuration.GitCloneUri, configuration.Direction), ex);

                try
                {
                    DeleteDirectoryIfExists(cloneDirectoryPath);
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
            // Also checking if the directory is empty. If yes, it was a failed attempt and really the repo is not cloned.
            return Directory.Exists(cloneDirectoryPath) && Directory.EnumerateFileSystemEntries(cloneDirectoryPath).Any();
        }

        public void Dispose()
        {
            _commandRunner.Dispose();
        }


        private void PushToGit(Uri gitCloneUri)
        {
            var gitUrl = gitCloneUri.ToString().Replace("git+https", "https");
            RunCommandAndLogOutput(@"cd .hg\git");
            // Git repos should be pushed with git as otherwise large (even as large as 15MB) pushes can fail.
            try
            {
                RunCommandAndLogOutput("git push " + gitUrl.EncloseInQuotes() + " --mirror");
            }
            catch (CommandException ex)
            {
                // Git communicates some messages via the error stream, so checking them here.

                    // If there is nothing to push git will return this message in the error stream.
                if (!ex.Error.Contains("Everything up-to-date") &&
                    // When pushing to an empty repo.
                    !ex.Error.Contains("* [new branch]"))
                {
                    throw;
                }
            }
        }

        private void PullFromGit(Uri gitCloneUri)
        {
            RunGitCommand(gitCloneUri, "pull {url}");
        }

        private void CloneGit(Uri gitCloneUri, string quotedCloneDirectoryPath)
        {
            // Cloning a large git repo will work even when (after cloning the corresponding hg repo) pulling it
            // in will fail with a "the connection was forcibly closed by remote host"-like error. This is why
            // we start with cloning the git repo.
            RunGitCommand(gitCloneUri, "clone --noupdate {url} " + quotedCloneDirectoryPath);
        }

        /// <summary>
        /// Runs the specified command for a git repo.
        /// </summary>
        /// <param name="gitCloneUri">The git clone URI.</param>
        /// <param name="command">
        /// The command, including an optional placeholder for the git URL in form of {url}, e.g.: "clone --noupdate {url}".
        /// </param>
        private void RunGitCommand(Uri gitCloneUri, string command)
        {
            var gitUriBuilder = new UriBuilder(gitCloneUri);
            var userName = gitUriBuilder.UserName;
            var password = gitUriBuilder.Password;
            gitUriBuilder.UserName = null;
            gitUriBuilder.Password = null;
            var gitUri = gitUriBuilder.Uri;
            var quotedGitCloneUrl = gitUri.ToString().EncloseInQuotes();
            command = command.Replace("{url}", quotedGitCloneUrl);

            if (!string.IsNullOrEmpty(userName))
            {
                RunCommandAndLogOutput(
                    "hg --config auth.rc.prefix=" +
                    ("https://" + gitUri.Host).EncloseInQuotes() +
                    " --config auth.rc.username=" +
                    userName.EncloseInQuotes() +
                    " --config auth.rc.password=" +
                    password.EncloseInQuotes() +
                    " " +
                    command);
            }
            else
            {
                RunCommandAndLogOutput("hg " + command);
            }
        }

        private void CloneHg(string quotedHgCloneUrl, string quotedCloneDirectoryPath)
        {
            RunCommandAndLogOutput("hg clone --noupdate " + quotedHgCloneUrl + " " + quotedCloneDirectoryPath);
        }

        private void CreateBookmarksForBranches()
        {
            // Adding bookmarks for all branches so they appear as proper git branches.
            var branchesOutput = RunCommandAndLogOutput("hg branches --closed");

            var branches = branchesOutput
                  .Split(Environment.NewLine.ToArray())
                  .Skip(1) // The first line is the command itself
                  .Where(line => !string.IsNullOrEmpty(line))
                  .Select(line => Regex.Match(line, @"(.+?)\s+\d+:[a-z0-9]+").Groups[1].Value);

            foreach (var branch in branches)
            {
                // Need to strip spaces from branch names, see:
                // https://bitbucket.org/durin42/hg-git/issues/163/gexport-fails-on-bookmarks-with-spaces-in
                var bookmark = branch.Replace(' ', '-');
                if (branch == "default") bookmark = "master";
                else if (branch == "dev") bookmark = "develop"; // This is a special name substitution not to use the hg/ prefix.
                else bookmark = "hg/" + bookmark;

                RunCommandAndLogOutput("hg bookmark -r " + branch.EncloseInQuotes() + " " + bookmark);
            }
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
                      .Where(line => !string.IsNullOrEmpty(line))
                      .Select(line => Regex.Match(line, @"\s([a-zA-Z0-9/.\-_]+)\s", RegexOptions.IgnoreCase).Groups[1].Value)
                      .Where(line => !string.IsNullOrEmpty(line))
                      .Select(line => "-B " + line);

                // Pushing a lot of bookmarks at once would result in a "RuntimeError: maximum recursion depth exceeded" error.
                var batchSize = 30;
                var bookmarksBatch = bookmarks.Take(batchSize);
                var skip = 0;
                var bookmarkCount = bookmarks.Count();
                while (skip < bookmarkCount)
                {
                    RunCommandAndLogOutput("hg push --new-branch --force " + string.Join(" ", bookmarksBatch) + " " + quotedHgCloneUrl);
                    skip += batchSize;
                    bookmarksBatch = bookmarks.Skip(skip).Take(batchSize);
                    if (bookmarksBatch.Any())
                    {
                        // Bitbucket throttles such requests so we need to slow down. Otherwise we'd get this error:
                        // "remote: Push throttled (max allowable rate: 30 per 60 seconds)."
                        Thread.Sleep(61000);
                    }
                }
            }
        }

        private string RunCommandAndLogOutput(string command)
        {
            var output = _commandRunner.RunCommand(command);
            _eventLog.WriteEntry(output);
            return output;
        }


        private static string GetCloneDirectoryName(MirroringConfiguration configuration)
        {
            return (configuration.HgCloneUri + " - " + configuration.GitCloneUri).GetHashCode().ToString();
        }

        private static void DeleteDirectoryIfExists(string path)
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
    }
}


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
        private const string GitBookmarkSuffix = "-git";
        private const string HgGitConfig = " --config git.branch_bookmark_suffix=" + GitBookmarkSuffix;

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
            var descriptor = GetMirroringDescriptor(configuration);

            Debug.WriteLine("Starting mirroring: " + descriptor);

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
                            if (configuration.GitUrlIsHgUrl)
                            {
                                RunRemoteHgCommandAndLogOutput("hg pull --insecure " + quotedGitCloneUrl);
                            }
                            else
                            {
                                PullFromGit(configuration.GitCloneUri, cloneDirectoryPath);
                                cdCloneDirectory();
                                RunGitImport();
                            }
                        }
                        else
                        {
                            if (configuration.GitUrlIsHgUrl)
                            {
                                CloneHg(quotedGitCloneUrl, quotedCloneDirectoryPath);
                            }
                            else
                            {
                                CloneHg(quotedHgCloneUrl, (cloneDirectoryPath + "-hg").EncloseInQuotes());
                                RunCommandAndLogOutput("cd " + (cloneDirectoryPath + "-hg").EncloseInQuotes());
                                DeleteAllBookmarks(quotedHgCloneUrl);
                                cdCloneDirectory();

                                CloneGit(configuration.GitCloneUri, quotedCloneDirectoryPath);

                                cdCloneDirectory();
                                DeleteAllBookmarks(quotedHgCloneUrl, false);
                                CreateBookmarksForBranches();
                            }
                        }

                        cdCloneDirectory();

                        PushWithBookmarks(quotedHgCloneUrl);

                        break;
                    case MirroringDirection.HgToGit:
                        if (isCloned)
                        {
                            RunRemoteHgCommandAndLogOutput("hg pull --insecure " + quotedHgCloneUrl);
                        }
                        else
                        {
                            CloneHg(quotedHgCloneUrl, quotedCloneDirectoryPath);

                            cdCloneDirectory();
                            DeleteAllBookmarks(quotedHgCloneUrl);
                        }

                        cdCloneDirectory();

                        if (configuration.GitUrlIsHgUrl)
                        {
                            PushWithBookmarks(quotedGitCloneUrl);
                        }
                        else
                        {
                            CreateBookmarksForBranches();
                            RunGitExport();
                            PushToGit(configuration.GitCloneUri);
                        }

                        break;
                    case MirroringDirection.TwoWay:
                        if (isCloned)
                        {
                            if (configuration.GitUrlIsHgUrl)
                            {
                                RunRemoteHgCommandAndLogOutput("hg pull --insecure " + quotedGitCloneUrl);

                                cdCloneDirectory();
                            }
                            else
                            {
                                PullFromGit(configuration.GitCloneUri, cloneDirectoryPath);

                                cdCloneDirectory();

                                RunRemoteHgCommandAndLogOutput("hg pull --insecure " + quotedHgCloneUrl);

                                CreateBookmarksForBranches();
                                RunGitExport();
                                RunGitImport();
                            }
                        }
                        else
                        {
                            if (configuration.GitUrlIsHgUrl)
                            {
                                CloneHg(quotedGitCloneUrl, quotedCloneDirectoryPath);

                                cdCloneDirectory();

                                RunRemoteHgCommandAndLogOutput("hg pull --insecure " + quotedHgCloneUrl);
                            }
                            else
                            {
                                // We need to start with cloning the hg repo. Otherwise cloning the git repo, then
                                // pulling from the hg repo would yield a "repository unrelated" error, even if the git
                                // repo was created from the hg. For an explanation see: 
                                // http://stackoverflow.com/questions/17240852/hg-git-clone-from-github-gives-abort-repository-is-unrelated
                                CloneHg(quotedHgCloneUrl, quotedCloneDirectoryPath);
                                cdCloneDirectory();

                                DeleteAllBookmarks(quotedHgCloneUrl);

                                //CreateBookmarksForBranches();
                                //RunGitExport();
                                //PullFromGit(configuration.GitCloneUri, cloneDirectoryPath);
                                //cdCloneDirectory();
                                //RunGitExport();
                                //RunGitImport();

                                // This wipes branches created in git.
                                CreateBookmarksForBranches();
                                RunGitExport();
                                PullFromGit(configuration.GitCloneUri, cloneDirectoryPath);
                                cdCloneDirectory();
                                DeleteAllBookmarks(quotedHgCloneUrl, false);
                                CreateBookmarksForBranches();
                                RunGitExport();
                                RunGitImport();
                            }
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

                Debug.WriteLine("Finished mirroring: " + descriptor);
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
            RunCommandAndLogOutput(@"cd .hg\git");
            // Git repos should be pushed with git as otherwise large (even as large as 15MB) pushes can fail.
            try
            {
                RunCommandAndLogOutput("git push " + gitCloneUri.ToGitUrl().EncloseInQuotes() + " --mirror --follow-tags");
            }
            catch (CommandException ex)
            {
                if (IsGitExceptionRealError(ex)) throw;
            }
        }

        private void PullFromGit(Uri gitCloneUri, string cloneDirectoryPath)
        {
            var gitDirectoryPath = Path.Combine(cloneDirectoryPath, ".hg", "git");
            // The git directory won't exist if the hg repo is empty (gexport won't do anything).
            if (!Directory.Exists(gitDirectoryPath))
            {
                try
                {
                    RunCommandAndLogOutput("git clone --mirror " + gitCloneUri.ToGitUrl().EncloseInQuotes() + " " + gitDirectoryPath.EncloseInQuotes());
                }
                catch (CommandException ex)
                {
                    if (!ex.Error.Contains("Cloning into bare repository ") && IsGitExceptionRealError(ex)) throw;
                }
            }
            else
            {
                RunCommandAndLogOutput(@"cd .hg\git");

                // Git repos should be pulled with git as hg-git pull won't pull in new tags.
                try
                {
                    RunCommandAndLogOutput("git fetch --prune --tags " + gitCloneUri.ToGitUrl().EncloseInQuotes());
                }
                catch (CommandException ex)
                {
                    if (IsGitExceptionRealError(ex)) throw;
                }
            }
        }

        private void CloneGit(Uri gitCloneUri, string quotedCloneDirectoryPath)
        {
            // Cloning a large git repo will work even when (after cloning the corresponding hg repo) pulling it
            // in will fail with a "the connection was forcibly closed by remote host"-like error. This is why
            // we start with cloning the git repo.
            RunGitRepoCommand(gitCloneUri, "clone --noupdate {url} " + quotedCloneDirectoryPath);
        }

        /// <summary>
        /// Runs the specified command for a git repo in hg.
        /// </summary>
        /// <param name="gitCloneUri">The git clone URI.</param>
        /// <param name="command">
        /// The command, including an optional placeholder for the git URL in form of {url}, e.g.: "clone --noupdate {url}".
        /// </param>
        private void RunGitRepoCommand(Uri gitCloneUri, string command)
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
                RunRemoteHgCommandAndLogOutput(
                    "hg --insecure --config auth.rc.prefix=" +
                    ("https://" + gitUri.Host).EncloseInQuotes() +
                    " --config auth.rc.username=" +
                    userName.EncloseInQuotes() +
                    " --config auth.rc.password=" +
                    password.EncloseInQuotes()
                    + HgGitConfig +
                    " " +
                    command);
            }
            else
            {
                RunRemoteHgCommandAndLogOutput("hg " + command);
            }
        }

        private void RunGitImport()
        {
            RunCommandAndLogOutput("hg gimport" + HgGitConfig);
        }

        private void RunGitExport()
        {
            RunCommandAndLogOutput("hg gexport" + HgGitConfig);
        }

        private void CloneHg(string quotedHgCloneUrl, string quotedCloneDirectoryPath)
        {
            RunRemoteHgCommandAndLogOutput("hg clone --insecure --noupdate " + quotedHgCloneUrl + " " + quotedCloneDirectoryPath);
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
                if (branch == "default") bookmark = "master" + GitBookmarkSuffix;
                else bookmark = bookmark + GitBookmarkSuffix;

                // Don't move the bookmark if on the changeset there is already a git bookmark, because this means that
                // there was a branch created in git. E.g. we shouldn't move the master bookmark to the default head
                // since with a new git branch there will be two default heads (since git branches are converted to
                // bookmarks on default) and we'd wrongly move the master head.
                var changesetLogOutput = RunCommandAndLogOutput("hg log -r " + branch.EncloseInQuotes());
                // For hg log this is needed, otherwise the next command would return an empty line.
                RunCommandAndLogOutput(Environment.NewLine);

                var existingBookmarks = changesetLogOutput
                      .Split(Environment.NewLine.ToArray())
                      .Skip(1) // The first line is the command itself
                      .Where(line => !string.IsNullOrEmpty(line) && line.StartsWith("bookmark:"))
                      .Select(line => Regex.Match(line, @"bookmark:\s+(.+)(\s|$)").Groups[1].Value);

                if (!existingBookmarks.Any(existingBookmark => existingBookmark.EndsWith(GitBookmarkSuffix)))
                {
                    // Need --force so it moves the bookmark if it already exists.
                    RunCommandAndLogOutput("hg bookmark -r " + branch.EncloseInQuotes() + " " + bookmark + " --force"); 
                }
            }
        }

        private void DeleteAllBookmarks(string quotedHgCloneUrl, bool push = true)
        {
            var bookmarksOutput = RunCommandAndLogOutput("hg bookmarks");

            if (!bookmarksOutput.Contains("no bookmarks set"))
            {
                var bookmarks = bookmarksOutput
                      .Split(Environment.NewLine.ToArray())
                      .Skip(1) // The first line is the command itself
                      .Where(line => !string.IsNullOrEmpty(line))
                      .Select(line => Regex.Match(line, @"\s([a-zA-Z0-9/.\-_]+)\s", RegexOptions.IgnoreCase).Groups[1].Value)
                      .Where(line => !string.IsNullOrEmpty(line));

                foreach (var bookmark in bookmarks)
                {
                    RunCommandAndLogOutput("hg bookmark --delete " + bookmark);
                    if (push) RunCommandAndLogOutput("hg push --insecure --bookmark " + bookmark + " " + quotedHgCloneUrl);
                }
            }
        }

        private void PushWithBookmarks(string quotedHgCloneUrl)
        {
            var bookmarksOutput = RunCommandAndLogOutput("hg bookmarks");

            // There will be at least one bookmark, "master" with a git repo. However with hg-hg mirroring maybe there are no bookmarks.
            if (bookmarksOutput.Contains("no bookmarks set"))
            {
                RunRemoteHgCommandAndLogOutput("hg push --insecure --new-branch --force " + quotedHgCloneUrl);
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
                    RunRemoteHgCommandAndLogOutput("hg push --insecure --new-branch --force " + string.Join(" ", bookmarksBatch) + " " + quotedHgCloneUrl);
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

        private string RunRemoteHgCommandAndLogOutput(string hgCommand)
        {
            var output = "";
            try
            {
                output = RunCommandAndLogOutput(hgCommand);
                return output;
            }
            catch (CommandException ex)
            {
                // Catching warning-level "bitbucket.org certificate with fingerprint .. not verified (check hostfingerprints 
                // or web.cacerts config setting)" kind of errors that happen when mirroring happens accessing an insecure
                // host.
                if (!ex.Error.Contains("not verified (check hostfingerprints or web.cacerts config setting)"))
                {
                    throw;
                }

                return output;
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
            return GetMirroringDescriptor(configuration).GetHashCode().ToString();
        }

        private static string GetMirroringDescriptor(MirroringConfiguration configuration)
        {
            var directionIndicator = "->";
            if (configuration.Direction == MirroringDirection.GitToHg) directionIndicator = "<-";
            else if (configuration.Direction == MirroringDirection.TwoWay) directionIndicator = "<->";

            return 
                configuration.HgCloneUri +
                " " + directionIndicator + " " + 
                configuration.GitCloneUri;
        }

        private static void DeleteDirectoryIfExists(string path)
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }

        /// <summary>
        /// Git communicates some messages via the error stream, so checking them here.
        /// </summary>
        private static bool IsGitExceptionRealError(CommandException ex)
        {
            return
                // If there is nothing to push git will return this message in the error stream.
                !ex.Error.Contains("Everything up-to-date") &&
                // A new branch was added.
                !ex.Error.Contains("* [new branch]") &&
                // Branches were deleted in git.
                !ex.Error.Contains("[deleted]") &&
                // A new tag was added.
                !ex.Error.Contains("* [new tag]") &&
                // The branch head was moved (shown during push).
                !(ex.Error.Contains("..") && ex.Error.Contains(" -> ")) &&
                // The branch head was moved (shown during fetch).
                !(ex.Error.Contains("* branch") && ex.Error.Contains(" -> "));
        }
    }
}

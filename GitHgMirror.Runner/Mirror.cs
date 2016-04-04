
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
using GitHgMirror.Runner.Helpers;
using LibGit2Sharp;

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

                    // Debug info file. Not placing it into the clone directory because that would bother Mercurial.
                    File.WriteAllText(cloneDirectoryPath + "-info.txt", GetMirroringDescriptor(configuration));
                }


                switch (configuration.Direction)
                {
                    case MirroringDirection.GitToHg:
                        if (isCloned)
                        {
                            if (configuration.GitUrlIsHgUrl)
                            {
                                PullHg(quotedGitCloneUrl);
                            }
                            else
                            {
                                FetchFromGit(configuration.GitCloneUri, cloneDirectoryPath);
                                cdCloneDirectory();
                                ImportHistoryFromGit();
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
                                CloneGit(configuration.GitCloneUri, quotedCloneDirectoryPath);
                            }
                        }

                        cdCloneDirectory();

                        PushWithBookmarks(quotedHgCloneUrl);

                        break;
                    case MirroringDirection.HgToGit:
                        if (isCloned)
                        {
                            PullHg(quotedHgCloneUrl);
                        }
                        else
                        {
                            CloneHg(quotedHgCloneUrl, quotedCloneDirectoryPath);
                        }

                        cdCloneDirectory();

                        if (configuration.GitUrlIsHgUrl)
                        {
                            PushWithBookmarks(quotedGitCloneUrl);
                        }
                        else
                        {
                            CreateOrUpdateBookmarksForBranches();
                            ExportHistoryToGit();
                            PushToGit(configuration.GitCloneUri, cloneDirectoryPath);
                        }

                        break;
                    case MirroringDirection.TwoWay:
                        Action syncHgAndGitHistories = () =>
                            {
                                cdCloneDirectory();
                                CreateOrUpdateBookmarksForBranches();
                                ExportHistoryToGit();

                                // This will clear all commits int he git repo that aren't in the git remote repo but 
                                // add changes that were added to the git repo.
                                FetchFromGit(configuration.GitCloneUri, cloneDirectoryPath);
                                cdCloneDirectory();
                                ImportHistoryFromGit();
                                
                                // Updating bookmarks which may have shifted after importing from git. This way the
                                // export to git will create a git repo with history identical to the hg repo.
                                CreateOrUpdateBookmarksForBranches();
                                ExportHistoryToGit();

                                PushToGit(configuration.GitCloneUri, cloneDirectoryPath);
                            };

                        if (isCloned)
                        {
                            PullHg(quotedHgCloneUrl);

                            if (configuration.GitUrlIsHgUrl)
                            {
                                PullHg(quotedGitCloneUrl);

                                cdCloneDirectory();
                            }
                            else
                            {
                                syncHgAndGitHistories();
                            }
                        }
                        else
                        {
                            if (configuration.GitUrlIsHgUrl)
                            {
                                CloneHg(quotedGitCloneUrl, quotedCloneDirectoryPath);

                                cdCloneDirectory();

                                PullHg(quotedHgCloneUrl);
                            }
                            else
                            {
                                // We need to start with cloning the hg repo. Otherwise cloning the git repo, then
                                // pulling from the hg repo would yield a "repository unrelated" error, even if the git
                                // repo was created from the hg repo. For an explanation see: 
                                // http://stackoverflow.com/questions/17240852/hg-git-clone-from-github-gives-abort-repository-is-unrelated
                                CloneHg(quotedHgCloneUrl, quotedCloneDirectoryPath);

                                syncHgAndGitHistories();
                            }
                        }


                        PushWithBookmarks(quotedHgCloneUrl);

                        if (configuration.GitUrlIsHgUrl)
                        {
                            PushWithBookmarks(quotedGitCloneUrl);
                        }


                        break;
                }

                Debug.WriteLine("Finished mirroring: " + descriptor);
            }
            catch (Exception ex)
            {
                if (ex.IsFatal()) throw;

                // We should dispose the command runner so the folder is not locked by the command line.
                _commandRunner.Dispose();

                var exceptionMessage = string.Format("An error occured while running commands when mirroring the repositories {0} and {1} in direction {2}. Mirroring will be re-started next time.", configuration.HgCloneUri, configuration.GitCloneUri, configuration.Direction);

                try
                {
                    // Re-cloning a repo is costly. During local debugging you can flip this variable from the
                    // Immediate Window to prevent it if necessary.
                    var continueWithDelete = true;
                    if (continueWithDelete)
                    {
                        DeleteDirectoryIfExists(cloneDirectoryPath);
                    }
                }
                catch (Exception directoryDeleteException)
                {
                    if (directoryDeleteException.IsFatal()) throw;

                    try
                    {
                        // This most possibly means that for some reason some process is still locking the folder although
                        // it shouldn't (mostly, but not definitely the cause of IOException) or there are read-only files
                        // (git pack files commonly are) which can be (but not always) behind UnauthorizedAccessException.
                        if (directoryDeleteException is IOException || directoryDeleteException is UnauthorizedAccessException)
                        {
                            var killedProcesses = new List<string>();
                            var readOnlyFiles = new List<string>();

                            var files = Directory.EnumerateFiles(cloneDirectoryPath, "*", SearchOption.AllDirectories);
                            foreach (var file in files)
                            {
                                var lockingProcesses = FileUtil.WhoIsLocking(file);
                                foreach (var process in lockingProcesses)
                                {
                                    killedProcesses.Add(process.MainModule.FileName);
                                    process.Kill();
                                }

                                if (File.GetAttributes(file).HasFlag(FileAttributes.ReadOnly))
                                {
                                    readOnlyFiles.Add(file);
                                    File.SetAttributes(file, FileAttributes.Normal);
                                }
                            }

                            DeleteDirectoryIfExists(cloneDirectoryPath);

                            exceptionMessage += 
                                " While deleting the folder of the mirror initially failed, after trying to kill processes that were locking files in it and setting all files not to be read-only the folder could be successfully deleted. " +
                                "Processes killed: " + (killedProcesses.Any() ? string.Join(", ", killedProcesses) : "no processes") +
                                " Read-only files: " + (readOnlyFiles.Any() ? string.Join(", ", readOnlyFiles) : "no files");

                            throw new MirroringException(exceptionMessage, ex, directoryDeleteException);
                        }
                    }
                    catch (Exception forcedCleanUpException)
                    {
                        if (forcedCleanUpException.IsFatal() || forcedCleanUpException is MirroringException) throw;

                        throw new MirroringException(
                            exceptionMessage + " Subsequently clean-up after the error failed as well, also the attempt to kill processes that were locking the mirror's folder and clearing all read-only files.",
                            ex,
                            directoryDeleteException,
                            forcedCleanUpException);
                    }

                    throw new MirroringException(
                        exceptionMessage + " Subsequently clean-up after the error failed as well.", 
                        ex, 
                        directoryDeleteException);
                }

                throw new MirroringException(exceptionMessage, ex);
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


        private void PushToGit(Uri gitCloneUri, string cloneDirectoryPath)
        {
            // The git directory won't exist if the hg repo is empty (gexport won't do anything).
            if (!Directory.Exists(GetGitDirectoryPath(cloneDirectoryPath))) return;

            // Git repos should be pushed with git as otherwise large (even as large as 15MB) pushes can fail.

            RunGitOperationOnClonedRepo(gitCloneUri, cloneDirectoryPath, repository =>
                {
                    // This allows large, 500MB pushes, see: https://stackoverflow.com/questions/12651749/git-push-fails-rpc-failed-result-22-http-code-411
                    repository.Config.Set("http.postBuffer", 524288000);

                    // Refspec patterns on push are not supported, see: http://stackoverflow.com/a/25721274/220230
                    // So can't use "+refs/*:refs/*" here, must iterate.
                    foreach (var reference in repository.Refs)
                    {
                        // Having "+" + reference.CanonicalName + ":" + reference.CanonicalName  as the refspec here
                        // would be force push and completely overwrite the remote repo's content. This would always
                        // succeed no matter what is there but could wipe out changes made between the repo was fetched
                        // and pushed.
                        repository.Network.Push(repository.Network.Remotes["origin"], reference.CanonicalName);
                    }
                });
        }

        private void FetchFromGit(Uri gitCloneUri, string cloneDirectoryPath)
        {
            var gitDirectoryPath = GetGitDirectoryPath(cloneDirectoryPath);
            // The git directory won't exist if the hg repo is empty (gexport won't do anything).
            if (!Directory.Exists(gitDirectoryPath))
            {
                RunLibGit2SharpOperationWithRetry(gitCloneUri, cloneDirectoryPath, () =>
                    Repository.Clone(gitCloneUri.ToGitUrl(), gitDirectoryPath, new CloneOptions { IsBare = true }));
            }
            else
            {
                // Unfortunately this won't fetch tags for some reason. TagFetchMode.All won't help either...
                RunGitOperationOnClonedRepo(gitCloneUri, cloneDirectoryPath, repository =>
                    repository.Network.Fetch(repository.Network.Remotes["origin"], new[] { "+refs/*:refs/*" }));
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
                    "hg --config auth.rc.prefix=" +
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
                RunRemoteHgCommandAndLogOutput("hg " + command + HgGitConfig);
            }
        }

        private void ImportHistoryFromGit()
        {
            RunHgCommandAndLogOutput("hg gimport" + HgGitConfig);
        }

        private void ExportHistoryToGit()
        {
            RunHgCommandAndLogOutput("hg gexport" + HgGitConfig);
        }

        private void CloneHg(string quotedHgCloneUrl, string quotedCloneDirectoryPath)
        {
            try
            {
                RunRemoteHgCommandAndLogOutput("hg clone --noupdate " + quotedHgCloneUrl + " " + quotedCloneDirectoryPath);
            }
            catch (CommandException ex)
            {
                if (ex.IsHgConnectionTerminatedError())
                {
                    RunRemoteHgCommandAndLogOutput("hg clone --noupdate --rev 0 " + quotedHgCloneUrl + " " + quotedCloneDirectoryPath);
                    RunCommandAndLogOutput("cd " + quotedCloneDirectoryPath);

                    PullPerRevisionsHg(quotedHgCloneUrl);
                }
                else throw;
            }
        }

        private void PullHg(string quotedHgCloneUrl)
        {
            try
            {
                RunRemoteHgCommandAndLogOutput("hg pull " + quotedHgCloneUrl);
            }
            catch (CommandException ex)
            {
                if (ex.IsHgConnectionTerminatedError()) PullPerRevisionsHg(quotedHgCloneUrl);
                else throw;
            }
        }

        /// <summary>
        /// Pulling chunks a repo history in chunks of revisions. This will be slow but surely work, even if one
        /// changeset is huge like this one: http://hg.openjdk.java.net/openjfx/9-dev/rt/rev/86d5cbe0c60f (~100MB, 
        /// 11000 files).
        /// </summary>
        private void PullPerRevisionsHg(string quotedHgCloneUrl)
        {
            var startRevision = RunHgCommandAndLogOutput("hg identify --rev tip --num")
                .Split(new[] { Environment.NewLine }, StringSplitOptions.None)[1];
            var revision = int.Parse(startRevision) + 1;
            var finished = false;
            while (!finished)
            {
                try
                {
                    var output = RunRemoteHgCommandAndLogOutput("hg pull --rev " + revision + " " + quotedHgCloneUrl);
                    finished = output.Contains("no changes found");
                }
                catch (CommandException pullException)
                {
                    // This error happens when we try to go beyond existing revisions and it means we reached
                    // the end of the repo history.
                    // Maybe the hg identify command could be used to retrieve the latest revision number instead
                    // (see: https://selenic.com/hg/help/identify) although it says "can't query remote revision 
                    // number, branch, or tag" (and even if it could, what if new changes are being pushed?). So
                    // using exceptions for now.
                    if (pullException.Error.Contains("abort: unknown revision "))
                    {
                        finished = true;
                    }
                    else throw;
                }

                revision++;
            }
        }

        private void CreateOrUpdateBookmarksForBranches()
        {
            // Adding bookmarks for all branches so they appear as proper git branches.
            var branchesOutput = RunHgCommandAndLogOutput("hg branches --closed");

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
                var changesetLogOutput = RunHgCommandAndLogOutput("hg log -r " + branch.EncloseInQuotes());
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
                    RunHgCommandAndLogOutput("hg bookmark -r " + branch.EncloseInQuotes() + " " + bookmark + " --force");
                }
            }
        }

        private void PushWithBookmarks(string quotedHgCloneUrl)
        {
            var bookmarksOutput = RunHgCommandAndLogOutput("hg bookmarks");

            // There will be at least one bookmark, "master" with a git repo. However with hg-hg mirroring maybe there are no bookmarks.
            if (bookmarksOutput.Contains("no bookmarks set"))
            {
                RunRemoteHgCommandAndLogOutput("hg push --new-branch --force " + quotedHgCloneUrl);
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
                    RunRemoteHgCommandAndLogOutput("hg push --new-branch --force " + string.Join(" ", bookmarksBatch) + " " + quotedHgCloneUrl);
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

        private string RunRemoteHgCommandAndLogOutput(string hgCommand, int retryCount = 0)
        {
            var output = "";
            try
            {
                if (_settings.MercurialSettings.UseInsecure)
                {
                    hgCommand = hgCommand + " --insecure";
                }
                if (_settings.MercurialSettings.UseDebugForRemoteCommands && !_settings.MercurialSettings.UseDebug)
                {
                    hgCommand = hgCommand + " --debug";
                }

                output = RunHgCommandAndLogOutput(hgCommand);

                return output;
            }
            catch (CommandException ex)
            {
                // We'll randomly get such errors when interacting with Mercurial as well as with Git, otherwise properly
                // running repos. So we re-try the operation a few times, maybe it'll work...
                if (ex.Error.Contains("EOF occurred in violation of protocol"))
                {
                    if (retryCount >= 5)
                    {
                        throw new IOException("Couldn't run the following Mercurial command successfully even after " + retryCount + " tries due to an \"EOF occurred in violation of protocol\" error: " + hgCommand, ex);
                    }

                    // Let's wait a bit before re-trying so our prayers can heal the connection in the meantime.
                    Thread.Sleep(10000);

                    return RunRemoteHgCommandAndLogOutput(hgCommand, ++retryCount);
                }

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

        private string RunHgCommandAndLogOutput(string hgCommand)
        {
            if (_settings.MercurialSettings.UseDebug)
            {
                hgCommand = hgCommand + " --debug";
            }

            if (_settings.MercurialSettings.UseTraceback)
            {
                hgCommand = hgCommand + " --traceback";
            }

            return RunCommandAndLogOutput(hgCommand);
        }

        private string RunCommandAndLogOutput(string command)
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

        private void RunGitOperationOnClonedRepo(Uri gitCloneUri, string cloneDirectoryPath, Action<Repository> operation)
        {
            RunLibGit2SharpOperationWithRetry(gitCloneUri, cloneDirectoryPath, () =>
            {
                using (var repository = new Repository(GetGitDirectoryPath(cloneDirectoryPath)))
                {
                    if (repository.Network.Remotes["origin"] == null)
                    {
                        var newRemote = repository.Network.Remotes.Add("origin", gitCloneUri.ToGitUrl());

                        repository.Config.Set("remote.origin.mirror", true);
                    }


                    operation(repository);
                }
            });
        }

        /// <summary>
        /// Since somehow LibGit2Sharp routinely fails with "Failed to receive response: The server returned an invalid 
        /// or unrecognized response" we re-try operations here.
        /// </summary>
        private void RunLibGit2SharpOperationWithRetry(Uri gitCloneUri, string cloneDirectoryPath, Action operation, int retryCount = 0)
        {
            try
            {
                operation();
            }
            catch (LibGit2SharpException ex)
            {
                // We won't re-try these as these errors are most possibly not transient ones.
                if (ex.Message.Contains("Request failed with status code: 404") ||
                    ex.Message.Contains("Request failed with status code: 401") ||
                    ex.Message.Contains("Request failed with status code: 403"))
                {
                    throw;
                }

                var errorDescriptor = 
                    Environment.NewLine + "Operation attempted with the " + gitCloneUri.ToGitUrl() + " repository (directory: " + cloneDirectoryPath + ")" +
                    Environment.NewLine + ex.ToString() + 
                    Environment.NewLine + "Operation: " + Environment.NewLine + 
                    // Removing first two lines from the stack trace that contain the stack trace retrieval itself.
                    string.Join(Environment.NewLine, Environment.StackTrace.Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries).Skip(2));

                // We allow 3 tries.
                if (retryCount < 2)
                {
                    _eventLog.WriteEntry(
                        "A LibGit2Sharp operation failed " + (retryCount + 1) + " time(s) but will be re-tried." + errorDescriptor,
                        EventLogEntryType.Warning);

                    RunLibGit2SharpOperationWithRetry(gitCloneUri, cloneDirectoryPath, operation, ++retryCount);
                }
                else
                {
                    _eventLog.WriteEntry(
                        "A LibGit2Sharp operation failed " + (retryCount + 1) + " time(s) and won't be re-tried again." + errorDescriptor,
                        EventLogEntryType.Warning);

                    throw;
                }
            }
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

        private static string GetGitDirectoryPath(string cloneDirectoryPath)
        {
            return Path.Combine(cloneDirectoryPath, ".hg", "git");
        }
    }
}

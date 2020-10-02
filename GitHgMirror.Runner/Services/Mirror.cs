using GitHgMirror.CommonTypes;
using GitHgMirror.Runner.Helpers;
using LibGit2Sharp;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace GitHgMirror.Runner.Services
{
    internal class Mirror : CommandExecutorBase
    {
        private readonly HgCommandExecutor _hgCommandExecutor;
        private readonly GitCommandExecutor _gitCommandExecutor;


        public Mirror(EventLog eventLog)
            : base(eventLog)
        {
            _hgCommandExecutor = new HgCommandExecutor(eventLog);
            _gitCommandExecutor = new GitCommandExecutor(eventLog);
        }


        public void MirrorRepositories(
            MirroringConfiguration configuration,
            MirroringSettings settings,
            CancellationToken cancellationToken)
        {
            var descriptor = GetMirroringDescriptor(configuration);
            var loggedDescriptor = descriptor + " (#" + configuration.Id.ToString(CultureInfo.InvariantCulture) + ")";

            Debug.WriteLine("Starting mirroring: " + loggedDescriptor);
            _eventLog.WriteEntry("Starting mirroring: " + loggedDescriptor);

            var repositoryDirectoryName = GetCloneDirectoryName(configuration);
            // A subfolder per clone dir start letter:
            var cloneDirectoryParentPath = Path.Combine(settings.RepositoriesDirectoryPath, repositoryDirectoryName[0].ToString());
            var cloneDirectoryPath = Path.Combine(cloneDirectoryParentPath, repositoryDirectoryName);
            var repositoryLockFilePath = GetRepositoryLockFilePath(cloneDirectoryPath);

            try
            {
                if (File.Exists(repositoryLockFilePath))
                {
                    var logEntryStart =
                        "An existing lock was found for the mirroring configuration " + loggedDescriptor + ". ";
                    var lastUpdatedTimeUtc = RepositoryInfoFileHelper.GetLastUpdatedDateTimeUtc(cloneDirectoryPath);

                    if (lastUpdatedTimeUtc >= DateTime.UtcNow.AddSeconds(-settings.MirroringTimoutSeconds))
                    {
                        _eventLog.WriteEntry(
                            logEntryStart +
                            "This can mean that the number of configurations was reduced and thus while a mirroring was" +
                            " running a new process for the same repositories was started. We'll let the initial process finish.");

                        return;
                    }
                    else
                    {
                        _eventLog.WriteEntry(
                            logEntryStart +
                            "Additionally the directory was last touched at " + lastUpdatedTimeUtc.ToString(CultureInfo.InvariantCulture) +
                            " UTC which is older than the allowed mirroring timeout (" + settings.MirroringTimoutSeconds +
                            "s). Thus the lock is considered abandoned and mirroring will continue.",
                            EventLogEntryType.Warning);
                    }
                }

                if (configuration.HgCloneUri.Scheme.Equals("ssh", StringComparison.OrdinalIgnoreCase) ||
                    configuration.GitCloneUri.Scheme.Equals("ssh", StringComparison.OrdinalIgnoreCase))
                {
                    throw new MirroringException("SSH protocol is not supported, only HTTPS.");
                }

                if (!Directory.Exists(cloneDirectoryParentPath))
                {
                    Directory.CreateDirectory(cloneDirectoryParentPath);
                }

                File.Create(repositoryLockFilePath).Dispose();

                // Changing directory to other drive if necessary.
                RunCommandAndLogOutput(Path.GetPathRoot(cloneDirectoryPath).Replace("\\", string.Empty));

                var quotedHgCloneUrl = configuration.HgCloneUri.ToString().EncloseInQuotes();
                var quotedGitCloneUrl = configuration.GitCloneUri.ToString().EncloseInQuotes();
                var quotedCloneDirectoryPath = cloneDirectoryPath.EncloseInQuotes();
                var isCloned = IsCloned(configuration, settings);

                if (!isCloned)
                {
                    DeleteDirectoryIfExists(cloneDirectoryPath);
                    Directory.CreateDirectory(cloneDirectoryPath);
                }

                RepositoryInfoFileHelper.CreateOrUpdateFile(cloneDirectoryPath, descriptor);

                // Mirroring between two git repos is supported, but in a hacked-in way at the moment. This needs a
                // clean-up. Also, do note that only GitToHg and TwoWay is implemented. It would make the whole thing
                // even more messy to duplicate the logic in HgToGit.
                var hgUrlIsGitUrl = configuration.HgCloneUri.Scheme == "git+https";

                cancellationToken.ThrowIfCancellationRequested();

                // It'll be fine for now.
#pragma warning disable S1151 // "switch case" clauses should not have too many lines of code
                switch (configuration.Direction)
                {
                    case MirroringDirection.GitToHg:
                        if (hgUrlIsGitUrl)
                        {
                            RunGitCommandAndMarkException(() => _gitCommandExecutor
                                .FetchOrCloneFromGit(configuration.GitCloneUri, cloneDirectoryPath, true, cancellationToken));
                            _gitCommandExecutor.PushToGit(configuration.HgCloneUri, cloneDirectoryPath, cancellationToken);
                        }
                        else
                        {
                            if (isCloned)
                            {
                                if (configuration.GitUrlIsHgUrl)
                                {
                                    _hgCommandExecutor
                                            .PullHg(quotedGitCloneUrl, quotedCloneDirectoryPath, settings, cancellationToken);
                                }
                                else
                                {
                                    RunGitCommandAndMarkException(() => _gitCommandExecutor
                                        .FetchOrCloneFromGit(configuration.GitCloneUri, cloneDirectoryPath, true, cancellationToken));
                                    _hgCommandExecutor.ImportHistoryFromGit(quotedCloneDirectoryPath, settings, cancellationToken);
                                }
                            }
                            else
                            {
                                if (configuration.GitUrlIsHgUrl)
                                {
                                    _hgCommandExecutor
                                        .CloneHg(quotedGitCloneUrl, quotedCloneDirectoryPath, settings, cancellationToken);
                                }
                                else
                                {
                                    _hgCommandExecutor
                                        .CloneGit(configuration.GitCloneUri, quotedCloneDirectoryPath, settings, cancellationToken);
                                }
                            }

                            _hgCommandExecutor
                                .PushWithBookmarks(quotedHgCloneUrl, quotedCloneDirectoryPath, settings, cancellationToken);
                        }

                        break;
                    case MirroringDirection.HgToGit:
                        if (isCloned)
                        {
                            _hgCommandExecutor.PullHg(quotedHgCloneUrl, quotedCloneDirectoryPath, settings, cancellationToken);
                        }
                        else
                        {
                            _hgCommandExecutor.CloneHg(quotedHgCloneUrl, quotedCloneDirectoryPath, settings, cancellationToken);
                        }

                        if (configuration.GitUrlIsHgUrl)
                        {
                            _hgCommandExecutor
                                .PushWithBookmarks(quotedGitCloneUrl, quotedCloneDirectoryPath, settings, cancellationToken);
                        }
                        else
                        {
                            _hgCommandExecutor.CreateOrUpdateBookmarksForBranches(quotedCloneDirectoryPath, settings, cancellationToken);
                            _hgCommandExecutor.ExportHistoryToGit(quotedCloneDirectoryPath, settings, cancellationToken);
                            RunGitCommandAndMarkException(() =>
                                _gitCommandExecutor.PushToGit(configuration.GitCloneUri, cloneDirectoryPath, cancellationToken));
                        }

                        break;
                    case MirroringDirection.TwoWay:
                        Action syncHgAndGitHistories = () =>
                            {
                                _hgCommandExecutor
                                    .CreateOrUpdateBookmarksForBranches(quotedCloneDirectoryPath, settings, cancellationToken);
                                _hgCommandExecutor.ExportHistoryToGit(quotedCloneDirectoryPath, settings, cancellationToken);

                                // This will clear all commits in the git repo that aren't in the git remote repo but
                                // add changes that were added to the git repo.
                                RunGitCommandAndMarkException(() => _gitCommandExecutor
                                    .FetchOrCloneFromGit(configuration.GitCloneUri, cloneDirectoryPath, false, cancellationToken));
                                _hgCommandExecutor.ImportHistoryFromGit(quotedCloneDirectoryPath, settings, cancellationToken);

                                // Updating bookmarks which may have shifted after importing from git. This way the
                                // export to git will create a git repo with history identical to the hg repo.
                                _hgCommandExecutor.CreateOrUpdateBookmarksForBranches(quotedCloneDirectoryPath, settings, cancellationToken);
                                _hgCommandExecutor.ExportHistoryToGit(quotedCloneDirectoryPath, settings, cancellationToken);

                                RunGitCommandAndMarkException(() =>
                                    _gitCommandExecutor.PushToGit(configuration.GitCloneUri, cloneDirectoryPath, cancellationToken));
                            };

                        if (hgUrlIsGitUrl)
                        {
                            // The easiest solution to do two-way git mirroring is to sync separately, with two clones.
                            // Otherwise when e.g. repository A adds a new commit, then repository B is pulled in, the
                            // head of the branch will be at the point where it is in B. Thus pushing to A will fail
                            // with "Cannot push non-fastforwardable reference". There are other similar errors that
                            // can arise but can't easily be fixed automatically in a safe way. So first pulling both
                            // repos then pushing them won't work.

                            var gitDirectoryPath = GitCommandExecutor.GetGitDirectoryPath(cloneDirectoryPath);

                            var secondToFirstClonePath = Path.Combine(gitDirectoryPath, "secondToFirst");
                            void PullSecondPushToFirst()
                            {
                                RunGitCommandAndMarkException(() => _gitCommandExecutor
                                    .FetchOrCloneFromGit(configuration.HgCloneUri, secondToFirstClonePath, true, cancellationToken));
                                RunGitCommandAndMarkException(() => _gitCommandExecutor
                                    .PushToGit(configuration.GitCloneUri, secondToFirstClonePath, cancellationToken));
                            }

                            var firstToSecondClonePath = Path.Combine(gitDirectoryPath, "firstToSecond");
                            RunGitCommandAndMarkException(() => _gitCommandExecutor
                                .FetchOrCloneFromGit(configuration.GitCloneUri, firstToSecondClonePath, true, cancellationToken));
                            try
                            {
                                RunGitCommandAndMarkException(() => _gitCommandExecutor
                                    .PushToGit(configuration.HgCloneUri, firstToSecondClonePath, cancellationToken));

                                PullSecondPushToFirst();
                            }
                            catch (LibGit2SharpException ex)
                                when (ex.Message.Contains("Cannot push because a reference that you are trying to update on the remote contains commits that are not present locally."))
                            {
                                PullSecondPushToFirst();

                                // This exception can happen when the second repo contains changes not present in the
                                // first one. Then we need to update the first repo with the second's changes and pull-
                                // push again.
                                RunGitCommandAndMarkException(() => _gitCommandExecutor
                                    .FetchOrCloneFromGit(configuration.GitCloneUri, firstToSecondClonePath, true, cancellationToken));
                                RunGitCommandAndMarkException(() => _gitCommandExecutor
                                    .PushToGit(configuration.HgCloneUri, firstToSecondClonePath, cancellationToken));
                            }
                        }
                        else
                        {
                            if (isCloned)
                            {
                                _hgCommandExecutor.PullHg(quotedHgCloneUrl, quotedCloneDirectoryPath, settings, cancellationToken);

                                if (configuration.GitUrlIsHgUrl)
                                {
                                    _hgCommandExecutor
                                        .PullHg(quotedGitCloneUrl, quotedCloneDirectoryPath, settings, cancellationToken);
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
                                    _hgCommandExecutor
                                        .CloneHg(quotedGitCloneUrl, quotedCloneDirectoryPath, settings, cancellationToken);
                                    _hgCommandExecutor
                                        .PullHg(quotedHgCloneUrl, quotedCloneDirectoryPath, settings, cancellationToken);
                                }
                                else
                                {
                                    // We need to start with cloning the hg repo. Otherwise cloning the git repo, then
                                    // pulling from the hg repo would yield a "repository unrelated" error, even if the
                                    // git repo was created from the hg repo. For an explanation see:
                                    // http://stackoverflow.com/questions/17240852/hg-git-clone-from-github-gives-abort-repository-is-unrelated
                                    _hgCommandExecutor
                                        .CloneHg(quotedHgCloneUrl, quotedCloneDirectoryPath, settings, cancellationToken);

                                    syncHgAndGitHistories();
                                }
                            }


                            _hgCommandExecutor
                                .PushWithBookmarks(quotedHgCloneUrl, quotedCloneDirectoryPath, settings, cancellationToken);

                            if (configuration.GitUrlIsHgUrl)
                            {
                                _hgCommandExecutor
                                    .PushWithBookmarks(quotedGitCloneUrl, quotedCloneDirectoryPath, settings, cancellationToken);
                            }
                        }

                        break;
                    default:
                        throw new NotSupportedException("Not supported MirroringDirection.");
                }
#pragma warning restore S1151 // "switch case" clauses should not have too many lines of code

                Debug.WriteLine("Finished mirroring: " + loggedDescriptor);
                _eventLog.WriteEntry("Finished mirroring: " + loggedDescriptor);
            }
            catch (Exception ex) when (!ex.IsFatalOrCancellation())
            {
                // We should dispose the command runners so the folder is not locked by the command line.
                Dispose();
                // Waiting a bit for any file locks or leases to be disposed even though CommandRunners and processes
                // were killed.
                Thread.Sleep(10000);

                var exceptionMessage =
                    $"An error occured while running commands when mirroring the repositories {configuration.HgCloneUri} " +
                    $"and {configuration.GitCloneUri} in direction {configuration.Direction}. Mirroring will be re-started next time.";

                try
                {
                    // Re-cloning a repo is costly. During local debugging you can flip this variable from the
                    // Immediate Window to prevent it if necessary too.
                    var continueWithRepoFolderDelete = true;

                    // These git exceptions are caused by hg errors in a way, so despite them coming from git the whole
                    // repo folder should be removed.
                    var isHgOriginatedGitException =
                        ex.Message.Contains("does not match any existing object") ||
                        ex.Message.Contains("Object not found - failed to find pack entry");
                    if (ex.Data.Contains("IsGitException") && !isHgOriginatedGitException)
                    {
                        exceptionMessage += " The error was a git error.";

                        try
                        {
                            DeleteDirectoryIfExists(GitCommandExecutor.GetGitDirectoryPath(cloneDirectoryPath));

                            exceptionMessage += " Thus just the git folder was removed.";
                            continueWithRepoFolderDelete = false;
                        }
                        catch (Exception gitDirectoryDeleteException) when (!gitDirectoryDeleteException.IsFatalOrCancellation())
                        {
                            exceptionMessage +=
                                " While the removal of just the git folder was attempted it failed with the following " +
                                "exception, thus the deletion of the whole repository folder will be attempted: " +
                                gitDirectoryDeleteException;

                            // We'll continue with the repo folder removal below.
                        }
                    }

                    if (continueWithRepoFolderDelete)
                    {
                        DeleteDirectoryIfExists(cloneDirectoryPath);
                        RepositoryInfoFileHelper.DeleteFileIfExists(cloneDirectoryPath);
                    }
                }
                catch (Exception directoryDeleteException) when (!directoryDeleteException.IsFatalOrCancellation())
                {
                    try
                    {
                        // This most possibly means that for some reason some process is still locking the folder
                        // although it shouldn't (mostly, but not definitely the cause of IOException) or there are
                        // read-only files (git pack files commonly are) which can be (but not always) behind
                        // UnauthorizedAccessException.
                        if (directoryDeleteException is IOException || directoryDeleteException is UnauthorizedAccessException)
                        {
                            var killResult = DirectoryUtil.KillProcessesLockingFiles(cloneDirectoryPath);

                            DeleteDirectoryIfExists(cloneDirectoryPath);
                            RepositoryInfoFileHelper.DeleteFileIfExists(cloneDirectoryPath);

                            exceptionMessage +=
                                " While deleting the folder of the mirror initially failed, after trying to kill processes " +
                                "that were locking files in it and setting all files not to be read-only the folder could be successfully deleted. " +
                                "Processes killed: " + (killResult.KilledProcesseFileNames.Any() ? string.Join(", ", killResult.KilledProcesseFileNames) : "no processes") +
                                " Read-only files: " + (killResult.ReadOnlyFilePaths.Any() ? string.Join(", ", killResult.ReadOnlyFilePaths) : "no files");

                            throw new MirroringException(exceptionMessage, ex, directoryDeleteException);
                        }
                    }
                    catch (Exception forcedCleanUpException)
                    when (!forcedCleanUpException.IsFatalOrCancellation() && !(forcedCleanUpException is MirroringException))
                    {
                        throw new MirroringException(
                            exceptionMessage + " Subsequently clean-up after the error failed as well, also the attempt " +
                            "to kill processes that were locking the mirror's folder and clearing all read-only files.",
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
            finally
            {
                if (File.Exists(repositoryLockFilePath))
                {
                    File.Delete(repositoryLockFilePath);
                }
            }
        }

        public static bool IsCloned(MirroringConfiguration configuration, MirroringSettings settings)
        {
            var repositoryDirectoryName = GetCloneDirectoryName(configuration);
            // A subfolder per clone dir start letter.
            var cloneDirectoryParentPath = Path.Combine(settings.RepositoriesDirectoryPath, repositoryDirectoryName[0].ToString());
            var cloneDirectoryPath = Path.Combine(cloneDirectoryParentPath, repositoryDirectoryName);
            // Also checking if the directory is empty. If yes, it was a failed attempt and really the repo is not cloned.
            return Directory.Exists(cloneDirectoryPath) && Directory.EnumerateFileSystemEntries(cloneDirectoryPath).Any();
        }

        /// <summary>
        /// Gets the path for the lock file for the repository. This lock file shows that the repository is being worked
        /// on.
        /// </summary>
        public static string GetRepositoryLockFilePath(string cloneDirectoryPath) => cloneDirectoryPath + "-lock";


        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            _hgCommandExecutor.Dispose();
            _gitCommandExecutor.Dispose();
        }


        /// <summary>
        /// Runs a git command and if it throws an exception it will mark the exception as coming from git. This allows
        /// subsequent clean-up to only remove the git folder instead of the whole repo folder, sparing hg re-cloning.
        /// </summary>
        private static void RunGitCommandAndMarkException(Action commandRunner)
        {
            try
            {
                commandRunner();
            }
            catch (Exception ex) when (!ex.IsFatalOrCancellation())
            {
                ex.Data["IsGitException"] = true;

                throw;
            }
        }


        private static string GetCloneDirectoryName(MirroringConfiguration configuration) =>
            // Including the ID so if a config is removed, then newly added then it won't be considered the same (this
            // is necessary to fix issues when a re-clone is needed, like when git history was modified). But
            // nevertheless the URLs should be taken care of too: if the user completely changes the config then it
            // should be handled as new.
            // Having the ID in the directory name also makes it sure that directories will be unique (no issue with
            // the possibility of hash collision).
            configuration.Id.ToString(CultureInfo.InvariantCulture) + "-" +
            GetMirroringDescriptor(configuration).GetHashCode().ToString(CultureInfo.InvariantCulture);

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
    }
}

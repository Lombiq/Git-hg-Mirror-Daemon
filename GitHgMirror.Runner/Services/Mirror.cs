
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


        public void MirrorRepositories(MirroringConfiguration configuration, MirroringSettings settings)
        {
            var descriptor = GetMirroringDescriptor(configuration);

            Debug.WriteLine("Starting mirroring: " + descriptor);

            var repositoryDirectoryName = GetCloneDirectoryName(configuration);
            var cloneDirectoryParentPath = Path.Combine(settings.RepositoriesDirectoryPath, repositoryDirectoryName[0].ToString()); // A subfolder per clone dir start letter
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
                var isCloned = IsCloned(configuration, settings);

                //Action cdCloneDirectory = () => RunCommandAndLogOutput("cd " + quotedCloneDirectoryPath);

                if (isCloned)
                {
                    Directory.SetLastAccessTimeUtc(cloneDirectoryPath, DateTime.UtcNow);
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
                                _hgCommandExecutor.PullHg(quotedGitCloneUrl, quotedCloneDirectoryPath, settings);
                            }
                            else
                            {
                                RunGitCommandAndMarkException(() =>
                                    _gitCommandExecutor.FetchFromGit(configuration.GitCloneUri, cloneDirectoryPath));
                                _hgCommandExecutor.ImportHistoryFromGit(quotedCloneDirectoryPath, settings);
                            }
                        }
                        else
                        {
                            if (configuration.GitUrlIsHgUrl)
                            {
                                _hgCommandExecutor.CloneHg(quotedGitCloneUrl, quotedCloneDirectoryPath, settings);
                            }
                            else
                            {
                                _hgCommandExecutor.CloneGit(configuration.GitCloneUri, quotedCloneDirectoryPath, settings);
                            }
                        }

                        _hgCommandExecutor.PushWithBookmarks(quotedHgCloneUrl, quotedCloneDirectoryPath, settings);

                        break;
                    case MirroringDirection.HgToGit:
                        if (isCloned)
                        {
                            _hgCommandExecutor.PullHg(quotedHgCloneUrl, quotedCloneDirectoryPath, settings);
                        }
                        else
                        {
                            _hgCommandExecutor.CloneHg(quotedHgCloneUrl, quotedCloneDirectoryPath, settings);
                        }

                        if (configuration.GitUrlIsHgUrl)
                        {
                            _hgCommandExecutor.PushWithBookmarks(quotedGitCloneUrl, quotedCloneDirectoryPath, settings);
                        }
                        else
                        {
                            _hgCommandExecutor.CreateOrUpdateBookmarksForBranches(quotedCloneDirectoryPath, settings);
                            _hgCommandExecutor.ExportHistoryToGit(quotedCloneDirectoryPath, settings);
                            RunGitCommandAndMarkException(() =>
                                _gitCommandExecutor.PushToGit(configuration.GitCloneUri, cloneDirectoryPath));
                        }

                        break;
                    case MirroringDirection.TwoWay:
                        Action syncHgAndGitHistories = () =>
                            {
                                _hgCommandExecutor.CreateOrUpdateBookmarksForBranches(quotedCloneDirectoryPath, settings);
                                _hgCommandExecutor.ExportHistoryToGit(quotedCloneDirectoryPath, settings);

                                // This will clear all commits int he git repo that aren't in the git remote repo but 
                                // add changes that were added to the git repo.
                                RunGitCommandAndMarkException(() =>
                                    _gitCommandExecutor.FetchFromGit(configuration.GitCloneUri, cloneDirectoryPath));
                                _hgCommandExecutor.ImportHistoryFromGit(quotedCloneDirectoryPath, settings);

                                // Updating bookmarks which may have shifted after importing from git. This way the
                                // export to git will create a git repo with history identical to the hg repo.
                                _hgCommandExecutor.CreateOrUpdateBookmarksForBranches(quotedCloneDirectoryPath, settings);
                                _hgCommandExecutor.ExportHistoryToGit(quotedCloneDirectoryPath, settings);

                                RunGitCommandAndMarkException(() =>
                                    _gitCommandExecutor.PushToGit(configuration.GitCloneUri, cloneDirectoryPath));
                            };

                        if (isCloned)
                        {
                            _hgCommandExecutor.PullHg(quotedHgCloneUrl, quotedCloneDirectoryPath, settings);

                            if (configuration.GitUrlIsHgUrl)
                            {
                                _hgCommandExecutor.PullHg(quotedGitCloneUrl, quotedCloneDirectoryPath, settings);
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
                                _hgCommandExecutor.CloneHg(quotedGitCloneUrl, quotedCloneDirectoryPath, settings);
                                _hgCommandExecutor.PullHg(quotedHgCloneUrl, quotedCloneDirectoryPath, settings);
                            }
                            else
                            {
                                // We need to start with cloning the hg repo. Otherwise cloning the git repo, then
                                // pulling from the hg repo would yield a "repository unrelated" error, even if the git
                                // repo was created from the hg repo. For an explanation see: 
                                // http://stackoverflow.com/questions/17240852/hg-git-clone-from-github-gives-abort-repository-is-unrelated
                                _hgCommandExecutor.CloneHg(quotedHgCloneUrl, quotedCloneDirectoryPath, settings);

                                syncHgAndGitHistories();
                            }
                        }


                        _hgCommandExecutor.PushWithBookmarks(quotedHgCloneUrl, quotedCloneDirectoryPath, settings);

                        if (configuration.GitUrlIsHgUrl)
                        {
                            _hgCommandExecutor.PushWithBookmarks(quotedGitCloneUrl, quotedCloneDirectoryPath, settings);
                        }


                        break;
                }

                Debug.WriteLine("Finished mirroring: " + descriptor);
            }
            catch (Exception ex)
            {
                if (ex.IsFatal()) throw;

                // We should dispose the command runners so the folder is not locked by the command line.
                Dispose();
                // Waiting a bit for any file locks or leases to be disposed even though CommandRunners and processes 
                // were killed.
                Thread.Sleep(10000);

                var exceptionMessage = string.Format(
                    "An error occured while running commands when mirroring the repositories {0} and {1} in direction {2}. Mirroring will be re-started next time.",
                    configuration.HgCloneUri,
                    configuration.GitCloneUri,
                    configuration.Direction);

                try
                {
                    // Re-cloning a repo is costly. During local debugging you can flip this variable from the
                    // Immediate Window to prevent it if necessary too.
                    var continueWithRepoFolderDelete = true;

                    if (ex.Data.Contains("IsGitException"))
                    {
                        exceptionMessage += " The error was a git error.";

                        try
                        {
                            DeleteDirectoryIfExists(GitCommandExecutor.GetGitDirectoryPath(cloneDirectoryPath));

                            exceptionMessage += " Thus just the git folder was removed.";
                            continueWithRepoFolderDelete = false;
                        }
                        catch (Exception gitDirectoryDeleteException)
                        {
                            if (gitDirectoryDeleteException.IsFatal()) throw;

                            exceptionMessage += 
                                " While the removal of just the git folder was attempted it failed with the following exception, thus the deletion of the whole repository folder will be attempted: " + 
                                gitDirectoryDeleteException;

                            // We'll continue with the repo folder removal below.
                        }
                    }

                    if (continueWithRepoFolderDelete)
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

        public bool IsCloned(MirroringConfiguration configuration, MirroringSettings settings)
        {
            var repositoryDirectoryName = GetCloneDirectoryName(configuration);
            var cloneDirectoryParentPath = Path.Combine(settings.RepositoriesDirectoryPath, repositoryDirectoryName[0].ToString()); // A subfolder per clone dir start letter
            var cloneDirectoryPath = Path.Combine(cloneDirectoryParentPath, repositoryDirectoryName);
            // Also checking if the directory is empty. If yes, it was a failed attempt and really the repo is not cloned.
            return Directory.Exists(cloneDirectoryPath) && Directory.EnumerateFileSystemEntries(cloneDirectoryPath).Any();
        }

        public override void Dispose()
        {
            base.Dispose();

            _hgCommandExecutor.Dispose();
            _gitCommandExecutor.Dispose();
        }


        /// <summary>
        /// Runs a git command and if it throws an exception it will mark the exception as coming from git. This allows
        /// subsequent clean-up to only remove the git folder instead of the whole repo folder, sparing hg re-cloning.
        /// </summary>
        private void RunGitCommandAndMarkException(Action commandRunner)
        {
            try
            {
                commandRunner();
            }
            catch (Exception ex)
            {
                if (ex.IsFatal()) throw;

                ex.Data["IsGitException"] = true;

                throw;
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
    }
}

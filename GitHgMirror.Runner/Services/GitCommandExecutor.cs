using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LibGit2Sharp;

namespace GitHgMirror.Runner.Services
{
    internal class GitCommandExecutor : CommandExecutorBase
    {
        public GitCommandExecutor(EventLog eventLog)
            : base(eventLog)
        {
        }


        public void PushToGit(Uri gitCloneUri, string cloneDirectoryPath)
        {
            // The git directory won't exist if the hg repo is empty (gexport won't do anything).
            if (!Directory.Exists(GetGitDirectoryPath(cloneDirectoryPath))) return;

            // Git repos should be pushed with git as otherwise large (even as large as 15MB) pushes can fail.

            try
            {
                RunGitOperationOnClonedRepo(gitCloneUri, cloneDirectoryPath, repository =>
                {
                    _eventLog.WriteEntry(
                        "Starting to push to git repo: " + gitCloneUri + " (" + cloneDirectoryPath + ").",
                        EventLogEntryType.Information);

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

                    _eventLog.WriteEntry(
                        "Finished pushing to git repo: " + gitCloneUri + " (" + cloneDirectoryPath + ").",
                        EventLogEntryType.Information);
                });
            }
            catch (LibGit2SharpException ex)
            {
                // These will be the messages of an exception thrown when a large push times out. So we'll re-try pushing
                // commit by commit.
                if (!ex.Message.Contains("Failed to write chunk footer: The operation timed out") &&
                    !ex.Message.Contains("Failed to write chunk footer: The connection with the server was terminated abnormally"))
                {
                    throw;
                }

                _eventLog.WriteEntry(
                    "Pushing to the follwing git repo timed out even after retries: " + gitCloneUri + " (" + cloneDirectoryPath + "). This can mean that the push was simply too large. Trying pushing again, commit by commit.",
                    EventLogEntryType.Warning);

                RunCommandAndLogOutput("cd " + GetGitDirectoryPath(cloneDirectoryPath).EncloseInQuotes());

                RunGitOperationOnClonedRepo(gitCloneUri, cloneDirectoryPath, repository =>
                {
                    // Since we can only push a given commit if we also know its branch we need to iterate through them.
                    // This won't push tags but that will be taken care of next time with the above standard push logic.
                    foreach (var branch in repository.Branches)
                    {
                        // We can't use push by commit hash (as described on 
                        // http://stackoverflow.com/questions/3230074/git-pushing-specific-commit) with libgit2 because
                        // of lack of support (see: https://github.com/libgit2/libgit2/issues/3178). So we need to use
                        // git directly.
                        // This is super-slow as it iterates over every commit in every branch (and a commit can be in
                        // multiple branches), but will surely work.

                        // It's costly to iterate over the Commits collection but it could also potentially consume too 
                        // much memory to enumerate the whole collection once and keep it in memory. Thus we work in
                        // batches.

                        var commits = repository.Commits.QueryBy(new CommitFilter { Since = branch });
                        var commitCount = commits.Count();
                        var batchSize = 100;
                        var currentBatchSkip = commitCount;
                        var currentBatch = Enumerable.Empty<Commit>();

                        var firstCommitOfBranch = true;

                        do
                        {
                            currentBatchSkip = currentBatchSkip - batchSize;
                            if (currentBatchSkip < 0) currentBatchSkip = 0;

                            // We need to push the oldest commit first, so need to do a reverse.
                            currentBatch = commits.Skip(currentBatchSkip).Reverse();

                            foreach (var commit in currentBatch)
                            {
                                var sha = commit.Sha;

                                _eventLog.WriteEntry(
                                    "Starting to push commit " + sha + " to the branch " + branch.Name + " in the git repo: " + gitCloneUri + " (" + cloneDirectoryPath + ").",
                                    EventLogEntryType.Information);


                                var tryCount = 0;
                                var reRunGitPush = false;

                                do
                                {
                                    try
                                    {
                                        tryCount++;
                                        reRunGitPush = false;

                                        // The first commit for a new remote branch should use the "refs/heads/" prefix, 
                                        // others just the branch name.
                                        var branchName = branch.Name;
                                        if (firstCommitOfBranch) branchName = "refs/heads/" + branchName;

                                        // The --mirror switch can't be used with refspec push.
                                        RunCommandAndLogOutput(
                                            "git push " +
                                            gitCloneUri.ToGitUrl().EncloseInQuotes() + " "
                                            + sha + ":" + branchName + " --follow-tags");
                                    }
                                    catch (CommandException commandException)
                                    {
                                        if (IsGitExceptionRealError(commandException) &&
                                            // When trying to re-push a commit we'll get an error like below, but this isn't
                                            // an issue:
                                            // ! [rejected]        b028f04f5092cb47db015dd7d9bfc2ad8cd8ce98 -> master (non-fast-forward)
                                            !commandException.Error.Contains(" ! [rejected]"))
                                        {
                                            // Pushing commit by commit is very slow, thus restarting from the beginning
                                            // is tedious. Thus if pushing a git commit happens to fail then re-try on
                                            // this micro level first.
                                            if (tryCount < 3)
                                            {
                                                _eventLog.WriteEntry(
                                                    "Pushing commit " + sha + " to the branch " + branch.Name + 
                                                    " in the git repo: " + gitCloneUri + " (" + cloneDirectoryPath + 
                                                    ") failed with the following exception: " + commandException.ToString() +
                                                    "This was try #" + tryCount + ", retrying.",
                                                    EventLogEntryType.Warning);
                                                reRunGitPush = true;
                                                // Waiting a bit so maybe the error will go away if it was temporary.
                                                Thread.Sleep(10000);
                                            }
                                            else throw;
                                        }
                                    }  
                                } while (reRunGitPush);


                                _eventLog.WriteEntry(
                                    "Finished pushing commit " + sha + " to the branch " + branch.Name + " in the git repo: " + gitCloneUri + " (" + cloneDirectoryPath + ").",
                                    EventLogEntryType.Information);
                            }
                        } while (currentBatchSkip != 0);

                        var currentCommit = repository.Commits.First();
                        var firstParentDepth = 0;
                        while (currentCommit.Parents.Any())
                        {
                            var firstParent = currentCommit.Parents.First();
                            currentCommit = firstParent;
                            firstParentDepth++;
                        }
                    }
                });

                _eventLog.WriteEntry(
                    "Finished commit by commit pushing to the git repo: " + gitCloneUri + " (" + cloneDirectoryPath + ").",
                    EventLogEntryType.Information);
            }
        }

        public void FetchFromGit(Uri gitCloneUri, string cloneDirectoryPath)
        {
            var gitDirectoryPath = GetGitDirectoryPath(cloneDirectoryPath);
            // The git directory won't exist if the hg repo is empty (gexport won't do anything).
            if (!Directory.Exists(gitDirectoryPath))
            {
                RunLibGit2SharpOperationWithRetry(gitCloneUri, cloneDirectoryPath, () =>
                {
                    _eventLog.WriteEntry(
                        "Starting to clone git repo: " + gitCloneUri + " (" + cloneDirectoryPath + ").",
                        EventLogEntryType.Information);

                    Repository.Clone(gitCloneUri.ToGitUrl(), gitDirectoryPath, new CloneOptions { IsBare = true });

                    _eventLog.WriteEntry(
                        "Finished cloning git repo: " + gitCloneUri + " (" + cloneDirectoryPath + ").",
                        EventLogEntryType.Information);
                });
            }
            else
            {
                // Unfortunately this won't fetch tags for some reason. TagFetchMode.All won't help either...
                RunGitOperationOnClonedRepo(gitCloneUri, cloneDirectoryPath, repository =>
                {
                    _eventLog.WriteEntry(
                        "Starting to fetch from git repo: " + gitCloneUri + " (" + cloneDirectoryPath + ").",
                        EventLogEntryType.Information);

                    repository.Network.Fetch(repository.Network.Remotes["origin"], new[] { "+refs/*:refs/*" });

                    _eventLog.WriteEntry(
                        "Finished fetching from git repo: " + gitCloneUri + " (" + cloneDirectoryPath + ").",
                        EventLogEntryType.Information);
                });
            }
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
        private void RunLibGit2SharpOperationWithRetry(
            Uri gitCloneUri,
            string cloneDirectoryPath,
            Action operation,
            int retryCount = 0)
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
                    ex.Message.Contains("Request failed with status code: 403") ||
                    ex is RepositoryNotFoundException)
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


        private static string GetGitDirectoryPath(string cloneDirectoryPath)
        {
            return Path.Combine(cloneDirectoryPath, ".hg", "git");
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

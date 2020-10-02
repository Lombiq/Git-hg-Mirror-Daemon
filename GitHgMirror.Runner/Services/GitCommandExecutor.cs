using LibGit2Sharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace GitHgMirror.Runner.Services
{
    internal class GitCommandExecutor : CommandExecutorBase
    {
        public GitCommandExecutor(EventLog eventLog)
            : base(eventLog)
        {
        }


        public void PushToGit(Uri gitCloneUri, string cloneDirectoryPath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The git directory won't exist if the hg repo is empty (gexport won't do anything).
            if (!Directory.Exists(GetGitDirectoryPath(cloneDirectoryPath))) return;

            // Git repos should be pushed with git as otherwise large (even as large as 15MB) pushes can fail.

            try
            {
                RunGitOperationOnClonedRepo(gitCloneUri, cloneDirectoryPath, (repository, remoteName) =>
                {
                    _eventLog.WriteEntry(
                        "Starting to push to git repo: " + gitCloneUri + " (" + cloneDirectoryPath + ").",
                        EventLogEntryType.Information);

                    // Refspec patterns on push are not supported, see: http://stackoverflow.com/a/25721274/220230
                    // So can't use "+refs/*:refs/*" here, must iterate.
                    foreach (var reference in repository.Refs)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        // Having "+" + reference.CanonicalName + ":" + reference.CanonicalName as the refspec here
                        // would be force push and completely overwrite the remote repo's content. This would always
                        // succeed no matter what is there but could wipe out changes made between the repo was fetched
                        // and pushed.
                        repository.Network.Push(repository.Network.Remotes[remoteName], reference.CanonicalName);
                    }

                    _eventLog.WriteEntry(
                        "Finished pushing to git repo: " + gitCloneUri + " (" + cloneDirectoryPath + ").",
                        EventLogEntryType.Information);
                });
            }
            catch (LibGit2SharpException ex)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // These will be the messages of an exception thrown when a large push times out. So we'll re-try pushing
                // commit by commit.
                if (!ex.Message.Contains("Failed to write chunk footer: The operation timed out") &&
                    !ex.Message.Contains("Failed to write chunk footer: The connection with the server was terminated abnormally") &&
                    !ex.Message.Contains("Failed to receive response: The server returned an invalid or unrecognized response"))
                {
                    throw;
                }

                _eventLog.WriteEntry(
                    "Pushing to the following git repo timed out even after retries: " + gitCloneUri + " (" +
                    cloneDirectoryPath + "). This can mean that the push was simply too large. Trying pushing again, commit by commit.",
                    EventLogEntryType.Warning);

                CdDirectory(GetGitDirectoryPath(cloneDirectoryPath).EncloseInQuotes());

                RunGitOperationOnClonedRepo(gitCloneUri, cloneDirectoryPath, (repository, remoteName) =>
                {
                    var pushCount = 0;

                    var remoteReferences = Repository
                        .ListRemoteReferences(gitCloneUri.ToGitUrl())
                        .Select(reference => reference.TargetIdentifier)
                        .ToArray();

                    // Since we can only push a given commit if we also know its branch we need to iterate through them.
                    // This won't push tags but that will be taken care of next time with the above standard push logic.
                    foreach (var branch in repository.Branches)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        // We can't use push by commit hash (as described on
                        // http://stackoverflow.com/questions/3230074/git-pushing-specific-commit) with libgit2 because
                        // of lack of support (see: https://github.com/libgit2/libgit2/issues/3178). So we need to use
                        // git directly.
                        // This is super-slow as it iterates over every commit in every branch (and a commit can be in
                        // multiple branches), but will surely work.

                        // To avoid re-pushing already pushed commits we try to start from an already existing remote
                        // reference.
                        var commitFilter = new CommitFilter
                        {
                            IncludeReachableFrom = branch,
                            SortBy = CommitSortStrategies.Reverse,
                        };
                        var commits = repository.Commits.QueryBy(commitFilter);
                        var commitCount = commits.Count();

                        if (remoteReferences.Any())
                        {
                            // Searching for the remote reference that filters out the most commits for this branch.
                            var i = 0;
                            while (commitCount > 0 && i < remoteReferences.Length)
                            {
                                // If both IncludeReachableFrom and ExcludeReachableFrom is the same then all commits
                                // are filtered out.
                                if (remoteReferences[i] != branch.CanonicalName)
                                {
                                    // Since the filtering will be lazily evaluated the CommitFilter object needs to be
                                    // copied, otherwise the last one would take effect.
                                    var filteredCommitFilter = new CommitFilter
                                    {
                                        IncludeReachableFrom = commitFilter.IncludeReachableFrom,
                                        ExcludeReachableFrom = remoteReferences[i],
                                        SortBy = commitFilter.SortBy,
                                    };

                                    var filteredCommits = repository.Commits.QueryBy(filteredCommitFilter);
                                    var filteredCommitCount = filteredCommits.Count();

                                    if (filteredCommitCount < commitCount)
                                    {
                                        commits = filteredCommits;
                                        commitCount = filteredCommitCount;
                                    }
                                }

                                i++;
                            }
                        }

                        // It's costly to iterate over the Commits collection but it could also potentially consume too
                        // much memory to enumerate the whole collection once and keep it in memory. Thus we work in
                        // batches.
                        const int batchSize = 100;
                        var currentBatchSkip = 0;
                        IEnumerable<Commit> currentBatch;

                        do
                        {
                            currentBatch = commits.Skip(currentBatchSkip).Take(batchSize);

                            foreach (var commit in currentBatch)
                            {
                                cancellationToken.ThrowIfCancellationRequested();

                                var sha = commit.Sha;

                                _eventLog.WriteEntry(
                                    "Starting to push commit " + sha + " to the branch " + branch.FriendlyName + " in the git repo: " + gitCloneUri + " (" + cloneDirectoryPath + ").",
                                    EventLogEntryType.Information);


                                var tryCount = 0;
                                var reRunGitPush = false;

                                do
                                {
                                    try
                                    {
                                        tryCount++;
                                        reRunGitPush = false;

                                        var branchName = "refs/heads/" + branch.FriendlyName;

                                        // The --mirror switch can't be used with refspec push.
                                        RunCommandAndLogOutput(
                                            "git push " +
                                            gitCloneUri.ToGitUrl().EncloseInQuotes() + " "
                                            + sha + ":" + branchName + " --follow-tags");

                                        pushCount++;

                                        // Let's try a normal push every 300 commits. If it succeeds then the mirroring
                                        // can finish faster (otherwise it could even time out).
                                        // This may be a larger number than in hg revision by revision pulling because
                                        // git commits can be repeated among branches, so the number of commits pushed
                                        // can be lower than the number of push operations.
                                        if (pushCount > 500)
                                        {
                                            PushToGit(gitCloneUri, cloneDirectoryPath, cancellationToken);
                                            return;
                                        }
                                    }
                                    catch (CommandException commandException)
                                    {
                                        if (commandException.IsGitExceptionRealError() &&
                                            // When trying to re-push a commit we'll get an error like below, but this
                                            // isn't an issue:
                                            // ! [rejected]        b028f04f5092cb47db015dd7d9bfc2ad8cd8ce98 -> master (non-fast-forward)
                                            !commandException.Error.Contains(" ! [rejected]"))
                                        {
                                            // Pushing commit by commit is very slow, thus restarting from the beginning
                                            // is tedious. Thus if pushing a git commit happens to fail then re-try on
                                            // this micro level first.
                                            if (tryCount < 3)
                                            {
                                                _eventLog.WriteEntry(
                                                    "Pushing commit " + sha + " to the branch " + branch.FriendlyName +
                                                    " in the git repo: " + gitCloneUri + " (" + cloneDirectoryPath +
                                                    ") failed with the following exception: " + commandException +
                                                    "This was try #" + tryCount + ", retrying.",
                                                    EventLogEntryType.Warning);
                                                reRunGitPush = true;

                                                // Waiting a bit so maybe the error will go away if it was temporary.
                                                Thread.Sleep(30000);
                                            }
                                            else
                                            {
                                                throw;
                                            }
                                        }
                                    }
                                }
                                while (reRunGitPush);


                                _eventLog.WriteEntry(
                                    "Finished pushing commit " + sha + " to the branch " + branch.FriendlyName +
                                    " in the git repo: " + gitCloneUri + " (" + cloneDirectoryPath + ").",
                                    EventLogEntryType.Information);
                            }

                            currentBatchSkip += batchSize;
                        }
                        while (currentBatchSkip < commitCount);
                    }
                });

                _eventLog.WriteEntry(
                    "Finished commit by commit pushing to the git repo: " + gitCloneUri + " (" + cloneDirectoryPath + ").",
                    EventLogEntryType.Information);
            }
        }

        public void FetchOrCloneFromGit(
            Uri gitCloneUri,
            string cloneDirectoryPath,
            bool useLibGit2Sharp,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

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
                RunGitOperationOnClonedRepo(gitCloneUri, cloneDirectoryPath, (repository, remoteName) =>
                {
                    _eventLog.WriteEntry(
                        "Starting to fetch from git repo: " + gitCloneUri + " (" + cloneDirectoryPath + ").",
                        EventLogEntryType.Information);

                    // The LibGit2Sharp version will work well when fetching updates into repos that were cloned with
                    // hg-git from a git repo. The git.exe version will work for two-way mirrors...
                    if (useLibGit2Sharp)
                    {
                        // We can't just use the +refs/*:refs/* refspec since on GitHub PRs have their own specials
                        // refs as refs/pull/[ID]/head and refs/pull/[ID]/merge refs. Pushing a latter ref merges the
                        // PR, what of course we don't want. So we need to filter just the interesting refs.
                        // Also we really shouldn't fetch and push other namespaces like meta/config either, see:
                        // https://groups.google.com/forum/#!topic/repo-discuss/zpqpPpHAwSM
                        Commands.Fetch(repository, remoteName, new[] { "+refs/heads/*:refs/heads/*" }, null, null);
                        Commands.Fetch(repository, remoteName, new[] { "+refs/tags/*:refs/tags/*" }, null, null);
                    }
                    else
                    {
                        CdDirectory(gitDirectoryPath.EncloseInQuotes());

                        // Tried to use LibGit2Sharp for fetching but using the "+refs/heads/*:refs/heads/*" and
                        // "+refs/tags/*:refs/tags/*" refspec wipes out changes export from hg.
                        try
                        {
                            RunCommandAndLogOutput("git fetch --tags \"origin\"");
                        }
                        catch (CommandException commandException) when (!commandException.IsGitExceptionRealError())
                        {
                            // Ignoring false alarms.
                        }
                    }

                    _eventLog.WriteEntry(
                        "Finished fetching from git repo: " + gitCloneUri + " (" + cloneDirectoryPath + ").",
                        EventLogEntryType.Information);
                });
            }
        }


        private void RunGitOperationOnClonedRepo(Uri gitCloneUri, string cloneDirectoryPath, Action<Repository, string> operation) =>
            RunLibGit2SharpOperationWithRetry(gitCloneUri, cloneDirectoryPath, () =>
            {
                using var repository = new Repository(GetGitDirectoryPath(cloneDirectoryPath));
                // Keeping a configured remote for each repo URL the repository is synced with. This is necessary
                // because some LibGit2Sharp operations need one, can't operate with just a clone URL.

                var remoteName = "origin";
                var gitUrl = gitCloneUri.ToGitUrl();

                if (repository.Network.Remotes["origin"] == null)
                {
                    repository.AddMirrorRemote("origin", gitUrl);
                }
                else
                {
                    var existingOtherRemote = repository.Network.Remotes
                        .SingleOrDefault(remote => remote.Url == gitUrl);

                    if (existingOtherRemote != null)
                    {
                        remoteName = existingOtherRemote.Name;
                    }
                    else
                    {
                        remoteName = "remote" + repository.Network.Remotes.Count();
                        repository.AddMirrorRemote(remoteName, gitUrl);
                    }
                }


                operation(repository, remoteName);
            });

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
                // It's readable.
#pragma warning disable S1067 // Expressions should not be too complex
                if (ex.Message.Contains("Request failed with status code: 404") ||
                    ex.Message.Contains("Request failed with status code: 401") ||
                    ex.Message.Contains("Request failed with status code: 403") ||
                    ex.Message.Contains("Cannot push because a reference that you are trying to update on the remote contains commits that are not present locally.") ||
                    ex.Message.Contains("Cannot push non-fastforwardable reference") ||
                    ex is RepositoryNotFoundException)
#pragma warning restore S1067 // Expressions should not be too complex
                {
                    throw;
                }

                var errorDescriptor =
                    Environment.NewLine + "Operation attempted with the " + gitCloneUri.ToGitUrl() + " repository (directory: " + cloneDirectoryPath + ")" +
                    Environment.NewLine + ex +
                    Environment.NewLine + "Operation: " + Environment.NewLine +
                    // Removing first two lines from the stack trace that contain the stack trace retrieval itself.
                    string.Join(Environment.NewLine, Environment.StackTrace.Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries).Skip(2));

                // We allow 3 tries.
                if (retryCount < 2)
                {
                    _eventLog.WriteEntry(
                        "A LibGit2Sharp operation failed " + (retryCount + 1) + " time(s) but will be re-tried." + errorDescriptor,
                        EventLogEntryType.Warning);

                    // Letting temporary issues resolve themselves.
                    Thread.Sleep(30000);

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


        public static string GetGitDirectoryPath(string cloneDirectoryPath) => Path.Combine(cloneDirectoryPath, ".hg", "git");
    }
}

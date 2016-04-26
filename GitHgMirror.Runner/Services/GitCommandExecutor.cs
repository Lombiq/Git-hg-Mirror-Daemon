using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
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
    }
}

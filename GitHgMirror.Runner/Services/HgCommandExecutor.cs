using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace GitHgMirror.Runner.Services
{
    internal class HgCommandExecutor : CommandExecutorBase
    {
        private const string GitBookmarkSuffix = "-git";
        private const string HgGitConfig = " --config git.branch_bookmark_suffix=" + GitBookmarkSuffix;


        public HgCommandExecutor(EventLog eventLog)
            : base(eventLog)
        {
        }


        /// <summary>
        /// Runs the specified command for a git repo in hg.
        /// </summary>
        /// <param name="gitCloneUri">The git clone URI.</param>
        /// <param name="command">
        /// The command, including an optional placeholder for the git URL in form of {url}, e.g.: "clone --noupdate {url}".
        /// </param>
        public void RunGitRepoCommand(Uri gitCloneUri, string command, MirroringSettings settings)
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
                    command,
                    settings);
            }
            else
            {
                RunRemoteHgCommandAndLogOutput("hg " + command + HgGitConfig, settings);
            }
        }

        public void CloneGit(Uri gitCloneUri, string quotedCloneDirectoryPath, MirroringSettings settings)
        {
            CdDirectory(quotedCloneDirectoryPath);
            // Cloning a large git repo will work even when (after cloning the corresponding hg repo) pulling it
            // in will fail with a "the connection was forcibly closed by remote host"-like error. This is why
            // we start with cloning the git repo.
            RunGitRepoCommand(gitCloneUri, "clone --noupdate {url} " + quotedCloneDirectoryPath, settings);
        }

        public void ImportHistoryFromGit(string quotedCloneDirectoryPath, MirroringSettings settings)
        {
            CdDirectory(quotedCloneDirectoryPath);
            RunHgCommandAndLogOutput("hg gimport" + HgGitConfig, settings);
        }

        public void ExportHistoryToGit(string quotedCloneDirectoryPath, MirroringSettings settings)
        {
            CdDirectory(quotedCloneDirectoryPath);
            RunHgCommandAndLogOutput("hg gexport" + HgGitConfig, settings);
        }

        public void CloneHg(string quotedHgCloneUrl, string quotedCloneDirectoryPath, MirroringSettings settings)
        {
            try
            {
                RunRemoteHgCommandAndLogOutput(
                    "hg clone --noupdate " + quotedHgCloneUrl + " " + quotedCloneDirectoryPath,
                    settings);
            }
            catch (CommandException ex)
            {
                if (ex.IsHgConnectionTerminatedError())
                {
                    _eventLog.WriteEntry(
                        "Cloning from the Mercurial repo " + quotedHgCloneUrl + " failed because the server terminated the connection. Re-trying by pulling revision by revision.",
                        EventLogEntryType.Warning);

                    RunRemoteHgCommandAndLogOutput(
                        "hg clone --noupdate --rev 0 " + quotedHgCloneUrl + " " + quotedCloneDirectoryPath,
                        settings);

                    CdDirectory(quotedCloneDirectoryPath);
                    PullPerRevisionsHg(quotedHgCloneUrl, settings);
                }
                else throw;
            }
        }

        public void PullHg(string quotedHgCloneUrl, string quotedCloneDirectoryPath, MirroringSettings settings)
        {
            CdDirectory(quotedCloneDirectoryPath);

            try
            {
                RunRemoteHgCommandAndLogOutput("hg pull " + quotedHgCloneUrl, settings);
            }
            catch (CommandException ex)
            {
                if (ex.IsHgConnectionTerminatedError())
                {
                    _eventLog.WriteEntry(
                        "Pulling from the Mercurial repo " + quotedHgCloneUrl + " failed because the server terminated the connection. Re-trying by pulling revision by revision.",
                        EventLogEntryType.Warning);
                    PullPerRevisionsHg(quotedHgCloneUrl, settings);
                }
                else throw;
            }
        }

        public void CreateOrUpdateBookmarksForBranches(string quotedCloneDirectoryPath, MirroringSettings settings)
        {
            CdDirectory(quotedCloneDirectoryPath);

            // Adding bookmarks for all branches so they appear as proper git branches.
            var branchesOutput = RunHgCommandAndLogOutput("hg branches --closed", settings);

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
                var changesetLogOutput = RunHgCommandAndLogOutput("hg log -r " + branch.EncloseInQuotes(), settings);
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
                    RunHgCommandAndLogOutput(
                        "hg bookmark -r " + branch.EncloseInQuotes() + " " + bookmark + " --force",
                        settings);
                }
            }
        }

        public void PushWithBookmarks(string quotedHgCloneUrl, string quotedCloneDirectoryPath, MirroringSettings settings)
        {
            CdDirectory(quotedCloneDirectoryPath);

            var bookmarksOutput = RunHgCommandAndLogOutput("hg bookmarks", settings);

            // There will be at least one bookmark, "master" with a git repo. However with hg-hg mirroring maybe there are no bookmarks.
            if (bookmarksOutput.Contains("no bookmarks set"))
            {
                RunRemoteHgCommandAndLogOutput("hg push --new-branch --force " + quotedHgCloneUrl, settings);
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
                    RunRemoteHgCommandAndLogOutput(
                        "hg push --new-branch --force " + string.Join(" ", bookmarksBatch) + " " + quotedHgCloneUrl,
                        settings);
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


        /// <summary>
        /// Pulling chunks a repo history in chunks of revisions. This will be slow but surely work, even if one
        /// changeset is huge like this one: http://hg.openjdk.java.net/openjfx/9-dev/rt/rev/86d5cbe0c60f (~100MB, 
        /// 11000 files).
        /// </summary>
        private void PullPerRevisionsHg(string quotedHgCloneUrl, MirroringSettings settings)
        {
            var startRevision = RunHgCommandAndLogOutput("hg identify --rev tip --num", settings)
                .Split(new[] { Environment.NewLine }, StringSplitOptions.None)[1];
            var revision = int.Parse(startRevision) + 1;
            var finished = false;
            while (!finished)
            {
                try
                {
                    var output = RunRemoteHgCommandAndLogOutput(
                        "hg pull --rev " + revision + " " + quotedHgCloneUrl,
                        settings);
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

        private string RunRemoteHgCommandAndLogOutput(string hgCommand, MirroringSettings settings, int retryCount = 0)
        {
            var output = "";
            try
            {
                if (settings.MercurialSettings.UseInsecure)
                {
                    hgCommand = hgCommand + " --insecure";
                }
                if (settings.MercurialSettings.UseDebugForRemoteCommands && !settings.MercurialSettings.UseDebug)
                {
                    hgCommand = hgCommand + " --debug";
                }

                output = RunHgCommandAndLogOutput(hgCommand, settings);

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

                    return RunRemoteHgCommandAndLogOutput(hgCommand, settings, ++retryCount);
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

        private string RunHgCommandAndLogOutput(string hgCommand, MirroringSettings settings)
        {
            if (settings.MercurialSettings.UseDebug)
            {
                hgCommand = hgCommand + " --debug";
            }

            if (settings.MercurialSettings.UseTraceback)
            {
                hgCommand = hgCommand + " --traceback";
            }

            return RunCommandAndLogOutput(hgCommand);
        }
    }
}

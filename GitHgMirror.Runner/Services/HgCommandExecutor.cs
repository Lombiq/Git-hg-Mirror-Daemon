using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace GitHgMirror.Runner.Services
{
    /// <remarks>
    /// <para>>
    /// While it would be nice to have cancellation for "atomic" hg operations like clone, apart from that being non-
    /// trivial to add to the current implementation they can also be risky, since such cancellations can corrupt a
    /// repo.
    /// </para>
    /// </remarks>
    internal class HgCommandExecutor : CommandExecutorBase
    {
        private const string GitBookmarkSuffix = "-git";
        private const string HgGitConfig =
            // Setting the suffix for all bookmarks created corresponding to git branches (when importing from git to hg).
            " --config git.branch_bookmark_suffix=" + GitBookmarkSuffix +
            // Enabling the hggit extension.
            " --config extensions.hggit=" +
            // Disabling the mercurial_keyring extension since it will override auth data contained in repo URLs.
            " --config extensions.mercurial_keyring=!" +
            // Disabling the eol extension since it will unnecessarily warn of line ending issues.
            " --config extensions.eol=!";


        public HgCommandExecutor(EventLog eventLog)
            : base(eventLog)
        {
        }


        public void CloneGit(
            Uri gitCloneUri,
            string quotedCloneDirectoryPath,
            MirroringSettings settings,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            CdDirectory(quotedCloneDirectoryPath);
            // Cloning a large git repo will work even when (after cloning the corresponding hg repo) pulling it in will
            // fail with a "the connection was forcibly closed by remote host"-like error. This is why we start with
            // cloning the git repo.
            RunGitRepoCommand(gitCloneUri, "clone --noupdate {url} " + quotedCloneDirectoryPath, settings);
        }

        public void ImportHistoryFromGit(
            string quotedCloneDirectoryPath,
            MirroringSettings settings,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            CdDirectory(quotedCloneDirectoryPath);
            RunHgCommandAndLogOutput(PrefixHgCommandWithHgGitConfig("gimport"), settings);
        }

        public void ExportHistoryToGit(
            string quotedCloneDirectoryPath,
            MirroringSettings settings,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            CdDirectory(quotedCloneDirectoryPath);
            RunHgCommandAndLogOutput(PrefixHgCommandWithHgGitConfig("gexport"), settings);
        }

        public void CloneHg(
            string quotedHgCloneUrl,
            string quotedCloneDirectoryPath,
            MirroringSettings settings,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                RunRemoteHgCommandAndLogOutput(
                    "hg clone --noupdate " + quotedHgCloneUrl + " " + quotedCloneDirectoryPath,
                    settings);
            }
            catch (CommandException ex) when (ex.IsHgConnectionTerminatedError())
            {
                _eventLog.WriteEntry(
                    "Cloning from the Mercurial repo " + quotedHgCloneUrl + " failed because the server terminated the connection. Re-trying by pulling revision by revision.",
                    EventLogEntryType.Warning);

                RunRemoteHgCommandAndLogOutput(
                    "hg clone --noupdate --rev 0 " + quotedHgCloneUrl + " " + quotedCloneDirectoryPath,
                    settings);

                cancellationToken.ThrowIfCancellationRequested();

                PullPerRevisionsHg(quotedHgCloneUrl, quotedCloneDirectoryPath, settings, cancellationToken);
            }
        }

        public void PullHg(
            string quotedHgCloneUrl,
            string quotedCloneDirectoryPath,
            MirroringSettings settings,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            CdDirectory(quotedCloneDirectoryPath);

            try
            {
                RunRemoteHgCommandAndLogOutput("hg pull " + quotedHgCloneUrl, settings);
            }
            catch (CommandException ex) when (ex.IsHgConnectionTerminatedError())
            {
                cancellationToken.ThrowIfCancellationRequested();

                _eventLog.WriteEntry(
                    "Pulling from the Mercurial repo " + quotedHgCloneUrl + " failed because the server terminated the connection. Re-trying by pulling revision by revision.",
                    EventLogEntryType.Warning);

                PullPerRevisionsHg(quotedHgCloneUrl, quotedCloneDirectoryPath, settings, cancellationToken);
            }
        }

        public void CreateOrUpdateBookmarksForBranches(
            string quotedCloneDirectoryPath,
            MirroringSettings settings,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

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
                cancellationToken.ThrowIfCancellationRequested();

                // Need to strip spaces from branch names, see:
                // https://bitbucket.org/durin42/hg-git/issues/163/gexport-fails-on-bookmarks-with-spaces-in
                var bookmark = branch.Replace(' ', '-');

                // Need to strip multiple slashes from branch names, see:
                // https://bitbucket.org/durin42/hg-git/issues/225/gexport-fails-on-bookmarks-with-multiple
                if (bookmark.Count(character => character == '/') > 1)
                {
                    var firstSlashIndex = bookmark.IndexOf('/');
                    bookmark = bookmark.Substring(0, firstSlashIndex) + bookmark.Substring(firstSlashIndex).Replace('/', '-');
                }

                if (branch == "default") bookmark = "master" + GitBookmarkSuffix;
                else bookmark += GitBookmarkSuffix;

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
                      .Where(line => !string.IsNullOrEmpty(line) && line.StartsWith("bookmark:", StringComparison.InvariantCulture))
                      .Select(line => Regex.Match(line, @"bookmark:\s+(.+)(\s|$)").Groups[1].Value);

                if (!existingBookmarks.Any(existingBookmark => existingBookmark.EndsWith(GitBookmarkSuffix, StringComparison.InvariantCulture)))
                {
                    // Need --force so it moves the bookmark if it already exists.
                    RunHgCommandAndLogOutput(
                        "hg bookmark -r " + branch.EncloseInQuotes() + " " + bookmark + " --force",
                        settings);
                }
            }
        }

        public void PushWithBookmarks(
            string quotedHgCloneUrl,
            string quotedCloneDirectoryPath,
            MirroringSettings settings,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            CdDirectory(quotedCloneDirectoryPath);

            var bookmarksOutput = RunHgCommandAndLogOutput("hg bookmarks", settings);

            // There will be at least one bookmark, "master" with a git repo. However with hg-hg mirroring maybe there
            // are no bookmarks.
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

                // Pushing a lot of bookmarks at once would result in a "RuntimeError: maximum recursion depth exceeded"
                // error.
                const int batchSize = 29;
                var bookmarksBatch = bookmarks.Take(batchSize);
                var skip = 0;
                var bookmarkCount = bookmarks.Count();
                while (skip < bookmarkCount)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    RunRemoteHgCommandAndLogOutput(
                        "hg push --new-branch --force " + string.Join(" ", bookmarksBatch) + " " + quotedHgCloneUrl,
                        settings);
                    skip += batchSize;
                    bookmarksBatch = bookmarks.Skip(skip).Take(batchSize);
                    if (bookmarksBatch.Any())
                    {
                        // Bitbucket throttles such requests so we need to slow down. Otherwise we'd get this error:
                        // "remote: Push throttled (max allowable rate: 30 per 60 seconds)." However, this is wrong as
                        // the actual limit is lower at 29.
                        Thread.Sleep(61000);
                    }
                }
            }
        }


        /// <summary>
        /// Runs the specified command for a git repo in hg.
        /// </summary>
        /// <param name="gitCloneUri">The git clone URI.</param>
        /// <param name="command">
        /// The command, including an optional placeholder for the git URL in form of {url}, e.g.: "clone --noupdate {url}".
        /// </param>
        private void RunGitRepoCommand(Uri gitCloneUri, string command, MirroringSettings settings)
        {
            var gitUriBuilder = new UriBuilder(gitCloneUri);
            var userName = gitUriBuilder.UserName;
            var password = gitUriBuilder.Password;
            gitUriBuilder.UserName = null;
            gitUriBuilder.Password = null;
            var gitUri = gitUriBuilder.Uri;
            var quotedGitCloneUrl = gitUri.ToString().EncloseInQuotes();
            command = command.Replace("{url}", quotedGitCloneUrl);

            // Would be quite ugly that way.
#pragma warning disable S3240 // The simplest possible condition syntax should be used
            if (!string.IsNullOrEmpty(userName))
#pragma warning restore S3240 // The simplest possible condition syntax should be used
            {
                RunRemoteHgCommandAndLogOutput(
                    PrefixHgCommandWithHgGitConfig(
                        "--config auth.rc.prefix=" +
                        ("https://" + gitUri.Host).EncloseInQuotes() +
                        " --config auth.rc.username=" +
                        userName.EncloseInQuotes() +
                        " --config auth.rc.password=" +
                        password.EncloseInQuotes() +
                        " " +
                        command),
                    settings);
            }
            else
            {
                RunRemoteHgCommandAndLogOutput(PrefixHgCommandWithHgGitConfig(command), settings);
            }
        }

        /// <summary>
        /// Pulling chunks a repo history in chunks of revisions. This will be slow but surely work, even if one
        /// changeset is huge like this one: http://hg.openjdk.java.net/openjfx/9-dev/rt/rev/86d5cbe0c60f (~100MB,
        /// 11000 files).
        /// </summary>
        private void PullPerRevisionsHg(
            string quotedHgCloneUrl,
            string quotedCloneDirectoryPath,
            MirroringSettings settings,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            CdDirectory(quotedCloneDirectoryPath);

            var startRevision = int.Parse(
                RunHgCommandAndLogOutput("hg identify --rev tip --num", settings).Split(new[] { Environment.NewLine }, StringSplitOptions.None)[1],
                CultureInfo.InvariantCulture);
            var revision = startRevision + 1;

            var finished = false;
            var pullRetryCount = 0;
            while (!finished)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var output = RunRemoteHgCommandAndLogOutput(
                        "hg pull --rev " + revision + " " + quotedHgCloneUrl,
                        settings);

                    finished = output.Contains("no changes found");

                    // Let's try a normal pull every 300 revisions. If it succeeds then the mirroring can finish faster
                    // (otherwise it could even time out).
                    if (!finished && revision - startRevision >= 300)
                    {
                        PullHg(quotedHgCloneUrl, quotedCloneDirectoryPath, settings, cancellationToken);
                        return;
                    }

                    revision++;
                    pullRetryCount = 0;
                }
                catch (CommandException pullException)
                {
                    // This error happens when we try to go beyond existing revisions and it means we reached the end
                    // of the repo history.
                    // Maybe the hg identify command could be used to retrieve the latest revision number instead (see:
                    // https://selenic.com/hg/help/identify) although it says "can't query remote revision number,
                    // branch, or tag" (and even if it could, what if new changes are being pushed?). So using exceptions
                    // for now.
                    if (pullException.Error.Contains("abort: unknown revision "))
                    {
                        finished = true;
                    }
                    else if (pullException.IsHgConnectionTerminatedError() && pullRetryCount < 2)
                    {
                        // If such a pull fails then we can't fall back more, have to retry.

                        // It is used though.
#pragma warning disable S1854 // Unused assignments should be removed
                        pullRetryCount++;
#pragma warning restore S1854 // Unused assignments should be removed

                        // Letting temporary issues resolve themselves.
                        Thread.Sleep(30000);
                    }
                    else
                    {
                        throw;
                    }
                }
            }
        }

        private string RunRemoteHgCommandAndLogOutput(string hgCommand, MirroringSettings settings, int retryCount = 0)
        {
            var output = string.Empty;
            try
            {
                if (settings.MercurialSettings.UseInsecure)
                {
                    hgCommand += " --insecure";
                }

                if (settings.MercurialSettings.UseDebugForRemoteCommands && !settings.MercurialSettings.UseDebug)
                {
                    hgCommand += " --debug";
                }

                output = RunHgCommandAndLogOutput(hgCommand, settings);

                return output;
            }
            catch (CommandException ex)
            {
                // We'll randomly get such errors when interacting with Mercurial as well as with Git, otherwise
                // properly running repos. So we re-try the operation a few times, maybe it'll work...
                if (ex.Error.Contains("EOF occurred in violation of protocol"))
                {
                    if (retryCount >= 5)
                    {
                        throw new IOException(
                            "Couldn't run the following Mercurial command successfully even after " + retryCount +
                                " tries due to an \"EOF occurred in violation of protocol\" error: " + hgCommand,
                            ex);
                    }

                    // Let's wait a bit before re-trying so our prayers can heal the connection in the meantime.
                    Thread.Sleep(10000);

                    return RunRemoteHgCommandAndLogOutput(hgCommand, settings, ++retryCount);
                }

                // Catching warning-level "bitbucket.org certificate with fingerprint .. not verified (check
                // hostfingerprints or web.cacerts config setting)" kind of errors that happen when mirroring happens
                // accessing an insecure host.
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
                hgCommand += " --debug";
            }

            if (settings.MercurialSettings.UseTraceback)
            {
                hgCommand += " --traceback";
            }

            return RunCommandAndLogOutput(hgCommand);
        }


        private static string PrefixHgCommandWithHgGitConfig(string hgCommand) => "hg" + HgGitConfig + " " + hgCommand;
    }
}

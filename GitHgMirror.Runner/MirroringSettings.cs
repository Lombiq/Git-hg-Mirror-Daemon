using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GitHgMirror.Runner
{
    public class MirroringSettings
    {
        /// <summary>
        /// The URL of the web API endpoint to fetch mirroring configurations from.
        /// </summary>
        public Uri ApiEndpointUrl { get; set; }

        /// <summary>
        /// Password of the web API endpoint to fetch mirroring configurations from.
        /// </summary>
        public string ApiPassword { get; set; }

        /// <summary>
        /// Path of the directory where the cloned repositories are stored. Must be an absolute path.
        /// </summary>
        public string RepositoriesDirectoryPath { get; set; }

        /// <summary>
        /// The maximum number of parallel tasks used to run repo synchronization. This can be higher with the
        /// performance of the server increasing, but keep in mind that syncing also generates network traffic.
        /// </summary>
        public int MaxDegreeOfParallelism { get; set; } = 10;

        /// <summary>
        /// The amount of sync runs processed in one batch (executed in one task).
        /// </summary>
        public int BatchSize { get; set; } = 50;

        /// <summary>
        /// The time in seconds on how frequent the number of mirroring configurations will be checked and thus newly 
        /// added configs discovered.
        /// </summary>
        public int SecondsBetweenConfigurationCountChecks { get; set; } = 60;

        /// <summary>
        /// The time in seconds after which a mirroring will be forcefully terminated if it doesn't complete.
        /// </summary>
        public int MirroringTimoutSeconds { get; set; } = 48 * 60 * 60; // 48 hours.

        public MercurialSettings MercurialSettings { get; set; } = new MercurialSettings();
    }


    public class MercurialSettings
    {
        /// <summary>
        /// If set to <c>true</c> all remote Mercurial operations will use the --insecure switch which will make Mercurial
        /// bypass server certificate checks.
        /// </summary>
        public bool UseInsecure { get; set; }

        /// <summary>
        /// If set to <c>true</c> all Mercurial operations will use the --debug switch to show debug information. Useful
        /// if you need to find out details on why certain commands fail.
        /// </summary>
        public bool UseDebug { get; set; }

        /// <summary>
        /// If set to <c>true</c> all remote Mercurial operations will use the --debug switch to show debug information.
        /// Useful if you need to find out details on why certain remote commands fail.
        /// </summary>
        public bool UseDebugForRemoteCommands { get; set; }

        /// <summary>
        /// If set to <c>true</c> all Mercurial operations will use the --traceback switch to show the traceback of
        /// exceptions. Useful if you need to find out details on exceptions.
        /// </summary>
        public bool UseTraceback { get; set; }
    }
}

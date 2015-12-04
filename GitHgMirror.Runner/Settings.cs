using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GitHgMirror.Runner
{
    public class Settings
    {
        public Uri ApiEndpointUrl { get; set; }
        public string ApiPassword { get; set; }

        /// <summary>
        /// Path of the directory where the cloned repositories are stored. Must be an absolute path.
        /// </summary>
        public string RepositoriesDirectoryPath { get; set; }

        /// <summary>
        /// The maximum number of parallel tasks used to run repo synchronization. This can be higher with the
        /// performance of the server increasing, but keep in mind that syncing also generates network traffic.
        /// </summary>
        public int MaxDegreeOfParallelism { get; set; }

        /// <summary>
        /// The amount of sync runs processed in on batch (executed in one task).
        /// </summary>
        public int BatchSize { get; set; }

        public int SecondsBetweenConfigurationCountChecks { get; set; }


        public Settings()
        {
            MaxDegreeOfParallelism = 10;
            BatchSize = 50;
            SecondsBetweenConfigurationCountChecks = 60;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GitHgMirrorRunner
{
    public class Settings
    {
        public Uri ApiEndpointUrl { get; set; }
        public string ApiPassword { get; set; }
        public string HgExePath { get; set; }
        public int ParallelisationDegree { get; set; }
        public int BatchSize { get; set; }
    }
}

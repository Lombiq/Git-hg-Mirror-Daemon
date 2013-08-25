using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GitHgMirrorRunner
{
    public class Runner
    {
        private readonly Settings _settings;
        private readonly EventLog _eventLog;


        public Runner(Settings settings, EventLog eventLog)
        {
            _settings = settings;
            _eventLog = eventLog;
        }


        public void Start()
        {
        }

        public void Stop()
        {
        }
    }
}

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GitHgMirrorDaemon
{
    public partial class GitHgMirrorService : ServiceBase
    {
        public GitHgMirrorService()
        {
            InitializeComponent();
        }

        protected override void OnStart(string[] args)
        {
            serviceEventLog.WriteEntry("GitHgMirrorDaemon started.");

            var timer = new System.Timers.Timer(10000);
            timer.Elapsed += timer_Elapsed;
            timer.Enabled = true;
        }

        void timer_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            ((System.Timers.Timer)sender).Enabled = false;
            serviceEventLog.WriteEntry("valami");
            Thread.Sleep(60000);
            serviceEventLog.WriteEntry("valami2");
            Thread.Sleep(99999999);
        }

        protected override void OnStop()
        {
            serviceEventLog.WriteEntry("GitHgMirrorDaemon stopped.");
        }
    }
}

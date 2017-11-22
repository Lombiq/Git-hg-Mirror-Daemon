using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.ServiceProcess;
using System.Text;
using System.Threading.Tasks;
using CSWindowsServiceRecoveryProperty;

namespace GitHgMirror.Daemon
{
    [RunInstaller(true)]
    public class Installer : System.Configuration.Install.Installer
    {
        public Installer()
        {
            var process = new ServiceProcessInstaller
            {
                Account = ServiceAccount.LocalSystem
            };
            var serviceAdmin = new ServiceInstaller();
            serviceAdmin.StartType = ServiceStartMode.Automatic;
            serviceAdmin.DelayedAutoStart = true;
            serviceAdmin.ServiceName = "GitHgMirrorService";
            serviceAdmin.DisplayName = "Git-hg Mirror Service";
            serviceAdmin.Description = "Runs the mirroring of the repositories.";
            serviceAdmin.AfterInstall += (sender, e) =>
            {
                // Adding failure actions (i.e. what Windows should do when the service crashes) with delay in ms. Code 
                // taken from https://code.msdn.microsoft.com/windowsdesktop/CSWindowsServiceRecoveryPro-2147e7ac/
                // Such crashes should be very rare, but sometimes happen for some reason when there are a lot of errors
                // (like when GitHub goes down).
                var failureActions = new List<SC_ACTION>
                {
                    new SC_ACTION
                    {
                        Type = (int)SC_ACTION_TYPE.RestartService,
                        Delay = 1000 * 60 * 15
                    },
                    new SC_ACTION()
                    {
                        Type = (int)SC_ACTION_TYPE.RestartService,
                        Delay = 1000 * 60 * 60
                    },
                    new SC_ACTION()
                    {
                        Type = (int)SC_ACTION_TYPE.RestartService,
                        Delay = 1000 * 60 * 120
                    }
                };

                ServiceRecoveryProperty.ChangeRecoveryProperty(
                    scName: "GitHgMirrorService",
                    scActions: failureActions,
                    resetPeriod: 60 * 60 * 24 * 1,
                    command: "C:\\Windows\\System32\\cmd.exe /help /fail=%1%",
                    fFailureActionsOnNonCrashFailures: true,
                    rebootMsg: "reboot message");
            };
            Installers.Add(process);
            Installers.Add(serviceAdmin);
        }
    }
}

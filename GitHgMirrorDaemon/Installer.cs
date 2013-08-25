using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.ServiceProcess;
using System.Text;
using System.Threading.Tasks;

namespace GitHgMirrorDaemon
{
    [RunInstaller(true)]
    public class Installer : System.Configuration.Install.Installer
    {
        public Installer()
        {
            var process = new ServiceProcessInstaller();
            process.Account = ServiceAccount.LocalSystem;
            var serviceAdmin = new ServiceInstaller();
            serviceAdmin.StartType = ServiceStartMode.Manual;
            serviceAdmin.ServiceName = "GitHgMirrorService";
            serviceAdmin.DisplayName = "GitHgMirrorService";
            Installers.Add(process);
            Installers.Add(serviceAdmin);
        }
    }
}

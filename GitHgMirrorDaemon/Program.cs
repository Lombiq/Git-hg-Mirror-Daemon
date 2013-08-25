using System;
using System.Collections.Generic;
using System.Configuration.Install;
using System.Linq;
using System.Reflection;
using System.ServiceProcess;
using System.Text;
using System.Threading.Tasks;

namespace GitHgMirrorDaemon
{
    static class Program
    {
        static void Main()
        {
            // Below code taken mostly from http://www.codeproject.com/Articles/27112/Installing-NET-Windows-Services-the-easiest-way
            var service = ServiceController.GetServices().Where(controller => controller.ServiceName == "GitHgMirrorService").SingleOrDefault();

            // Service not installed
            if (service == null)
            {
                SelfInstaller.InstallMe();
            }
            // Service is not starting
            else if (service.Status != ServiceControllerStatus.StartPending)
            {
                SelfInstaller.UninstallMe();
            }
            // Started from the SCM
            else
            {
                System.ServiceProcess.ServiceBase[] servicestorun;
                servicestorun = new System.ServiceProcess.ServiceBase[] { new GitHgMirrorService() };
                ServiceBase.Run(servicestorun);
            }
        }
    }

    static class SelfInstaller
    {
        private static readonly string _exePath = Assembly.GetExecutingAssembly().Location;


        public static bool InstallMe()
        {
            try
            {
                ManagedInstallerClass.InstallHelper(new string[] { _exePath });
            }
            catch
            {
                return false;
            }

            return true;
        }

        public static bool UninstallMe()
        {
            try
            {
                ManagedInstallerClass.InstallHelper(new string[] { "/u", _exePath });
            }
            catch
            {
                return false;
            }

            return true;
        }
    }
}

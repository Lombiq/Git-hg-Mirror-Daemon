using System.Configuration.Install;
using System.Linq;
using System.ServiceProcess;

namespace GitHgMirror.Daemon
{
    public static class Program
    {
        public static void Main()
        {
            // Below code taken mostly from
            // http://www.codeproject.com/Articles/27112/Installing-NET-Windows-Services-the-easiest-way
            var service = ServiceController.GetServices().SingleOrDefault(controller => controller.ServiceName == "GitHgMirrorService");

            if (service == null)
            {
                // Service not installed.
                SelfInstaller.InstallMe();
            }
            else if (service.Status != ServiceControllerStatus.StartPending)
            {
                // Service is not starting.
                SelfInstaller.UninstallMe();
            }
            else
            {
                // Started from the SCM.
                ServiceBase[] servicestorun;
                servicestorun = new ServiceBase[] { new GitHgMirrorService() };
                ServiceBase.Run(servicestorun);
            }
        }
    }

    internal static class SelfInstaller
    {
        private static readonly string _exePath = typeof(SelfInstaller).Assembly.Location;

        public static bool InstallMe()
        {
            try
            {
                ManagedInstallerClass.InstallHelper(new[] { _exePath });
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
                ManagedInstallerClass.InstallHelper(new[] { "/u", _exePath });
            }
            catch
            {
                return false;
            }

            return true;
        }
    }
}

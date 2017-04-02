using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using LibGit2Sharp;

namespace LibGit2Sharp
{
    internal static class RepositoryExtensions
    {
        public static void AddMirrorRemote(this Repository repository, string name, string url)
        {
            repository.Network.Remotes.Add(name, url);
            repository.Config.Set("remote." + name + ".mirror", true);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace System
{
    internal static class UriExtensions
    {
        public static string ToGitUrl(this Uri gitCloneUri)
        {
            return gitCloneUri.ToString().Replace("git+https", "https");
        }
    }
}

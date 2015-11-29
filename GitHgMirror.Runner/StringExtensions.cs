using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GitHgMirror.Runner
{
    internal static class StringExtensions
    {
        public static string EncloseInQuotes(this string text)
        {
            return "\"" + text + "\"";
        }

        public static string WithHgGitConfig(this string command)
        {
            return command + " --config git.branch_bookmark_suffix=-git";
        }
    }
}

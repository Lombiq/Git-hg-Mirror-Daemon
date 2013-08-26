using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GitHgMirror.Runner
{
    static class StringExtensions
    {
        public static string EncloseInQuotes(this string text)
        {
            return "\"" + text + "\"";
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GitHgMirror.CommonTypes
{
    [DebuggerDisplay("{ToString()}")]
    public class MirroringConfiguration
    {
        public int Id { get; set; }
        public Uri HgCloneUri { get; set; }
        public Uri GitCloneUri { get; set; }
        public bool GitUrlIsHgUrl { get; set; }
        public MirroringDirection Direction { get; set; }


        public override string ToString()
        {
            return
                Id + " " +
                HgCloneUri.ToString() + " - " +
                GitCloneUri.ToString() + " " +
                Direction.ToString();
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GitHgMirrorCommonTypes
{
    public enum MirroringDirection
    {
        GitToHg,
        HgToGit,
        TwoWay
    }

    public class MirroringConfiguration
    {
        public Uri HgCloneUri { get; set; }
        public Uri GitCloneUri { get; set; }
        public MirroringDirection Direction { get; set; }
    }
}

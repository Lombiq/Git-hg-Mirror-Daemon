using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace GitHgMirror.CommonTypes
{
    public enum MirroringDirection
    {
        GitToHg,
        HgToGit,
        TwoWay
    }

    public enum MirroringStatus
    {
        New,
        Cloning,
        Syncing,
        Failed
    }
}

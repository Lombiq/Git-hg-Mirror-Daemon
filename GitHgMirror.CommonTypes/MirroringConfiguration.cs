using System;
using System.Diagnostics;

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


        public override string ToString() =>
            Id + " " +
            HgCloneUri + " - " +
            GitCloneUri + " " +
            Direction.ToString();
    }
}

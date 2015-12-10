using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GitHgMirror.CommonTypes
{
    public class MirroringConfiguration
    {
        public int Id { get; set; }
        public Uri HgCloneUri { get; set; }
        public Uri GitCloneUri { get; set; }
        public bool GitUrlIsHgUrl { get; set; }
        public MirroringDirection Direction { get; set; }


        public MirroringConfiguration(MirroringConfiguration previous) : this()
        {
            Id = previous.Id;
            HgCloneUri = previous.HgCloneUri;
            GitCloneUri = previous.GitCloneUri;
            GitUrlIsHgUrl = previous.GitUrlIsHgUrl;
            Direction = previous.Direction;
        }

        public MirroringConfiguration()
        {
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace GitHgMirror.CommonTypes
{
    public class MirroringStatusReport
    {
        public int ConfigurationId { get; set; }
        public MirroringStatus Status { get; set; }
        public int Code { get; set; }
        public string Message { get; set; }
    }
}

namespace GitHgMirror.CommonTypes
{
    public class MirroringStatusReport
    {
        public string ConfigurationId { get; set; }
        public MirroringStatus Status { get; set; }
        public int Code { get; set; }
        public string Message { get; set; }
    }
}

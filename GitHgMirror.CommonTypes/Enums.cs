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
        Failed,
        Disabled
    }
}

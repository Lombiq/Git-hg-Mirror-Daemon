namespace System
{
    internal static class UriExtensions
    {
        public static string ToGitUrl(this Uri gitCloneUri) => gitCloneUri.ToString().Replace("git+https", "https");
    }
}

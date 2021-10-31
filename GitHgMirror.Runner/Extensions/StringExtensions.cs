namespace System
{
    internal static class StringExtensions
    {
        public static string EncloseInQuotes(this string text) => "\"" + text + "\"";
    }
}

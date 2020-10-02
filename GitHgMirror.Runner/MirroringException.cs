using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace GitHgMirror.Runner
{
    [SuppressMessage("Design", "CA1032:Implement standard exception constructors", Justification = "This exception is used in very particular cases.")]
    [SuppressMessage("Usage", "CA2237:Mark ISerializable types with serializable", Justification = "Doesn't need to be serializable.")]
    [SuppressMessage("Major Code Smell", "S3925:\"ISerializable\" should be implemented correctly", Justification = "Doesn't need to be serializable.")]
    public class MirroringException : AggregateException
    {
        public MirroringException(string message)
            : base(message)
        {
        }

        public MirroringException(string message, params Exception[] innerExceptions)
            : base(message, innerExceptions)
        {
        }


        public override string ToString() =>
            typeof(MirroringException).FullName + ": " + Message + Environment.NewLine +
            StackTrace + Environment.NewLine +
            string.Join(Environment.NewLine, InnerExceptions.Select(exception => Environment.NewLine + "---> " + exception));
    }
}

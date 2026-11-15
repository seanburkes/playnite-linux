namespace Playnite.Core.Models;

// The model copy paths in SDK/Exceptions/{ReferenceException,TypeMismatchException}.cs
// use literal messages. LocalizedException's ResourceProvider dependency is not ported.
public class ReferenceException : Exception
{
    public ReferenceException() { }
    public ReferenceException(string message) : base(message) { }
}

public class TypeMismatchException : Exception
{
    public TypeMismatchException() { }
    public TypeMismatchException(string message) : base(message) { }
}

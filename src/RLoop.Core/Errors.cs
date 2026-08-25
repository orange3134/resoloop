using System.Collections.ObjectModel;

namespace RLoop.Core;

public sealed class RLoopException : Exception
{
    public RLoopException(
        string code,
        string message,
        int exitCode = ExitCodes.OperationFailed,
        IReadOnlyDictionary<string, object?>? context = null,
        IReadOnlyList<string>? suggestions = null,
        Exception? innerException = null) : base(message, innerException)
    {
        Code = code;
        ExitCode = exitCode;
        Context = context ?? new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>());
        Suggestions = suggestions ?? Array.Empty<string>();
    }

    public string Code { get; }
    public int ExitCode { get; }
    public IReadOnlyDictionary<string, object?> Context { get; }
    public IReadOnlyList<string> Suggestions { get; }
}

public static class ExitCodes
{
    public const int Success = 0;
    public const int InvalidArguments = 2;
    public const int ConfigurationError = 3;
    public const int ConnectionFailed = 4;
    public const int NotFound = 5;
    public const int ValidationFailed = 6;
    public const int OperationFailed = 7;
    public const int Timeout = 8;
    public const int ExternalToolFailed = 9;
}

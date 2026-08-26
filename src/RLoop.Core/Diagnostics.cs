namespace RLoop.Core;

public sealed record DoctorCheck(
    string Name,
    string Status,
    bool Required,
    string Message,
    string? Suggestion = null);

public sealed record DoctorReport(
    bool Ready,
    string WorkingDirectory,
    string? ProjectConfig,
    IReadOnlyList<DoctorCheck> Checks);

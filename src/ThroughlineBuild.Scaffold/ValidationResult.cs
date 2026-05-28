namespace ThroughlineBuild.Scaffold;

public record ValidationResult(
    bool IsValid,
    IReadOnlyList<ValidationError> Errors,
    IReadOnlyList<ValidationWarning> Warnings);

public record ValidationError(
    string Code,
    string Path,
    string Message);

public record ValidationWarning(
    string Code,
    string Path,
    string Message);

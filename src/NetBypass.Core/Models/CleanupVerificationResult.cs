namespace NetBypass.Core.Models;

public sealed record CleanupVerificationResult(
    bool IsClean,
    IReadOnlyList<string> CompletedChecks,
    IReadOnlyList<string> Issues);

namespace NetBypass.Core.Models;

public sealed record ServiceModule(
    string Id,
    string Name,
    string Category,
    bool IsEnabledByDefault,
    IReadOnlyList<HostEntry> Entries,
    string SourcePath);

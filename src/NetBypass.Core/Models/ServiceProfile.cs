namespace NetBypass.Core.Models;

public sealed record ServiceProfile(
    int SchemaVersion,
    ServiceModule Module,
    IReadOnlyList<string> Strategies,
    IReadOnlyList<HealthCheckDefinition> HealthChecks)
{
    public string Id => Module.Id;
    public string Name => Module.Name;
    public string Category => Module.Category;
}

public sealed record HealthCheckDefinition(
    string TargetAddress,
    string Host,
    int Port,
    string Protocol,
    IReadOnlySet<int> AcceptedHttpStatuses);

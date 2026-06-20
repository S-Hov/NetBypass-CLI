using NetBypass.Core.Models;

namespace NetBypass.Core.Services;

public sealed class ServiceProfileLoader(ModuleLoader? moduleLoader = null)
{
    private readonly ModuleLoader _moduleLoader = moduleLoader ?? new ModuleLoader();

    public IReadOnlyList<ServiceProfile> LoadDirectory(string directory) =>
        _moduleLoader.LoadDirectory(directory)
            .Select(CreateProfile)
            .ToArray();

    public static ServiceProfile CreateProfile(ServiceModule module)
    {
        var healthChecks = module.Entries
            .GroupBy(entry => entry.Address, StringComparer.OrdinalIgnoreCase)
            .Select(group => new HealthCheckDefinition(
                TargetAddress: group.Key,
                Host: group.First().Hostname,
                Port: 443,
                Protocol: "https",
                AcceptedHttpStatuses: Enumerable.Range(200, 300).ToHashSet()))
            .ToArray();

        return new ServiceProfile(
            SchemaVersion: 1,
            Module: module,
            Strategies: ["adaptive-hosts"],
            HealthChecks: healthChecks);
    }
}

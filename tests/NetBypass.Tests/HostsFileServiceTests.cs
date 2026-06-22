using NetBypass.Core.Models;
using NetBypass.Core.Services;
using Xunit;

namespace NetBypass.Tests;

public sealed class HostsFileServiceTests
{
    private static readonly ServiceModule DemoModule = new(
        "demo",
        "Demo",
        "Test",
        false,
        [new HostEntry("1.2.3.4", "example.com")],
        "demo.hosts");

    [Fact]
    public void RemoveManagedBlock_PreservesUserContent()
    {
        var content = $"127.0.0.1 localhost\n{HostsFileService.BeginMarker}\n1.2.3.4 example.com\n{HostsFileService.EndMarker}\n10.0.0.2 intranet";

        var result = HostsFileService.RemoveManagedBlock(content);

        Assert.Contains("127.0.0.1 localhost", result);
        Assert.Contains("10.0.0.2 intranet", result);
        Assert.DoesNotContain("example.com", result);
    }

    [Fact]
    public void BuildManagedBlock_GroupsSelectedModules()
    {
        var result = HostsFileService.BuildManagedBlock([DemoModule]);

        Assert.Contains(HostsFileService.BeginMarker, result);
        Assert.Contains("# Demo [demo]", result);
        Assert.Contains("1.2.3.4\texample.com", result);
        Assert.EndsWith(HostsFileService.EndMarker, result);
    }

    [Fact]
    public void GetState_WithoutManagedBlock_ReturnsInactive()
    {
        using var fixture = new HostsFixture("127.0.0.1 localhost");

        Assert.Equal(HostsState.Inactive, fixture.Service.GetState([DemoModule]));
    }

    [Fact]
    public void GetState_AfterApplyAndReopen_ReturnsActive()
    {
        using var fixture = new HostsFixture("127.0.0.1 localhost");
        fixture.Service.Apply([DemoModule]);

        var reopenedService = new HostsFileService(fixture.HostsPath, fixture.BackupPath);

        Assert.Equal(HostsState.Active, reopenedService.GetState([DemoModule]));
    }

    [Fact]
    public void GetState_WhenSelectionDiffers_ReturnsChangesPending()
    {
        using var fixture = new HostsFixture("127.0.0.1 localhost");
        fixture.Service.Apply([DemoModule]);

        Assert.Equal(HostsState.ChangesPending, fixture.Service.GetState([]));
    }

    [Theory]
    [InlineData("# NETBYPASS-BEGIN\n1.2.3.4 example.com")]
    [InlineData("# NETBYPASS-END")]
    [InlineData("# NETBYPASS-BEGIN\n# NETBYPASS-BEGIN\n# NETBYPASS-END")]
    public void GetState_WithBrokenMarkers_ReturnsCorrupted(string content)
    {
        using var fixture = new HostsFixture(content);

        Assert.Equal(HostsState.Corrupted, fixture.Service.GetState([DemoModule]));
    }

    [Fact]
    public void Disable_RemovesOnlyManagedBlock()
    {
        using var fixture = new HostsFixture("127.0.0.1 localhost\n10.0.0.2 intranet");
        fixture.Service.Apply([DemoModule]);

        fixture.Service.Disable();
        var result = File.ReadAllText(fixture.HostsPath);

        Assert.Contains("127.0.0.1 localhost", result);
        Assert.Contains("10.0.0.2 intranet", result);
        Assert.DoesNotContain("example.com", result);
        Assert.DoesNotContain(HostsFileService.BeginMarker, result);
        Assert.DoesNotContain(HostsFileService.EndMarker, result);
        Assert.Equal(HostsState.Inactive, fixture.Service.GetState([DemoModule]));
        Assert.Empty(Directory.GetFiles(
            Path.GetDirectoryName(fixture.HostsPath)!,
            ".netbypass-*.tmp"));
    }

    [Fact]
    public void Restore_CorruptedBlock_RemovesKnownNetBypassLinesOnly()
    {
        var content = "127.0.0.1 localhost\n# NETBYPASS-BEGIN\n# Demo [demo]\n1.2.3.4 example.com\n10.0.0.2 intranet";
        using var fixture = new HostsFixture(content);

        fixture.Service.Restore([DemoModule]);
        var result = File.ReadAllText(fixture.HostsPath);

        Assert.Contains("127.0.0.1 localhost", result);
        Assert.Contains("10.0.0.2 intranet", result);
        Assert.DoesNotContain("example.com", result);
        Assert.DoesNotContain(HostsFileService.BeginMarker, result);
    }

    private sealed class HostsFixture : IDisposable
    {
        private readonly string _directory;

        public HostsFixture(string content)
        {
            _directory = Path.Combine(Path.GetTempPath(), $"NetBypass.Tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_directory);
            HostsPath = Path.Combine(_directory, "hosts");
            BackupPath = Path.Combine(_directory, "hosts.bak");
            File.WriteAllText(HostsPath, content);
            Service = new HostsFileService(HostsPath, BackupPath);
        }

        public string HostsPath { get; }
        public string BackupPath { get; }
        public HostsFileService Service { get; }

        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}

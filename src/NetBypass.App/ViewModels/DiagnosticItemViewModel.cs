using NetBypass.Core.Models;

namespace NetBypass.App.ViewModels;

public sealed class DiagnosticItemViewModel(ServiceDiagnosticResult result)
{
    public string ServiceName => result.ServiceName;
    public string TargetAddress => result.TargetAddress;
    public string StatusText => result.IsReachable ? "Доступен" : "Недоступен";
    public string StatusColor => result.IsReachable ? "#61D6A3" : "#FF6B7A";
    public string Summary => result.Summary;
    public string CheckedAt => result.CheckedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss");
    public string DnsAddresses => result.ResolvedAddresses.Count > 0
        ? string.Join(", ", result.ResolvedAddresses)
        : "нет ответа";
}

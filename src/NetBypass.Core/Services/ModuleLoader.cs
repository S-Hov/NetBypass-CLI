using System.Net;
using System.Text.RegularExpressions;
using NetBypass.Core.Models;

namespace NetBypass.Core.Services;

public sealed partial class ModuleLoader
{
    private static readonly HashSet<string> LocalHostnames = new(StringComparer.OrdinalIgnoreCase)
    {
        "localhost",
        "localhost.localdomain",
        "ip6-localhost",
        "ip6-loopback",
        "broadcasthost"
    };

    public IReadOnlyList<ServiceModule> LoadDirectory(string directory)
    {
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"Каталог модулей не найден: {directory}");

        return Directory.EnumerateFiles(directory, "*.hosts", SearchOption.TopDirectoryOnly)
            .Select(LoadFile)
            .OrderBy(module => module.Category, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(module => module.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public ServiceModule LoadFile(string path)
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<HostEntry>();
        var hostnames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (line.StartsWith('#'))
            {
                ParseMetadata(line, metadata);
                continue;
            }

            var parts = WhitespaceRegex().Split(line);
            if (parts.Length != 2)
                throw new FormatException($"{path}: неверная строка '{rawLine}'. Ожидается IP и домен.");

            if (!IPAddress.TryParse(parts[0], out var address))
                throw new FormatException($"{path}: неверный IP-адрес '{parts[0]}'.");

            var hostname = parts[1].TrimEnd('.').ToLowerInvariant();
            ValidateEntry(path, address, hostname);

            if (hostnames.Add(hostname))
                entries.Add(new HostEntry(address.ToString(), hostname));
        }

        var id = Required(metadata, "id", path);
        var name = Required(metadata, "name", path);
        var category = Required(metadata, "category", path);
        var enabledByDefault = metadata.TryGetValue("default", out var rawDefault)
            && bool.TryParse(rawDefault, out var parsedDefault)
            && parsedDefault;

        if (entries.Count == 0)
            throw new FormatException($"{path}: модуль не содержит записей.");

        return new ServiceModule(id, name, category, enabledByDefault, entries, path);
    }

    private static void ParseMetadata(string line, IDictionary<string, string> metadata)
    {
        var content = line.TrimStart('#').Trim();
        var separator = content.IndexOf(':');
        if (separator <= 0)
            return;

        metadata[content[..separator].Trim()] = content[(separator + 1)..].Trim();
    }

    private static void ValidateEntry(string path, IPAddress address, string hostname)
    {
        if (IsPrivateOrLocal(address))
            throw new FormatException($"{path}: запрещён локальный адрес '{address}'.");

        if (LocalHostnames.Contains(hostname) || hostname.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            throw new FormatException($"{path}: запрещён локальный домен '{hostname}'.");

        if (!HostnameRegex().IsMatch(hostname))
            throw new FormatException($"{path}: неверный домен '{hostname}'.");
    }

    private static bool IsPrivateOrLocal(IPAddress address)
    {
        if (address.Equals(IPAddress.Any)
            || address.Equals(IPAddress.IPv6Any)
            || IPAddress.IsLoopback(address)
            || address.IsIPv6LinkLocal
            || address.IsIPv6Multicast)
            return true;

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return bytes[0] == 10
                || bytes[0] == 127
                || bytes[0] == 169 && bytes[1] == 254
                || bytes[0] == 172 && bytes[1] is >= 16 and <= 31
                || bytes[0] == 192 && bytes[1] == 168
                || bytes[0] is >= 224;
        }

        // fc00::/7 — unique local IPv6.
        return (bytes[0] & 0xFE) == 0xFC;
    }

    private static string Required(IReadOnlyDictionary<string, string> metadata, string key, string path) =>
        metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new FormatException($"{path}: отсутствует метадата '# {key}: ...'.");

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"^(?=.{1,253}$)(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)*[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex HostnameRegex();
}

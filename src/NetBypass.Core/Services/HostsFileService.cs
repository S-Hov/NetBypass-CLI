using System.Text;
using NetBypass.Core.Models;

namespace NetBypass.Core.Services;

public sealed class HostsFileService
{
    public const string BeginMarker = "# NETBYPASS-BEGIN";
    public const string EndMarker = "# NETBYPASS-END";

    private readonly string _hostsPath;
    private readonly string _backupPath;

    public HostsFileService(string? hostsPath = null, string? backupPath = null)
    {
        _hostsPath = hostsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "drivers", "etc", "hosts");

        _backupPath = backupPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NetBypass", "Backups", "hosts.initial.bak");
    }

    public HostsState GetState(IEnumerable<ServiceModule> selectedModules)
    {
        if (!File.Exists(_hostsPath))
            return HostsState.Corrupted;

        var content = File.ReadAllText(_hostsPath);
        var block = TryGetManagedBlock(content);

        if (block.Kind == ManagedBlockKind.None)
            return HostsState.Inactive;

        if (block.Kind == ManagedBlockKind.Corrupted)
            return HostsState.Corrupted;

        var expected = NormalizeLineEndings(BuildManagedBlock(selectedModules));
        var actual = NormalizeLineEndings(block.Content!);
        return string.Equals(actual, expected, StringComparison.Ordinal)
            ? HostsState.Active
            : HostsState.ChangesPending;
    }

    public void Apply(IEnumerable<ServiceModule> modules)
    {
        var selected = modules.ToArray();
        if (selected.Length == 0)
            throw new InvalidOperationException("Выберите хотя бы один сервис.");

        EnsureBackup();
        var original = File.ReadAllText(_hostsPath);
        var clean = RemoveManagedBlock(original).TrimEnd();
        var block = BuildManagedBlock(selected);
        WriteSafely($"{clean}{Environment.NewLine}{Environment.NewLine}{block}{Environment.NewLine}");
    }

    public void Disable()
    {
        if (!File.Exists(_hostsPath))
            throw new FileNotFoundException("Системный hosts не найден.", _hostsPath);

        EnsureBackup();
        var original = File.ReadAllText(_hostsPath);
        var clean = RemoveManagedBlock(original).TrimEnd();
        WriteSafely($"{clean}{Environment.NewLine}");
    }

    public void Restore(IEnumerable<ServiceModule> knownModules)
    {
        if (!File.Exists(_hostsPath))
            throw new FileNotFoundException("Системный hosts не найден.", _hostsPath);

        EnsureBackup();
        var original = File.ReadAllText(_hostsPath);
        var block = TryGetManagedBlock(original);
        string clean;

        if (block.Kind == ManagedBlockKind.Valid)
        {
            clean = RemoveManagedBlock(original);
        }
        else if (block.Kind == ManagedBlockKind.None)
        {
            clean = original;
        }
        else
        {
            clean = RemoveKnownNetBypassLines(original, knownModules);
        }

        WriteSafely($"{clean.TrimEnd()}{Environment.NewLine}");
    }

    public static string BuildManagedBlock(IEnumerable<ServiceModule> modules)
    {
        var builder = new StringBuilder();
        builder.AppendLine(BeginMarker);
        builder.AppendLine("# Этот блок управляется NetBypass. Не редактируйте его вручную.");

        foreach (var module in modules.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            builder.AppendLine();
            builder.AppendLine($"# {module.Name} [{module.Id}]");
            foreach (var entry in module.Entries)
                builder.AppendLine($"{entry.Address}\t{entry.Hostname}");
        }

        builder.Append(EndMarker);
        return builder.ToString();
    }

    public static string RemoveManagedBlock(string content)
    {
        var start = content.IndexOf(BeginMarker, StringComparison.Ordinal);
        if (start < 0)
            return content;

        var end = content.IndexOf(EndMarker, start, StringComparison.Ordinal);
        if (end < 0)
            throw new InvalidDataException("Найден незавершённый блок NetBypass. Исправьте hosts вручную.");

        end += EndMarker.Length;
        while (end < content.Length && content[end] is '\r' or '\n')
            end++;

        return content.Remove(start, end - start);
    }

    private static ManagedBlockResult TryGetManagedBlock(string content)
    {
        var starts = FindAll(content, BeginMarker);
        var ends = FindAll(content, EndMarker);

        if (starts.Count == 0 && ends.Count == 0)
            return new ManagedBlockResult(ManagedBlockKind.None, null);

        if (starts.Count != 1 || ends.Count != 1 || ends[0] < starts[0])
            return new ManagedBlockResult(ManagedBlockKind.Corrupted, null);

        var endExclusive = ends[0] + EndMarker.Length;
        return new ManagedBlockResult(
            ManagedBlockKind.Valid,
            content[starts[0]..endExclusive]);
    }

    private static IReadOnlyList<int> FindAll(string content, string marker)
    {
        var positions = new List<int>();
        var index = 0;

        while ((index = content.IndexOf(marker, index, StringComparison.Ordinal)) >= 0)
        {
            positions.Add(index);
            index += marker.Length;
        }

        return positions;
    }

    private static string RemoveKnownNetBypassLines(
        string content,
        IEnumerable<ServiceModule> knownModules)
    {
        var knownEntries = knownModules
            .SelectMany(module => module.Entries)
            .Select(entry => $"{entry.Address} {entry.Hostname}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var knownHeaders = knownModules
            .Select(module => $"# {module.Name} [{module.Id}]")
            .ToHashSet(StringComparer.Ordinal);

        var result = new List<string>();
        foreach (var rawLine in NormalizeLineEndings(content).Split('\n'))
        {
            var line = rawLine.Trim();
            var normalizedEntry = string.Join(
                " ",
                line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

            if (line == BeginMarker
                || line == EndMarker
                || line == "# Этот блок управляется NetBypass. Не редактируйте его вручную."
                || knownHeaders.Contains(line)
                || knownEntries.Contains(normalizedEntry))
            {
                continue;
            }

            result.Add(rawLine);
        }

        return string.Join(Environment.NewLine, result);
    }

    private static string NormalizeLineEndings(string content) =>
        content.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();

    private void EnsureBackup()
    {
        if (File.Exists(_backupPath))
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(_backupPath)!);
        File.Copy(_hostsPath, _backupPath, overwrite: false);
    }

    private void WriteSafely(string content)
    {
        var directory = Path.GetDirectoryName(_hostsPath)!;
        var temporaryPath = Path.Combine(directory, $".netbypass-{Guid.NewGuid():N}.tmp");
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        try
        {
            File.WriteAllText(temporaryPath, content, encoding);

            try
            {
                // ReplaceFile сохраняет атомарность, когда Windows разрешает замену
                // защищённого системного файла.
                File.Replace(
                    temporaryPath,
                    _hostsPath,
                    destinationBackupFileName: null,
                    ignoreMetadataErrors: true);
            }
            catch (Exception exception) when (
                exception is UnauthorizedAccessException or IOException)
            {
                // Некоторые конфигурации Windows и антивирусы разрешают запись
                // в hosts, но запрещают удаление/замену самого файла.
                WriteExistingFile(content, encoding);
            }

            var written = File.ReadAllText(_hostsPath);
            if (!string.Equals(
                    NormalizeLineEndings(written),
                    NormalizeLineEndings(content),
                    StringComparison.Ordinal))
            {
                throw new IOException("Windows не сохранила изменения файла hosts.");
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private void WriteExistingFile(string content, Encoding encoding)
    {
        using var stream = new FileStream(
            _hostsPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read);
        using var writer = new StreamWriter(stream, encoding);
        writer.Write(content);
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    private enum ManagedBlockKind
    {
        None,
        Valid,
        Corrupted
    }

    private sealed record ManagedBlockResult(ManagedBlockKind Kind, string? Content);
}

using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Windows;

namespace NetBypass.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (!IsAdministrator())
        {
            RestartAsAdministrator(e.Args);
            Shutdown();
            return;
        }

        new MainWindow().Show();
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static void RestartAsAdministrator(IEnumerable<string> arguments)
    {
        var executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Не удалось определить путь к NetBypass.");

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = string.Join(" ", arguments.Select(QuoteArgument)),
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory
            });
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            MessageBox.Show(
                "Для изменения системного hosts нужны права администратора.",
                "NetBypass",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    private static string QuoteArgument(string argument) =>
        $"\"{argument.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
}

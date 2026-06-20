using System.Windows;
using NetBypass.App.ViewModels;

namespace NetBypass.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        try
        {
            DataContext = new MainViewModel();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"Не удалось запустить NetBypass:\n{exception.Message}",
                "Ошибка запуска",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Close();
        }
    }

    private void RestoreHosts_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
            return;

        var result = MessageBox.Show(
            "Удалить все записи, добавленные NetBypass?\n\nОстальные пользовательские записи hosts останутся без изменений.",
            "Восстановление hosts",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (result == MessageBoxResult.Yes)
            viewModel.RestoreConfirmed();
    }
}

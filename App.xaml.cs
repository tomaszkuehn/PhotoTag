using System.Windows;
using System.Windows.Threading;

namespace PhotoTag;

public partial class App : Application
{
    public App()
    {
        // Nieobsłużony wyjątek (np. w handlerze async void) zamykałby aplikację bez słowa.
        DispatcherUnhandledException += OnUnhandledException;
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(Current?.MainWindow!, "Wystąpił nieoczekiwany błąd:\n\n" + e.Exception.Message +
                        "\n\nAplikacja będzie działać dalej, ale warto sprawdzić ostatnią operację.",
            "PhotoTag — błąd", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}

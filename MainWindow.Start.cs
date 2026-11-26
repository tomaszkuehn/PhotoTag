using System.Diagnostics;
using System.IO;
using System.Windows;
using PhotoTag.Services;

namespace PhotoTag;

/// <summary>Kreator „Jak zacząć” i poradnik PDF.</summary>
public partial class MainWindow
{
    public const string GuideFileName = "PhotoTag-poradnik.pdf";

    private StartWindow? _startWindow;

    private void StartGuideMenu_Click(object sender, RoutedEventArgs e) => ShowStartGuide();

    private void GuidePdfMenu_Click(object sender, RoutedEventArgs e) => OpenGuidePdf();

    private void ShowStartGuide()
    {
        if (_startWindow is not null)
        {
            _startWindow.Activate();
            return;
        }
        _startWindow = new StartWindow(
            new StartWindow.Actions(
                PickFolder: () => OpenFolderAsync(add: false),
                LoadTrack: LoadTrackAsync,
                AddReference: AddRefFolderAsync,
                OpenGuide: OpenGuidePdf),
            _settings.ShowStartGuide)
        { Owner = this };
        _startWindow.Closed += (_, _) =>
        {
            _settings.ShowStartGuide = _startWindow!.ShowAtStart;
            SettingsService.Save(_settings);
            _startWindow = null;
        };
        _startWindow.Show();
    }

    private void OpenGuidePdf()
    {
        var path = Path.Combine(AppContext.BaseDirectory, GuideFileName);
        if (!File.Exists(path))
        {
            ShowInfo($"Nie znaleziono poradnika ({GuideFileName}) obok programu.");
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowInfo("Nie udało się otworzyć poradnika: " + ex.Message);
        }
    }
}

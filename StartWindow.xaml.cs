using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PhotoTag;

/// <summary>
/// Kreator „Jak zacząć”: kroki wykonują akcje głównego okna (okno niemodalne — widać, jak lista i mapa się zapełniają).
/// </summary>
public partial class StartWindow : Window
{
    public sealed record Actions(
        Func<Task<string?>> PickFolder,
        Func<Task<string?>> LoadTrack,
        Func<Task<string?>> AddReference,
        Action OpenGuide);

    private readonly Actions _actions;

    public StartWindow(Actions actions, bool showAtStart)
    {
        InitializeComponent();
        _actions = actions;
        ShowAtStartCheck.IsChecked = showAtStart;
    }

    public bool ShowAtStart => ShowAtStartCheck.IsChecked == true;

    private async Task RunStep(Button button, Func<Task<string?>> action, TextBlock result, TextBlock? number)
    {
        IsEnabled = false;
        try
        {
            if (await action() is not { } text) return;
            result.Text = "✓ " + text;
            result.Visibility = Visibility.Visible;
            if (number is not null)
            {
                number.Text = "✓";
                ((Border)number.Parent).Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x7F, 0x3B));
            }
            button.FontWeight = FontWeights.Normal;
        }
        finally
        {
            IsEnabled = true;
            Activate();
        }
    }

    private async void Step1Btn_Click(object sender, RoutedEventArgs e) =>
        await RunStep(Step1Btn, _actions.PickFolder, Step1Result, Step1No);

    private async void TrackBtn_Click(object sender, RoutedEventArgs e) =>
        await RunStep(TrackBtn, _actions.LoadTrack, Step2Result, Step2No);

    private async void RefBtn_Click(object sender, RoutedEventArgs e) =>
        await RunStep(RefBtn, _actions.AddReference, Step2Result, Step2No);

    private void GuideBtn_Click(object sender, RoutedEventArgs e) => _actions.OpenGuide();

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();
}

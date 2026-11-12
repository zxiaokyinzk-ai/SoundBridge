using System.Windows;

namespace AudioRouter;

public partial class LauncherWindow : Window
{
    public LauncherWindow() => InitializeComponent();
    private void OpenMixer_Click(object sender, RoutedEventArgs e) => OpenMixer(false);
    private void CheckInput_Click(object sender, RoutedEventArgs e) => OpenMixer(true);

    private void OpenMixer(bool inspect)
    {
        var mixer = new MainWindow();
        Application.Current.MainWindow = mixer;
        mixer.GameMicPanel.IsExpanded = inspect;
        mixer.Show();
        Close();
    }
}

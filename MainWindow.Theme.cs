using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace AudioRouter;

internal static class SoundBridgeTheme
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "声桥", "theme.txt");

    internal static string Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var value = File.ReadAllText(SettingsPath).Trim();
                if (value is "水墨" or "黑夜" or "青瓷") return value;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return "水墨";
    }

    internal static void Apply(string name)
    {
        var palettes = Application.Current.Resources.MergedDictionaries;
        string assembly = typeof(SoundBridgeTheme).Assembly.GetName().Name!;
        var palette = new ResourceDictionary { Source = new Uri($"/{assembly};component/Themes/{name}.xaml", UriKind.Relative) };
        var previous = palettes.FirstOrDefault(d => d.Source?.OriginalString.Contains("Themes/", StringComparison.OrdinalIgnoreCase) == true);
        if (previous == null) palettes.Add(palette);
        else palettes[palettes.IndexOf(previous)] = palette;
    }

    internal static void Save(string name)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        string temp = SettingsPath + "." + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temp, name);
            File.Move(temp, SettingsPath, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

public partial class MainWindow
{
    private bool _themeReady;

    private void LoadTheme(object sender, RoutedEventArgs e)
    {
        if (_themeReady) return;
        string theme = SoundBridgeTheme.Load();
        SoundBridgeTheme.Apply(theme);
        ThemePicker.SelectedItem = ThemePicker.Items.Cast<ComboBoxItem>().First(i => Equals(i.Content, theme));
        _themeReady = true;
    }

    private void ThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_themeReady || ThemePicker.SelectedItem is not ComboBoxItem { Content: string name }) return;
        SoundBridgeTheme.Apply(name);
        try { SoundBridgeTheme.Save(name); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _vm.StatusText = "主题已切换，本次未能保存启动偏好 · " + ex.Message;
        }
    }
}

using System.Windows;

namespace PdvLocal.App;

public partial class SettingsWindow : Window
{
    private bool _loaded;

    public SettingsWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _loaded = false;
        LightThemeRadio.IsChecked = ThemeManager.Current == ThemeManager.Light;
        DarkThemeRadio.IsChecked  = ThemeManager.Current == ThemeManager.Dark;
        _loaded = true;
    }

    private void ThemeRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;

        var theme = DarkThemeRadio.IsChecked == true ? ThemeManager.Dark : ThemeManager.Light;
        ThemeManager.Apply(theme);

        var prefs = UserPreferences.Load();
        prefs.Theme = theme;
        prefs.Save();
    }

    private void TechnicalDetailsButton_Click(object sender, RoutedEventArgs e)
    {
        new TechnicalDetailsWindow { Owner = this }.ShowDialog();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}

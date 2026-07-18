using System.Windows;

namespace PdvLocal.App;

public static class ThemeManager
{
    public const string Light = "Light";
    public const string Dark = "Dark";

    public static string Current { get; private set; } = Light;

    public static void Apply(string themeName)
    {
        Current = themeName == Dark ? Dark : Light;
        var uri = new Uri($"Themes/{Current}.xaml", UriKind.Relative);
        var newDict = new ResourceDictionary { Source = uri };

        var mergedDicts = Application.Current.Resources.MergedDictionaries;
        var existing = mergedDicts.FirstOrDefault(d =>
            d.Source?.OriginalString.StartsWith("Themes/", StringComparison.OrdinalIgnoreCase) == true);

        if (existing is not null)
            mergedDicts.Remove(existing);

        mergedDicts.Insert(0, newDict);
    }
}

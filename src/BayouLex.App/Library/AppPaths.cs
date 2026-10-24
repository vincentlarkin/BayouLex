using System.Text.Json;

namespace BayouLex.App.Library;

public static class AppPaths
{
    public static string DataDirectory { get; } =
        Environment.GetEnvironmentVariable("BAYOULEX_DATA_DIR") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BayouLex");

    public static string DefaultLibraryPath => Path.Combine(DataDirectory, "bayoulex-content.sqlite");

    public static string UserDatabasePath => Path.Combine(DataDirectory, "user.sqlite");

    public static string SettingsPath => Path.Combine(DataDirectory, "settings.json");
}

public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string? LibraryPath { get; set; }
    public double ReaderFontSize { get; set; } = 17;

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsPath))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsPath)) ?? new AppSettings();
            }
        }
        catch (Exception)
        {
            // A corrupt settings file should never stop the app from opening.
        }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(AppPaths.DataDirectory);
        File.WriteAllText(AppPaths.SettingsPath, JsonSerializer.Serialize(this, JsonOptions));
    }

    /// <summary>BAYOULEX_LIBRARY wins, then the last library opened, then the default location.</summary>
    public string ResolveLibraryPath()
    {
        if (Environment.GetEnvironmentVariable("BAYOULEX_LIBRARY") is { Length: > 0 } fromEnv)
        {
            return fromEnv;
        }
        if (!string.IsNullOrWhiteSpace(LibraryPath) && File.Exists(LibraryPath))
        {
            return LibraryPath;
        }
        return AppPaths.DefaultLibraryPath;
    }
}

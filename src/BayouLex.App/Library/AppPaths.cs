using System.Text.Json;

namespace BayouLex.App.Library;

public static class AppPaths
{
    public static string DataDirectory { get; } =
        Environment.GetEnvironmentVariable("BAYOULEX_DATA_DIR") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BayouLex");

    /// <summary>The library that installed content packs are merged into.</summary>
    public static string ManagedLibraryPath => Path.Combine(DataDirectory, "library.sqlite");

    public static string UserDatabasePath => Path.Combine(DataDirectory, "user.sqlite");

    public static string SettingsPath => Path.Combine(DataDirectory, "settings.json");
}

public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string? LibraryPath { get; set; }
    public double ReaderFontSize { get; set; } = 17;
    public bool WizardShown { get; set; }

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

    /// <summary>
    /// A specific library file to open instead of the managed pack library: BAYOULEX_LIBRARY, or one
    /// chosen with File › Open Library File. Null means use the managed library.
    /// </summary>
    public string? ExplicitLibraryPath()
    {
        if (Environment.GetEnvironmentVariable("BAYOULEX_LIBRARY") is { Length: > 0 } fromEnv)
        {
            return fromEnv;
        }
        return !string.IsNullOrWhiteSpace(LibraryPath) && File.Exists(LibraryPath) ? LibraryPath : null;
    }
}

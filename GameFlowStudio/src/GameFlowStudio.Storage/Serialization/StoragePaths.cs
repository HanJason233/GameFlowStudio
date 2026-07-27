namespace GameFlowStudio.Storage.Serialization;

public static class StoragePaths
{
    public static string ApplicationDataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameFlowStudio");
    public static string SettingsFile => Path.Combine(ApplicationDataDirectory, "settings.json");
    public static string LogsDirectory => Path.Combine(ApplicationDataDirectory, "logs");
    public static string ImageTemplatesDirectory => Path.Combine(ApplicationDataDirectory, "templates");
}

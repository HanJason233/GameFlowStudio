using System.Text.Json;
using GameFlowStudio.Contracts.Services;
using GameFlowStudio.Contracts.Workflows;
using GameFlowStudio.Storage.Serialization;

namespace GameFlowStudio.Storage.Settings;

public sealed class SettingsStorageService : ISettingsStorageService
{
    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(StoragePaths.SettingsFile)) return new();
        try
        {
            await using var stream = File.OpenRead(StoragePaths.SettingsFile);
            return await JsonSerializer.DeserializeAsync<AppSettings>(stream, ProjectJson.Options, cancellationToken).ConfigureAwait(false) ?? new();
        }
        catch (JsonException) { return new(); }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(StoragePaths.ApplicationDataDirectory);
        var temp = StoragePaths.SettingsFile + ".tmp";
        await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            await JsonSerializer.SerializeAsync(stream, settings, ProjectJson.Options, cancellationToken).ConfigureAwait(false);
        File.Move(temp, StoragePaths.SettingsFile, true);
    }
}

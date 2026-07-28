using System.Collections.Concurrent;
using System.Text.Json;
using GameFlowStudio.Contracts.Services;
using GameFlowStudio.Contracts.Workflows;
using GameFlowStudio.Storage.Serialization;

namespace GameFlowStudio.Storage.Settings;

public sealed class SettingsStorageService : ISettingsStorageService
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> SaveGates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private readonly string _settingsFile;
    private readonly string _settingsDirectory;
    private readonly SemaphoreSlim _saveGate;

    public SettingsStorageService()
        : this(StoragePaths.SettingsFile)
    {
    }

    public SettingsStorageService(string settingsFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsFile);
        _settingsFile = Path.GetFullPath(settingsFile);
        _settingsDirectory = Path.GetDirectoryName(_settingsFile)
            ?? throw new ArgumentException("无法确定设置文件目录。", nameof(settingsFile));
        _saveGate = SaveGates.GetOrAdd(_settingsFile, static _ => new SemaphoreSlim(1, 1));
    }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_settingsFile)) return new();
        try
        {
            await using var stream = new FileStream(
                _settingsFile,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                81920,
                FileOptions.Asynchronous);
            var settings = await JsonSerializer.DeserializeAsync<AppSettings>(
                stream,
                ProjectJson.Options,
                cancellationToken).ConfigureAwait(false);
            return Normalize(settings);
        }
        catch (JsonException) { return new(); }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var snapshot = JsonSerializer.SerializeToUtf8Bytes(settings, ProjectJson.Options);

        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? tempPath = null;
        try
        {
            Directory.CreateDirectory(_settingsDirectory);
            tempPath = Path.Combine(_settingsDirectory, $".{Path.GetFileName(_settingsFile)}.{Guid.NewGuid():N}.tmp");
            await using (var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(snapshot, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(tempPath, _settingsFile, true);
            tempPath = null;
        }
        finally
        {
            TryDeleteTemporaryFile(tempPath);
            _saveGate.Release();
        }
    }

    private static void TryDeleteTemporaryFile(string? path)
    {
        if (path is null) return;
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static AppSettings Normalize(AppSettings? settings)
    {
        if (settings is null) return new();

        var defaults = new AppSettings();
        settings.StepCategoryLayout ??= defaults.StepCategoryLayout;
        settings.DefaultProjectDirectory ??= defaults.DefaultProjectDirectory;
        settings.GeminiApiKey ??= defaults.GeminiApiKey;
        settings.RunHotkey ??= defaults.RunHotkey;
        settings.PauseHotkey ??= defaults.PauseHotkey;
        settings.StopHotkey ??= defaults.StopHotkey;
        settings.Theme ??= defaults.Theme;
        return settings;
    }
}

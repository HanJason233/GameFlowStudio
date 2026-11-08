using GameFlowStudio.Contracts.Services;
using GameFlowStudio.Storage.Settings;

namespace GameFlowStudio.Storage.Tests;

public sealed class SettingsStorageTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"GameFlowStudio.Settings.Tests.{Guid.NewGuid():N}");

    [Fact]
    public async Task ConcurrentSavesProduceOneCompleteSnapshotWithoutTemporaryFiles()
    {
        var path = Path.Combine(_directory, "settings.json");
        var storage = new SettingsStorageService(path);
        var saves = Enumerable.Range(0, 20)
            .Select(index => storage.SaveAsync(CreateSettings(index), CancellationToken.None))
            .ToArray();

        await Task.WhenAll(saves);

        var loaded = await storage.LoadAsync(CancellationToken.None);
        var index = int.Parse(loaded.DefaultProjectDirectory);
        Assert.Equal($"layout-{index}", loaded.StepCategoryLayout);
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task ConcurrentSavesAcrossServiceInstancesRemainCoherent()
    {
        var path = Path.Combine(_directory, "shared-settings.json");
        var saves = Enumerable.Range(0, 20)
            .Select(index => new SettingsStorageService(path)
                .SaveAsync(CreateSettings(index), CancellationToken.None))
            .ToArray();

        await Task.WhenAll(saves);

        var loaded = await new SettingsStorageService(path).LoadAsync(CancellationToken.None);
        var index = int.Parse(loaded.DefaultProjectDirectory);
        Assert.Equal($"layout-{index}", loaded.StepCategoryLayout);
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task LoadRepairsNullReferenceSettings()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "null-settings.json");
        await File.WriteAllTextAsync(
            path,
            """
            {
              "stepCategoryLayout": null,
              "defaultProjectDirectory": null,
              "geminiApiKey": null
            }
            """);

        var loaded = await new SettingsStorageService(path).LoadAsync(CancellationToken.None);
        var defaults = new AppSettings();

        Assert.Equal(defaults.StepCategoryLayout, loaded.StepCategoryLayout);
        Assert.Equal(defaults.DefaultProjectDirectory, loaded.DefaultProjectDirectory);
        Assert.Equal(defaults.GeminiApiKey, loaded.GeminiApiKey);
    }

    [Fact]
    public async Task SaveUsesSnapshotCapturedWhenCalled()
    {
        var path = Path.Combine(_directory, "settings.json");
        var storage = new SettingsStorageService(path);
        var settings = CreateSettings(7);

        var save = storage.SaveAsync(settings, CancellationToken.None);
        settings.StepCategoryLayout = "mutated";
        settings.DefaultProjectDirectory = "mutated";
        await save;

        var loaded = await storage.LoadAsync(CancellationToken.None);
        Assert.Equal("layout-7", loaded.StepCategoryLayout);
        Assert.Equal("7", loaded.DefaultProjectDirectory);
    }

    [Fact]
    public async Task FailedSaveRemovesTemporaryFile()
    {
        var settingsPath = Path.Combine(_directory, "settings.json");
        Directory.CreateDirectory(settingsPath);
        var storage = new SettingsStorageService(settingsPath);

        var exception = await Record.ExceptionAsync(() => storage.SaveAsync(CreateSettings(1), CancellationToken.None));

        Assert.True(
            exception is IOException or UnauthorizedAccessException,
            $"预期保存目标冲突，实际异常：{exception?.GetType().Name ?? "无异常"}");
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.tmp"));
    }

    private static AppSettings CreateSettings(int index) => new()
    {
        StepCategoryLayout = $"layout-{index}",
        DefaultProjectDirectory = index.ToString()
    };

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}

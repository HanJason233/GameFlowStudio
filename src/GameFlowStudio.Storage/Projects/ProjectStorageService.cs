using System.Collections.Concurrent;
using System.Text.Json;
using GameFlowStudio.Contracts.Services;
using GameFlowStudio.Contracts.Workflows;
using GameFlowStudio.Storage.Serialization;
using Microsoft.Extensions.Logging;

namespace GameFlowStudio.Storage.Projects;

/// <summary>使用临时文件校验和原子替换保存 .gflow 项目。</summary>
public sealed class ProjectStorageService(ILogger<ProjectStorageService> logger) : IProjectStorageService
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> SaveGates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public async Task SaveAsync(AutomationProject project, string filePath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var fullPath = Path.GetFullPath(filePath);
        var directory = Path.GetDirectoryName(fullPath) ?? throw new InvalidOperationException("无法确定项目目录。");
        var saveGate = SaveGates.GetOrAdd(fullPath, static _ => new SemaphoreSlim(1, 1));
        await saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        string? tempPath = null;
        try
        {
            var snapshot = ProjectDocumentSerializer.CreateSnapshot(project, DateTimeOffset.UtcNow);
            Directory.CreateDirectory(directory);
            tempPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
                await ProjectDocumentSerializer.WriteAsync(stream, snapshot, cancellationToken).ConfigureAwait(false);

            await using (var verification = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous))
                _ = await ProjectDocumentSerializer.ReadAsync(verification, cancellationToken).ConfigureAwait(false);

            if (File.Exists(fullPath)) File.Replace(tempPath, fullPath, null, true);
            else File.Move(tempPath, fullPath);
            tempPath = null;
        }
        catch
        {
            TryDeleteTemporaryFile(tempPath);
            throw;
        }
        finally
        {
            saveGate.Release();
        }

        logger.LogInformation("项目已保存到 {Path}", fullPath);
    }

    public async Task<AutomationProject> LoadAsync(string filePath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        try
        {
            var fullPath = Path.GetFullPath(filePath);
            await using var stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                81920,
                FileOptions.Asynchronous);
            return await ProjectDocumentSerializer.ReadAsync(stream, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException ex) { throw new InvalidDataException($"项目 JSON 无效：{ex.Message}", ex); }
    }

    private static void TryDeleteTemporaryFile(string? path)
    {
        if (path is null) return;
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

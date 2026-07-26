using System.Text.Json;
using GameFlowStudio.Contracts.Services;
using GameFlowStudio.Contracts.Workflows;
using Microsoft.Extensions.Logging;

namespace GameFlowStudio.Storage.Projects;

/// <summary>使用临时文件校验和原子替换保存 .gflow 项目。</summary>
public sealed class ProjectStorageService(ILogger<ProjectStorageService> logger) : IProjectStorageService
{
    public async Task SaveAsync(AutomationProject project, string filePath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var fullPath = Path.GetFullPath(filePath);
        var directory = Path.GetDirectoryName(fullPath) ?? throw new InvalidOperationException("无法确定项目目录。");
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        project.Project.UpdatedAt = DateTimeOffset.UtcNow;
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
                await JsonSerializer.SerializeAsync(stream, project, ProjectJson.Options, cancellationToken).ConfigureAwait(false);

            await using (var verification = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous))
                _ = await JsonSerializer.DeserializeAsync<AutomationProject>(verification, ProjectJson.Options, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException("临时项目文件校验失败。");

            if (File.Exists(fullPath)) File.Replace(tempPath, fullPath, null, true);
            else File.Move(tempPath, fullPath);
            logger.LogInformation("项目已保存到 {Path}", fullPath);
        }
        catch
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
            throw;
        }
    }

    public async Task<AutomationProject> LoadAsync(string filePath, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
            var project = await JsonSerializer.DeserializeAsync<AutomationProject>(stream, ProjectJson.Options, cancellationToken).ConfigureAwait(false);
            return project ?? throw new InvalidDataException("项目文件内容为空。");
        }
        catch (JsonException ex) { throw new InvalidDataException($"项目 JSON 无效：{ex.Message}", ex); }
    }
}

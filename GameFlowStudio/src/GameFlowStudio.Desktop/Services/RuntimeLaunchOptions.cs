using System.IO;
using GameFlowStudio.Contracts.Services;
using GameFlowStudio.Contracts.Workflows;

namespace GameFlowStudio.Desktop.Services;

/// <summary>纯运行进程的命令行参数。</summary>
public sealed record RuntimeLaunchOptions(string ProjectPath, string? ScriptId)
{
    public const string RuntimeSwitch = "--runtime";
    public const string ScriptSwitch = "--script";

    public static bool TryParse(IReadOnlyList<string> arguments, out RuntimeLaunchOptions? options)
    {
        options = null;
        var runtimeIndex = IndexOf(arguments, RuntimeSwitch);
        if (runtimeIndex < 0 || runtimeIndex + 1 >= arguments.Count || string.IsNullOrWhiteSpace(arguments[runtimeIndex + 1]))
            return false;

        var projectPath = Path.GetFullPath(arguments[runtimeIndex + 1]);
        string? scriptId = null;
        var scriptIndex = IndexOf(arguments, ScriptSwitch);
        if (scriptIndex >= 0 && scriptIndex + 1 < arguments.Count && !string.IsNullOrWhiteSpace(arguments[scriptIndex + 1]))
            scriptId = arguments[scriptIndex + 1];

        options = new(projectPath, scriptId);
        return true;
    }

    private static int IndexOf(IReadOnlyList<string> arguments, string value)
    {
        for (var index = 0; index < arguments.Count; index++)
            if (string.Equals(arguments[index], value, StringComparison.OrdinalIgnoreCase))
                return index;
        return -1;
    }
}

/// <summary>从纯运行窗口切回编辑器时使用的命令行参数。</summary>
public sealed record EditorLaunchOptions(string ProjectPath)
{
    public const string EditorSwitch = "--editor";

    public static bool TryParse(IReadOnlyList<string> arguments, out EditorLaunchOptions? options)
    {
        options = null;
        for (var index = 0; index < arguments.Count - 1; index++)
        {
            if (!string.Equals(arguments[index], EditorSwitch, StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrWhiteSpace(arguments[index + 1])) return false;
            options = new(Path.GetFullPath(arguments[index + 1]));
            return true;
        }

        return false;
    }
}

public interface IRuntimeProcessLauncher
{
    Task LaunchAsync(AutomationProject project, CancellationToken cancellationToken);
}

/// <summary>把最新编辑内容交给独立的纯运行进程。</summary>
public sealed class RuntimeProcessLauncher(IProjectStorageService storage)
    : IRuntimeProcessLauncher
{
    public async Task LaunchAsync(AutomationProject project, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        var directory = Path.Combine(Path.GetTempPath(), "GameFlowStudio", "runtime");
        Directory.CreateDirectory(directory);
        var projectPath = Path.Combine(directory, $"project-{Guid.NewGuid():N}.gflow");

        await storage.SaveAsync(project, projectPath, cancellationToken).ConfigureAwait(false);
        try
        {
            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable))
                throw new InvalidOperationException("无法确定当前程序路径，不能启动纯运行窗口。");

            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = true,
                WorkingDirectory = AppContext.BaseDirectory
            };
            startInfo.ArgumentList.Add(RuntimeLaunchOptions.RuntimeSwitch);
            startInfo.ArgumentList.Add(projectPath);
            if (!string.IsNullOrWhiteSpace(project.ActiveScriptId))
            {
                startInfo.ArgumentList.Add(RuntimeLaunchOptions.ScriptSwitch);
                startInfo.ArgumentList.Add(project.ActiveScriptId);
            }

            if (System.Diagnostics.Process.Start(startInfo) is null)
                throw new InvalidOperationException("纯运行窗口启动失败。");
        }
        catch
        {
            TryDelete(projectPath);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

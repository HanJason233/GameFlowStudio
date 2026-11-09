using System.IO;
using GameFlowStudio.Contracts.Services;
using GameFlowStudio.Contracts.Workflows;

namespace GameFlowStudio.Desktop.Services;

/// <summary>纯运行进程的命令行参数。</summary>
public sealed record RuntimeLaunchOptions(string ProjectPath, string? ScriptId, string? EditorProjectPath)
{
    public const string RuntimeSwitch = "--pure-run";
    public const string ScriptSwitch = "--script";
    public const string EditorProjectSwitch = "--editor-project";

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

        string? editorProjectPath = null;
        var editorIndex = IndexOf(arguments, EditorProjectSwitch);
        if (editorIndex >= 0 && editorIndex + 1 < arguments.Count && !string.IsNullOrWhiteSpace(arguments[editorIndex + 1]))
            editorProjectPath = Path.GetFullPath(arguments[editorIndex + 1]);

        options = new(projectPath, scriptId, editorProjectPath);
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
public sealed record EditorLaunchOptions(string ProjectPath, bool DeleteOnExit = false)
{
    public const string EditorSwitch = "--editor";
    public const string DeleteOnExitSwitch = "--delete-on-exit";

    public static bool TryParse(IReadOnlyList<string> arguments, out EditorLaunchOptions? options)
    {
        options = null;
        for (var index = 0; index < arguments.Count - 1; index++)
        {
            if (!string.Equals(arguments[index], EditorSwitch, StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrWhiteSpace(arguments[index + 1])) return false;
            var deleteOnExit = arguments.Any(argument => string.Equals(argument, DeleteOnExitSwitch, StringComparison.OrdinalIgnoreCase));
            options = new(Path.GetFullPath(arguments[index + 1]), deleteOnExit);
            return true;
        }

        if (arguments.Count == 1 && Path.GetExtension(arguments[0]).Equals(".gflow", StringComparison.OrdinalIgnoreCase)
            && File.Exists(arguments[0]))
        {
            options = new(Path.GetFullPath(arguments[0]));
            return true;
        }

        return false;
    }
}

public interface IPureRunWindowLauncher
{
    Task LaunchAsync(AutomationProject project, string? editorProjectPath, CancellationToken cancellationToken);
}

/// <summary>把最新编辑内容交给独立的纯运行进程。</summary>
public sealed class PureRunWindowLauncher(IProjectStorageService storage)
    : IPureRunWindowLauncher
{
    public async Task LaunchAsync(AutomationProject project, string? editorProjectPath, CancellationToken cancellationToken)
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
            if (!string.IsNullOrWhiteSpace(editorProjectPath))
            {
                startInfo.ArgumentList.Add(RuntimeLaunchOptions.EditorProjectSwitch);
                startInfo.ArgumentList.Add(editorProjectPath);
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

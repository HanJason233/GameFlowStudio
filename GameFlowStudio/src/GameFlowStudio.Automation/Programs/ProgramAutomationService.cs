using System.Diagnostics;
using GameFlowStudio.Contracts.Services;

namespace GameFlowStudio.Automation.Programs;

public sealed class ProgramAutomationService : IProgramAutomationService
{
    public async Task StartAsync(string filePath, string arguments, string workingDirectory, bool waitForExit, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("程序路径不能为空。", nameof(filePath));
        var startInfo = new ProcessStartInfo
        {
            FileName = filePath,
            Arguments = arguments ?? string.Empty,
            WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? Environment.CurrentDirectory : workingDirectory,
            UseShellExecute = true
        };
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动程序。");
        if (waitForExit) await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
    }
}

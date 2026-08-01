using System.Diagnostics;
using GameFlowStudio.Contracts.Services;

namespace GameFlowStudio.Automation.Programs;

public sealed class ProgramAutomationService : IProgramAutomationService
{
    public Task StartAsync(string filePath, string arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("程序路径不能为空。", nameof(filePath));
        var startInfo = new ProcessStartInfo
        {
            FileName = filePath,
            Arguments = arguments ?? string.Empty,
            UseShellExecute = true
        };
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动程序。");
        return Task.CompletedTask;
    }
}

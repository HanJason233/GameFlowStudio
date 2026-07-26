using System.Text.RegularExpressions;
using GameFlowStudio.Contracts.Services;

namespace GameFlowStudio.Automation.Windows;

/// <summary>使用标题正则表达式解析可见顶级窗口的句柄集合。</summary>
public sealed class WindowScopeService(IWindowAutomationService windows) : IWindowScopeService
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    public IReadOnlyList<nint> ResolveWindowHandles(string titlePattern)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(titlePattern);

        Regex titleRegex;
        try
        {
            titleRegex = new Regex(
                titlePattern,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                MatchTimeout);
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException("窗口标题正则表达式无效。", nameof(titlePattern), exception);
        }

        try
        {
            return windows.GetOpenWindows()
                .Where(window => titleRegex.IsMatch(window.Title))
                .Select(window => window.Handle)
                .Distinct()
                .ToArray();
        }
        catch (RegexMatchTimeoutException exception)
        {
            throw new InvalidOperationException("窗口标题正则匹配超时。", exception);
        }
    }
}

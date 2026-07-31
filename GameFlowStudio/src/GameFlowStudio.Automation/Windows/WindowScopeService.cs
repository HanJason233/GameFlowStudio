using System.Text.RegularExpressions;
using GameFlowStudio.Contracts.Services;

namespace GameFlowStudio.Automation.Windows;

/// <summary>使用包含与排除标题正则表达式解析可见顶级窗口的句柄集合。</summary>
public sealed class WindowScopeService(IWindowAutomationService windows) : IWindowScopeService
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    public IReadOnlyList<nint> ResolveWindowHandles(string matchPattern, string excludePattern = "")
    {
        if (string.IsNullOrWhiteSpace(matchPattern))
            throw new ArgumentException("窗口匹配正则表达式不能为空。", nameof(matchPattern));

        Regex matchRegex;
        try
        {
            matchRegex = new Regex(
                matchPattern,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                MatchTimeout);
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException("窗口匹配正则表达式无效。", nameof(matchPattern), exception);
        }

        Regex? excludeRegex = null;
        if (!string.IsNullOrWhiteSpace(excludePattern))
        {
            try
            {
                excludeRegex = new Regex(
                    excludePattern,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                    MatchTimeout);
            }
            catch (ArgumentException exception)
            {
                throw new ArgumentException("窗口排除正则表达式无效。", nameof(excludePattern), exception);
            }
        }

        var handles = new List<nint>();
        var seenHandles = new HashSet<nint>();
        foreach (var window in windows.GetOpenWindows())
        {
            bool included;
            try
            {
                included = matchRegex.IsMatch(window.Title);
            }
            catch (RegexMatchTimeoutException exception)
            {
                throw new InvalidOperationException("窗口匹配正则表达式执行超时。", exception);
            }

            if (!included) continue;

            bool excluded;
            try
            {
                excluded = excludeRegex?.IsMatch(window.Title) == true;
            }
            catch (RegexMatchTimeoutException exception)
            {
                throw new InvalidOperationException("窗口排除正则表达式执行超时。", exception);
            }

            if (!excluded && seenHandles.Add(window.Handle))
                handles.Add(window.Handle);
        }

        return handles;
    }
}

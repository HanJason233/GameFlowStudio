using GameFlowStudio.Automation.Windows;
using GameFlowStudio.Contracts.Services;
using GameFlowStudio.Contracts.Results;

namespace GameFlowStudio.Workflow.Tests;

public sealed class WindowScopeServiceTests
{
    [Fact]
    public void ResolveWindowHandlesReturnsAllRegexMatchesIgnoringCase()
    {
        var windows = new FakeWindowAutomationService(
        [
            new((nint)101, "Game Client - Alpha"),
            new((nint)202, "game client - Beta"),
            new((nint)303, "Notepad"),
            new((nint)101, "Game Client - Alpha")
        ]);
        var scope = new WindowScopeService(windows);

        var handles = scope.ResolveWindowHandles("^game client - (alpha|beta)$");

        Assert.Equal(new nint[] { 101, 202 }, handles);
        Assert.Equal(1, windows.GetOpenWindowsCallCount);
    }

    [Fact]
    public void ResolveWindowHandlesIncludesFirstThenExcludesMatchingTargets()
    {
        var windows = new FakeWindowAutomationService(
        [
            new((nint)101, "Game Client - Alpha"),
            new((nint)202, "Game Client - Beta 测试服"),
            new((nint)303, "Notepad 测试服"),
            new((nint)404, "Game Client - Release")
        ]);
        var scope = new WindowScopeService(windows);

        var handles = scope.ResolveWindowHandles("^Game Client", "测试服$");

        Assert.Equal(new nint[] { 101, 404 }, handles);
        Assert.Equal(1, windows.GetOpenWindowsCallCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveWindowHandlesDoesNotExcludeWhenExcludePatternIsBlank(string excludePattern)
    {
        var scope = new WindowScopeService(new FakeWindowAutomationService(
        [
            new((nint)101, "Game Alpha"),
            new((nint)202, "Game Beta")
        ]));

        var handles = scope.ResolveWindowHandles("^Game", excludePattern);

        Assert.Equal(new nint[] { 101, 202 }, handles);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("[")]
    public void ResolveWindowHandlesRejectsInvalidPattern(string pattern)
    {
        var scope = new WindowScopeService(new FakeWindowAutomationService([]));

        var error = Assert.Throws<ArgumentException>(() => scope.ResolveWindowHandles(pattern));

        Assert.Equal("matchPattern", error.ParamName);
        Assert.Contains("窗口匹配正则表达式", error.Message);
    }

    [Fact]
    public void ResolveWindowHandlesRejectsInvalidExcludePatternClearly()
    {
        var scope = new WindowScopeService(new FakeWindowAutomationService([]));

        var error = Assert.Throws<ArgumentException>(
            () => scope.ResolveWindowHandles(".*", "["));

        Assert.Equal("excludePattern", error.ParamName);
        Assert.Contains("窗口排除正则表达式无效", error.Message);
    }

    private sealed class FakeWindowAutomationService(IReadOnlyList<WindowInfo> windows) : IWindowAutomationService
    {
        public int GetOpenWindowsCallCount { get; private set; }
        public IReadOnlyList<WindowInfo> GetOpenWindows()
        {
            GetOpenWindowsCallCount++;
            return windows;
        }
        public bool ActivateWindow(nint handle) => true;
        public bool CloseWindow(nint handle) => true;
        public bool MinimizeWindow(nint handle) => true;
        public bool ArrangeWindows(IReadOnlyList<nint> handles, int x, int y, int offsetY) => true;
        public bool SetWindowResolution(nint handle, int width, int height) => true;
        public WindowBounds GetWindowBounds(nint handle) => throw new NotSupportedException();
    }

}

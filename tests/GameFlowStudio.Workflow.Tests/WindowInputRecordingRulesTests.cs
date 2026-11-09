using GameFlowStudio.Desktop.Interaction;
using GameFlowStudio.Contracts.Recording;

namespace GameFlowStudio.Workflow.Tests;

public sealed class WindowInputRecordingRulesTests
{
    [Fact]
    public void RecordingOptionsDefaultToScreenScope()
    {
        var options = new InputRecordingOptions();

        Assert.Equal(InputRecordingScope.Screen, options.Scope);
        Assert.Equal(nint.Zero, options.TargetWindowHandle);
    }

    [Fact]
    public void MouseEventCanBelongToSelectedTopLevelWindow()
    {
        var target = new nint(101);

        Assert.True(WindowInputRecordingRules.ShouldCaptureMouse(
            InputRecordingMessages.LeftButtonDown,
            hasActiveMouseButton: false,
            target,
            eventRoot: target,
            eventRootOwner: new nint(99)));
        Assert.False(WindowInputRecordingRules.ShouldCaptureMouse(
            InputRecordingMessages.LeftButtonDown,
            hasActiveMouseButton: false,
            target,
            eventRoot: new nint(202),
            eventRootOwner: new nint(202)));
    }

    [Fact]
    public void OwnedPopupBelongsToSelectedMainWindow()
    {
        Assert.True(WindowInputRecordingRules.IsTargetWindow(
            targetWindow: new nint(101),
            candidateRoot: new nint(202),
            candidateRootOwner: new nint(101)));
    }

    [Fact]
    public void SelectedOwnedDialogDoesNotExpandBackToItsOwnerWindow()
    {
        Assert.False(WindowInputRecordingRules.IsTargetWindow(
            targetWindow: new nint(202),
            candidateRoot: new nint(101),
            candidateRootOwner: new nint(101)));
    }

    [Theory]
    [InlineData(InputRecordingMessages.MouseMove)]
    [InlineData(InputRecordingMessages.LeftButtonUp)]
    [InlineData(InputRecordingMessages.RightButtonUp)]
    [InlineData(InputRecordingMessages.MiddleButtonUp)]
    public void DragStartedInTargetWindowRemainsOneCapturedOperation(uint message)
    {
        Assert.True(WindowInputRecordingRules.ShouldCaptureMouse(
            message,
            hasActiveMouseButton: true,
            targetWindow: new nint(101),
            eventRoot: new nint(202),
            eventRootOwner: new nint(202)));
    }

    [Fact]
    public void EachDragEndpointUsesWindowRectangleAtItsOwnEventTime()
    {
        var start = WindowInputRecordingRules.ToRelativePoint(
            screenX: 150,
            screenY: 260,
            windowLeft: 100,
            windowTop: 200);
        var end = WindowInputRecordingRules.ToRelativePoint(
            screenX: 190,
            screenY: 330,
            windowLeft: 120,
            windowTop: 240);

        Assert.Equal((50, 60), start);
        Assert.Equal((70, 90), end);
    }
}

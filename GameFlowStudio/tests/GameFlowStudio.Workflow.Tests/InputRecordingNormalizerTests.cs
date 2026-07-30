using GameFlowStudio.Automation.Input;
using GameFlowStudio.Contracts.Enums;
using GameFlowStudio.Contracts.Recording;

namespace GameFlowStudio.Workflow.Tests;

public sealed class InputRecordingNormalizerTests
{
    [Theory]
    [InlineData(120)]
    [InlineData(-120)]
    public void MouseWheelPreservesSignedDelta(int delta)
    {
        var normalizer = new InputRecordingNormalizer();
        var mouseData = unchecked((uint)(ushort)(short)delta) << 16;

        var action = normalizer.ProcessMouse(
            InputRecordingMessages.MouseWheel,
            x: -320,
            y: 480,
            mouseData,
            elapsedMilliseconds: 75,
            capture: true);

        var scroll = Assert.IsType<RecordedInputAction>(action);
        Assert.Equal(RecordedInputActionKind.MouseScroll, scroll.Kind);
        Assert.Equal(-320, scroll.X);
        Assert.Equal(480, scroll.Y);
        Assert.Equal(delta, scroll.Delta);
        Assert.Equal(75, scroll.OffsetMilliseconds);
    }

    [Fact]
    public void MovementBelowDragThresholdProducesClickAtReleasePosition()
    {
        var normalizer = new InputRecordingNormalizer();
        Assert.Null(normalizer.ProcessMouse(
            InputRecordingMessages.LeftButtonDown,
            x: 10,
            y: 20,
            mouseData: 0,
            elapsedMilliseconds: 100,
            capture: true));

        var action = normalizer.ProcessMouse(
            InputRecordingMessages.LeftButtonUp,
            x: 14,
            y: 20,
            mouseData: 0,
            elapsedMilliseconds: 135,
            capture: true);

        var click = Assert.IsType<RecordedInputAction>(action);
        Assert.Equal(RecordedInputActionKind.MouseClick, click.Kind);
        Assert.Equal(MouseButtonType.Left, click.Button);
        Assert.Equal(14, click.X);
        Assert.Equal(20, click.Y);
        Assert.Equal(100, click.OffsetMilliseconds);
        Assert.Equal(35, click.DurationMilliseconds);
    }

    [Fact]
    public void MovementAtDragThresholdProducesDragFromPressToRelease()
    {
        var normalizer = new InputRecordingNormalizer();
        Assert.Null(normalizer.ProcessMouse(
            InputRecordingMessages.RightButtonDown,
            x: 10,
            y: 20,
            mouseData: 0,
            elapsedMilliseconds: 200,
            capture: true));

        var action = normalizer.ProcessMouse(
            InputRecordingMessages.RightButtonUp,
            x: 13,
            y: 24,
            mouseData: 0,
            elapsedMilliseconds: 260,
            capture: true);

        var drag = Assert.IsType<RecordedInputAction>(action);
        Assert.Equal(RecordedInputActionKind.MouseDrag, drag.Kind);
        Assert.Equal(MouseButtonType.Right, drag.Button);
        Assert.Equal(10, drag.X);
        Assert.Equal(20, drag.Y);
        Assert.Equal(13, drag.EndX);
        Assert.Equal(24, drag.EndY);
        Assert.Equal(200, drag.OffsetMilliseconds);
        Assert.Equal(60, drag.DurationMilliseconds);
    }

    [Fact]
    public void ReturningNearThePressPointStillProducesDragAfterAThresholdMove()
    {
        var normalizer = new InputRecordingNormalizer();
        _ = normalizer.ProcessMouse(
            InputRecordingMessages.LeftButtonDown,
            x: 10,
            y: 20,
            mouseData: 0,
            elapsedMilliseconds: 100,
            capture: true);
        _ = normalizer.ProcessMouse(
            InputRecordingMessages.MouseMove,
            x: 50,
            y: 60,
            mouseData: 0,
            elapsedMilliseconds: 120,
            capture: true);

        var action = normalizer.ProcessMouse(
            InputRecordingMessages.LeftButtonUp,
            x: 11,
            y: 20,
            mouseData: 0,
            elapsedMilliseconds: 140,
            capture: true);

        Assert.Equal(RecordedInputActionKind.MouseDrag, Assert.IsType<RecordedInputAction>(action).Kind);
    }

    [Fact]
    public void ReleasingOverAnExcludedWindowDiscardsThePendingMouseAction()
    {
        var normalizer = new InputRecordingNormalizer();
        _ = normalizer.ProcessMouse(
            InputRecordingMessages.LeftButtonDown,
            x: 10,
            y: 20,
            mouseData: 0,
            elapsedMilliseconds: 100,
            capture: true);

        var action = normalizer.ProcessMouse(
            InputRecordingMessages.LeftButtonUp,
            x: 40,
            y: 50,
            mouseData: 0,
            elapsedMilliseconds: 150,
            capture: false);

        Assert.Null(action);
        Assert.False(normalizer.HasActiveMouseButton);
    }

    [Fact]
    public void FlushCompletesAMouseButtonStillHeldAtStop()
    {
        var normalizer = new InputRecordingNormalizer();
        _ = normalizer.ProcessMouse(
            InputRecordingMessages.MiddleButtonDown,
            x: 10,
            y: 20,
            mouseData: 0,
            elapsedMilliseconds: 100,
            capture: true);
        _ = normalizer.ProcessMouse(
            InputRecordingMessages.MouseMove,
            x: 30,
            y: 40,
            mouseData: 0,
            elapsedMilliseconds: 120,
            capture: true);

        var action = Assert.Single(normalizer.Flush(elapsedMilliseconds: 180));

        Assert.Equal(RecordedInputActionKind.MouseDrag, action.Kind);
        Assert.Equal(MouseButtonType.Middle, action.Button);
        Assert.Equal((10, 20, 30, 40), (action.X, action.Y, action.EndX, action.EndY));
        Assert.Equal(80, action.DurationMilliseconds);
    }

    [Fact]
    public void ModifierKeysAreCombinedWithSupportedKey()
    {
        var normalizer = new InputRecordingNormalizer();

        Assert.False(normalizer.ProcessKeyboard(
            InputRecordingMessages.KeyDown,
            virtualKey: 0x11,
            elapsedMilliseconds: 5,
            capture: true).StopRequested);
        Assert.False(normalizer.ProcessKeyboard(
            InputRecordingMessages.KeyDown,
            virtualKey: 0x10,
            elapsedMilliseconds: 10,
            capture: true).StopRequested);
        Assert.Null(normalizer.ProcessKeyboard(
            InputRecordingMessages.KeyDown,
            virtualKey: 0x41,
            elapsedMilliseconds: 20,
            capture: true).Action);

        var result = normalizer.ProcessKeyboard(
            InputRecordingMessages.KeyUp,
            virtualKey: 0x41,
            elapsedMilliseconds: 55,
            capture: true);

        Assert.False(result.StopRequested);
        var keyPress = Assert.IsType<RecordedInputAction>(result.Action);
        Assert.Equal(RecordedInputActionKind.KeyPress, keyPress.Kind);
        Assert.Equal("Ctrl+Shift+A", keyPress.Key);
        Assert.Equal(20, keyPress.OffsetMilliseconds);
        Assert.Equal(35, keyPress.DurationMilliseconds);
    }

    [Fact]
    public void F7IsSuppressedAndRequestsStopOnRelease()
    {
        var normalizer = new InputRecordingNormalizer();

        var firstDown = normalizer.ProcessKeyboard(
            InputRecordingMessages.KeyDown,
            InputRecordingNormalizer.StopVirtualKey,
            elapsedMilliseconds: 10,
            capture: true);
        var repeatedDown = normalizer.ProcessKeyboard(
            InputRecordingMessages.KeyDown,
            InputRecordingNormalizer.StopVirtualKey,
            elapsedMilliseconds: 20,
            capture: true);
        var keyUp = normalizer.ProcessKeyboard(
            InputRecordingMessages.KeyUp,
            InputRecordingNormalizer.StopVirtualKey,
            elapsedMilliseconds: 30,
            capture: true);
        var secondDown = normalizer.ProcessKeyboard(
            InputRecordingMessages.KeyDown,
            InputRecordingNormalizer.StopVirtualKey,
            elapsedMilliseconds: 40,
            capture: false);
        var secondUp = normalizer.ProcessKeyboard(
            InputRecordingMessages.KeyUp,
            InputRecordingNormalizer.StopVirtualKey,
            elapsedMilliseconds: 50,
            capture: false);

        Assert.False(firstDown.StopRequested);
        Assert.Null(firstDown.Action);
        Assert.True(firstDown.Suppress);
        Assert.False(repeatedDown.StopRequested);
        Assert.Null(repeatedDown.Action);
        Assert.True(repeatedDown.Suppress);
        Assert.True(keyUp.StopRequested);
        Assert.Null(keyUp.Action);
        Assert.True(keyUp.Suppress);
        Assert.False(secondDown.StopRequested);
        Assert.Null(secondDown.Action);
        Assert.True(secondDown.Suppress);
        Assert.True(secondUp.StopRequested);
        Assert.True(secondUp.Suppress);
        Assert.Empty(normalizer.Flush(elapsedMilliseconds: 60));
    }
}

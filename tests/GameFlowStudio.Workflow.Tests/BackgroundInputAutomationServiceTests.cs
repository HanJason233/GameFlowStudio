using GameFlowStudio.Automation.Input;
using GameFlowStudio.Contracts.Enums;

namespace GameFlowStudio.Workflow.Tests;

public sealed class BackgroundInputAutomationServiceTests
{
    [Theory]
    [InlineData(MouseButtonType.Left, 0x0201u, 0x0202u, 0x0203u, 0x0001)]
    [InlineData(MouseButtonType.Right, 0x0204u, 0x0205u, 0x0206u, 0x0002)]
    [InlineData(MouseButtonType.Middle, 0x0207u, 0x0208u, 0x0209u, 0x0010)]
    public void ButtonMessagesUseTheExpectedWin32Values(
        MouseButtonType button,
        uint expectedDown,
        uint expectedUp,
        uint expectedDoubleClick,
        int expectedKeyState)
    {
        var messages = BackgroundInputAutomationService.ButtonMessages(button);

        Assert.Equal(expectedDown, messages.Down);
        Assert.Equal(expectedUp, messages.Up);
        Assert.Equal(expectedDoubleClick, messages.DoubleClick);
        Assert.Equal((nint)expectedKeyState, messages.DownKeyState);
    }

    [Fact]
    public void ClientCoordinatesArePackedIntoMouseMessageParameter()
    {
        var packed = BackgroundInputAutomationService.PackClientPoint(0x1234, 0x2345);

        Assert.Equal((nint)0x23451234, packed);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    [InlineData(32768, 0)]
    [InlineData(0, 32768)]
    public void UnsupportedClientCoordinatesAreRejected(int x, int y)
    {
        Assert.Throws<InvalidOperationException>(
            () => BackgroundInputAutomationService.PackClientPoint(x, y));
    }

    [Fact]
    public void SignedCoordinatesPreserveNegativeMultiMonitorValues()
    {
        var packed = BackgroundInputAutomationService.PackSignedPoint(-1, -2);

        Assert.Equal(unchecked((nint)0xFFFEFFFFu), packed);
    }

    [Theory]
    [InlineData(-32769, 0)]
    [InlineData(32768, 0)]
    [InlineData(0, -32769)]
    [InlineData(0, 32768)]
    public void SignedCoordinatesOutsideMessageRangeAreRejected(int x, int y)
    {
        Assert.Throws<InvalidOperationException>(
            () => BackgroundInputAutomationService.PackSignedPoint(x, y));
    }

    [Theory]
    [InlineData(120, 0x00780000u)]
    [InlineData(-120, 0xFF880000u)]
    public void WheelDeltaUsesTheHighWord(int delta, uint expected)
    {
        Assert.Equal(
            unchecked((nint)expected),
            BackgroundInputAutomationService.PackWheelParameter(delta));
    }

    [Fact]
    public void KeyboardMessageParameterIncludesScanExtendedAltAndReleaseFlags()
    {
        var down = BackgroundInputAutomationService.PackKeyMessageParameter(
            0xE04D,
            keyUp: false,
            altDown: false);
        var altUp = BackgroundInputAutomationService.PackKeyMessageParameter(
            0xE04D,
            keyUp: true,
            altDown: true);

        Assert.Equal((nint)0x014D0001, down);
        Assert.Equal(unchecked((nint)0xE14D0001u), altUp);
    }

    [Theory]
    [InlineData(0x12, false, true)]
    [InlineData(0x79, false, true)]
    [InlineData(0x41, true, true)]
    [InlineData(0x41, false, false)]
    public void SystemKeyMessageSelectionHandlesAltAndF10(
        ushort virtualKey,
        bool altDown,
        bool expected)
    {
        Assert.Equal(
            expected,
            BackgroundInputAutomationService.UsesSystemKeyMessage(virtualKey, altDown));
    }

    [Fact]
    public void AltContextBitUsesTheStateAfterTheAltTransition()
    {
        Assert.True(BackgroundInputAutomationService.IsAltDownAfterTransition(
            [],
            0x12,
            keyUp: false));
        Assert.True(BackgroundInputAutomationService.IsAltDownAfterTransition(
            [0x12, 0x41],
            0x41,
            keyUp: true));
        Assert.False(BackgroundInputAutomationService.IsAltDownAfterTransition(
            [0x12],
            0x12,
            keyUp: true));
    }

    [Theory]
    [InlineData("Ctrl+A", new ushort[] { 0x11, 0x41 })]
    [InlineData("control+f24", new ushort[] { 0x11, 0x87 })]
    [InlineData("Esc", new ushort[] { 0x1B })]
    public void ForegroundAndBackgroundShareTheSameKeyParser(
        string key,
        ushort[] expected)
    {
        Assert.Equal(expected, KeyboardInputParser.ParseChord(key));
    }

    [Theory]
    [InlineData("")]
    [InlineData("+")]
    [InlineData("Unknown")]
    public void UnsupportedKeyNamesAreRejected(string key)
    {
        Assert.Throws<InvalidOperationException>(
            () => KeyboardInputParser.ParseChord(key));
    }

    [Fact]
    public void ConsecutiveClickSequenceRestartsWhenTheTargetChildWindowChanges()
    {
        var sequence = new BackgroundClickSequence(nint.Zero, 0);

        sequence = BackgroundInputAutomationService.AdvanceClickSequence(sequence, (nint)101);
        Assert.Equal(1, sequence.ConsecutiveClickCount);
        sequence = BackgroundInputAutomationService.AdvanceClickSequence(sequence, (nint)101);
        Assert.Equal(2, sequence.ConsecutiveClickCount);

        sequence = BackgroundInputAutomationService.AdvanceClickSequence(sequence, (nint)202);
        Assert.Equal(1, sequence.ConsecutiveClickCount);
        sequence = BackgroundInputAutomationService.AdvanceClickSequence(sequence, (nint)202);
        Assert.Equal(2, sequence.ConsecutiveClickCount);
    }
}

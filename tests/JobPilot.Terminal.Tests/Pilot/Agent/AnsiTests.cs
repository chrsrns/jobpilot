using JobPilot.Terminal.Pilot.Agent;
using Xunit;

namespace JobPilot.Terminal.Tests.Pilot.Agent;

public class AnsiTests
{
    [Theory]
    [InlineData("\x1b[1;31mred\x1b[0m", "red")]
    [InlineData("\x1b[>4;2mkeys\x1b[<u", "keys")]
    [InlineData("\x1b]0;title\x07text", "text")]
    [InlineData("\x1b]8;;https://x\x1b\\link", "link")]
    [InlineData("done\x1b[3", "done")]
    public void Strip_RemovesEscapeSequences(string input, string expected) =>
        Assert.Equal(expected, Ansi.Strip(input));

    [Theory]
    // OSC queries stripped; OSC sets and regular CSI survive a replay.
    [InlineData("a\x1b]10;?\ab", "ab")]
    [InlineData("a\x1b]11;?\x1b\\b", "ab")]
    [InlineData("a\x1b]4;0;?;1;?\ab", "ab")]
    [InlineData("a\x1b]10;rgb:f4f4/f2f2/eeee\ab", "a\x1b]10;rgb:f4f4/f2f2/eeee\ab")]
    [InlineData("a\x1b]8;;https://x?y=1\x1b\\b", "a\x1b]8;;https://x?y=1\x1b\\b")]
    // DA, DSR/CPR, DECRQM, DECRQSS, DCS stripped.
    [InlineData("a\x1b[>cb", "ab")]
    [InlineData("a\x1b[cb", "ab")]
    [InlineData("a\x1b[6nb", "ab")]
    [InlineData("a\x1b[?6$pb", "ab")]
    [InlineData("a\x1b[2;1$qb", "ab")]
    [InlineData("a\x1bP+q544e\x1b\\b", "ab")]
    // Non-query CSI kept, including ?-param mode sets.
    [InlineData("a\x1b[?25hb", "a\x1b[?25hb")]
    [InlineData("a\x1b[1;31mb", "a\x1b[1;31mb")]
    // Truncated tail of a query sequence is dropped whole.
    [InlineData("a\x1b]10;", "a")]
    [InlineData("a\x1b[6", "a")]
    public void StripElicits_DropsOnlyReplyElicitingSequences(string input, string expected) =>
        Assert.Equal(expected, Ansi.StripElicits(input));
}

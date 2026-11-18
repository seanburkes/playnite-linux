using Playnite.Platform.Linux.Sessions;
using Xunit;

namespace Playnite.Core.Tests.Sessions;

public sealed class LinuxProcStatTests
{
    [Fact]
    public void Parses_state_parent_and_start_time_after_a_command_name_with_spaces_and_parentheses()
    {
        var stat = LinuxProcStat.Parse(
            "1234 (game (x86) with space) S 1 1234 1234 0 -1 4194304 1 0 0 0 0 0 0 0 20 0 1 0 987654 0 0");

        Assert.Equal(1234, stat.ProcessId);
        Assert.Equal(1, stat.ParentProcessId);
        Assert.Equal('S', stat.State);
        Assert.Equal(1234, stat.ProcessGroupId);
        Assert.Equal(987654, stat.StartTimeTicks);
        Assert.True(stat.IsRunning);
    }

    [Theory]
    [InlineData('Z')]
    [InlineData('X')]
    [InlineData('x')]
    public void Zombie_and_dead_states_are_not_running(char state)
    {
        var stat = LinuxProcStat.Parse($"10 (game) {state} 1 10 10 0 -1 0 0 0 0 0 0 0 0 0 20 0 1 0 55 0 0");
        Assert.False(stat.IsRunning);
    }

    [Theory]
    [InlineData("")]
    [InlineData("42 no-command-name S 1")]
    [InlineData("not-a-pid (game) S 1")]
    [InlineData("42 (game) S 1")]
    [InlineData("42 (game) S not-a-parent 1 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 1 0")]
    public void Malformed_rows_are_rejected(string text)
    {
        Assert.Throws<FormatException>(() => LinuxProcStat.Parse(text));
    }
}

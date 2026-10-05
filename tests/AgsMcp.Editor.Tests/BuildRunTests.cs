using AgsMcp.Editor.Tools;
using Xunit;

namespace AgsMcp.Editor.Tests
{
    public class BuildRunTests
    {
        [Fact]
        public void EngineArgs_NoStartRoom()
        {
            // First arg is the log DIRECTORY (engine writes ags.log inside it).
            string args = BuildRunTools.BuildEngineArgs(@"C:\tmp\run", @"C:\tmp\ud", null);
            Assert.Equal(
                "--windowed --background --no-message-box --log-file=all:debug " +
                "--log-file-path=\"C:\\tmp\\run\" --user-data-dir \"C:\\tmp\\ud\"",
                args);
        }

        [Fact]
        public void EngineArgs_WithStartRoom()
        {
            string args = BuildRunTools.BuildEngineArgs(@"C:\a\run", @"C:\a\ud", 3);
            Assert.EndsWith("--startr 3", args);
            // log-file-path uses '=', user-data-dir is space-separated (per engine OPTIONS.md)
            Assert.Contains("--log-file-path=\"C:\\a\\run\"", args);
            Assert.Contains("--user-data-dir \"C:\\a\\ud\"", args);
        }

        [Fact]
        public void SliceLog_ReturnsAllFromStart()
        {
            var s = BuildRunTools.SliceLog("a\nb\nc", 0, 500);
            Assert.Equal(3, s.TotalLines);
            Assert.Equal(0, s.FromLine);
            Assert.Equal(3, s.NextLine);
            Assert.False(s.Truncated);
            Assert.Equal(new[] { "a", "b", "c" }, s.Lines.ToArray());
        }

        [Fact]
        public void SliceLog_TailsFromOffset()
        {
            var s = BuildRunTools.SliceLog("a\nb\nc\nd", 2, 500);
            Assert.Equal(2, s.FromLine);
            Assert.Equal(4, s.NextLine);
            Assert.Equal(new[] { "c", "d" }, s.Lines.ToArray());
        }

        [Fact]
        public void SliceLog_RespectsLimitAndReportsTruncation()
        {
            var s = BuildRunTools.SliceLog("a\nb\nc\nd\ne", 0, 2);
            Assert.Equal(5, s.TotalLines);
            Assert.Equal(2, s.NextLine);
            Assert.True(s.Truncated);
            Assert.Equal(new[] { "a", "b" }, s.Lines.ToArray());
        }

        [Fact]
        public void SliceLog_NormalizesCrLf()
        {
            var s = BuildRunTools.SliceLog("a\r\nb\r\nc", 0, 500);
            Assert.Equal(new[] { "a", "b", "c" }, s.Lines.ToArray());
        }

        [Fact]
        public void SliceLog_OffsetPastEndReturnsNothing()
        {
            var s = BuildRunTools.SliceLog("a\nb", 10, 500);
            Assert.Equal(2, s.TotalLines);
            Assert.Equal(2, s.FromLine);
            Assert.Empty(s.Lines);
            Assert.False(s.Truncated);
        }

        [Fact]
        public void SliceLog_HandlesEmpty()
        {
            var s = BuildRunTools.SliceLog("", 0, 500);
            Assert.Equal(1, s.TotalLines); // one empty line
            Assert.Equal(new[] { "" }, s.Lines.ToArray());
        }
    }
}

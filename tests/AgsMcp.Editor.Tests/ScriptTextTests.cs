using System;
using AgsMcp.Editor.Tools;
using Xunit;

namespace AgsMcp.Editor.Tests
{
    public class ScriptTextTests
    {
        [Theory]
        [InlineData("", 0)]
        [InlineData("one", 1)]
        [InlineData("one\ntwo", 2)]
        [InlineData("one\r\ntwo", 2)]
        [InlineData("one\ntwo\n", 2)]       // trailing newline does not add an empty line
        [InlineData("one\r\ntwo\r\n", 2)]
        public void CountLines(string text, int expected)
        {
            Assert.Equal(expected, ScriptText.CountLines(text));
        }

        [Fact]
        public void ExtractLineRange_ReturnsInclusiveRange()
        {
            string text = "a\nb\nc\nd\ne";
            string r = ScriptText.ExtractLineRange(text, 2, 4, out int total);
            Assert.Equal(5, total);
            Assert.Equal("b\nc\nd", r);
        }

        [Fact]
        public void ExtractLineRange_ZeroEnd_MeansToEnd()
        {
            string r = ScriptText.ExtractLineRange("a\nb\nc", 2, 0, out int total);
            Assert.Equal(3, total);
            Assert.Equal("b\nc", r);
        }

        [Fact]
        public void ExtractLineRange_ClampsAndNormalizesCrlf()
        {
            string r = ScriptText.ExtractLineRange("a\r\nb\r\nc", 1, 100, out int total);
            Assert.Equal(3, total);
            Assert.Equal("a\nb\nc", r);
        }

        [Fact]
        public void ExtractLineRange_StartPastEnd_ReturnsEmpty()
        {
            string r = ScriptText.ExtractLineRange("a\nb", 10, 20, out int total);
            Assert.Equal(2, total);
            Assert.Equal("", r);
        }

        [Fact]
        public void ApplyEdit_ReplacesSingleOccurrence()
        {
            var res = ScriptText.ApplyEdit("int x = 1;", "x", "y", replaceAll: false);
            Assert.Equal(1, res.Count);
            Assert.Equal("int y = 1;", res.NewText);
        }

        [Fact]
        public void ApplyEdit_CountsMultipleWithoutReplacingAll()
        {
            var res = ScriptText.ApplyEdit("a a a", "a", "b", replaceAll: false);
            Assert.Equal(3, res.Count);
            Assert.Equal("b a a", res.NewText); // only the first is replaced when not replaceAll
        }

        [Fact]
        public void ApplyEdit_ReplaceAll()
        {
            var res = ScriptText.ApplyEdit("a a a", "a", "b", replaceAll: true);
            Assert.Equal(3, res.Count);
            Assert.Equal("b b b", res.NewText);
        }

        [Fact]
        public void ApplyEdit_NotFound_CountIsZero()
        {
            var res = ScriptText.ApplyEdit("hello", "zzz", "y", replaceAll: false);
            Assert.Equal(0, res.Count);
            Assert.Equal("hello", res.NewText);
        }

        [Fact]
        public void ApplyEdit_MatchesAcrossNewlineStyles()
        {
            // File uses CRLF; the caller's find/replace use LF. The match must still work and
            // the result must keep the file's CRLF style.
            string file = "line1\r\nline2\r\nline3";
            var res = ScriptText.ApplyEdit(file, "line1\nline2", "changed\nhere", replaceAll: false);
            Assert.Equal(1, res.Count);
            Assert.Equal("changed\r\nhere\r\nline3", res.NewText);
        }

        [Fact]
        public void ApplyEdit_MatchesInMixedNewlineFile()
        {
            // A file written with LF, then extended by the editor's generator with CRLF. A multi-line
            // find in the LF region must still match, and the result must come out uniform.
            string file = "function a()\n{\n  x();\n}\n\nfunction b()\r\n{\r\n}\r\n";
            var res = ScriptText.ApplyEdit(file, "{\n  x();\n}", "{\n  y();\n}", replaceAll: false);
            Assert.Equal(1, res.Count);
            Assert.Equal("function a()\r\n{\r\n  y();\r\n}\r\n\r\nfunction b()\r\n{\r\n}\r\n", res.NewText);
        }

        [Fact]
        public void ApplyEdit_EmptyFind_Throws()
        {
            Assert.Throws<ArgumentException>(() => ScriptText.ApplyEdit("x", "", "y", false));
        }

        [Theory]
        [InlineData("a\r\nb", "\n", "a\nb")]
        [InlineData("a\rb", "\n", "a\nb")]
        [InlineData("a\nb", "\r\n", "a\r\nb")]
        public void NormalizeNewlines(string input, string nl, string expected)
        {
            Assert.Equal(expected, ScriptText.NormalizeNewlines(input, nl));
        }
    }
}

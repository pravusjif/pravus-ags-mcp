using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AgsMcp.Editor.Tools;
using Xunit;

namespace AgsMcp.Editor.Tests
{
    public class WfnFontTests
    {
        // A synthetic font: 'A' 5x7, 'B' 6x9, ' ' 2x1; every other code has no glyph.
        private static byte[] BuildWfn()
        {
            var glyphs = new Dictionary<int, int[]> { { 'A', new[] { 5, 7 } }, { 'B', new[] { 6, 9 } }, { ' ', new[] { 2, 1 } } };
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(Encoding.ASCII.GetBytes(WfnFont.Signature));
                w.Write((ushort)0); // table address, patched below
                var offsets = new Dictionary<int, int>();
                foreach (var g in glyphs)
                {
                    offsets[g.Key] = (int)ms.Position;
                    int width = g.Value[0], height = g.Value[1];
                    w.Write((ushort)width);
                    w.Write((ushort)height);
                    w.Write(new byte[((width + 7) / 8) * height]);
                }
                int table = (int)ms.Position;
                for (int code = 0; code <= 'B'; code++)
                    w.Write((ushort)(offsets.TryGetValue(code, out int off) ? off : 0));
                ms.Position = WfnFont.Signature.Length;
                w.Write((ushort)table);
                return ms.ToArray();
            }
        }

        private static WfnTextMetrics Metrics(int multiplier = 1, int outline = 0, int lineSpacing = 0) =>
            new WfnTextMetrics { Font = WfnFont.Parse(BuildWfn()), SizeMultiplier = multiplier, AutoOutlineThickness = outline, LineSpacing = lineSpacing };

        [Fact]
        public void Parse_ReadsGlyphSizes()
        {
            WfnFont font = WfnFont.Parse(BuildWfn());
            Assert.Equal('B' + 1, font.CharCount);
            Assert.Equal(5, font.GlyphWidth('A'));
            Assert.Equal(9, font.GlyphHeight('B'));
            Assert.Equal(0, font.GlyphWidth('C')); // past the table: empty
            Assert.Equal(0, font.GlyphWidth('#')); // offset 0: no glyph
        }

        [Fact]
        public void Parse_RefusesBadSignature()
        {
            byte[] data = BuildWfn();
            data[0] = (byte)'X';
            Assert.Throws<FormatException>(() => WfnFont.Parse(data));
            Assert.Throws<FormatException>(() => WfnFont.Parse(new byte[4]));
        }

        [Fact]
        public void Width_SumsGlyphsTimesMultiplierPlusOutline()
        {
            Assert.Equal(11, Metrics().Width("AB"));
            Assert.Equal(22, Metrics(multiplier: 2).Width("AB"));
            Assert.Equal(13, Metrics(outline: 1).Width("AB"));
            Assert.Equal(0, Metrics(outline: 1).Width(""));
        }

        [Fact]
        public void Heights_AndLineStep()
        {
            WfnFont font = WfnFont.Parse(BuildWfn());
            Assert.Equal(9, font.TextHeight("AB", 1));
            Assert.Equal(7, font.TextHeight("A", 1));
            Assert.Equal(18, font.TextHeight("B", 2));
            // The font height is measured on "ZHwypgfjqhkilIK", none of which this font has.
            Assert.Equal(0, font.FontHeight(1));
            var m = Metrics(outline: 1, lineSpacing: 10);
            Assert.Equal(10, m.LineStep);
            Assert.Equal(2, Metrics(outline: 1).LineStep); // 0 + 2x1
            Assert.Equal(10 * 2 + 2, m.LinesHeight(3));
        }

        [Fact]
        public void Wrap_AtLastSpace()
        {
            // maxWidth 8 wraps at 7: "A " = 7 fits, "A B" = 13 does not.
            Assert.Equal(new[] { "A", "B" }, Metrics().Wrap("A B", 8));
            Assert.Equal(new[] { "A B" }, Metrics().Wrap("A B", 14));
            Assert.Equal(new[] { "A B" }, Metrics().Wrap("A B", 0));
        }

        [Fact]
        public void Wrap_InsideAWordWhenThereIsNoSpace()
        {
            // maxWidth 12 wraps at 11: "AA" = 10, "AAA" = 15.
            Assert.Equal(new[] { "AA", "AA" }, Metrics().Wrap("AAAA", 12));
        }

        [Fact]
        public void Wrap_NothingFitsIsNull()
        {
            Assert.Null(Metrics().Wrap("A", 3));
        }

        [Fact]
        public void Wrap_BracketBreaksAndEscapedBracketIsLiteral()
        {
            Assert.Equal(new[] { "A", "B" }, Metrics().Wrap("A[B", 0));
            Assert.Equal(new[] { "A[B" }, Metrics().Wrap(@"A\[B", 0));
            Assert.Equal(new[] { "", "A" }, Metrics().Wrap("[A", 0));
            Assert.Equal(new[] { "A" }, Metrics().Wrap("A[", 0));
        }

        [Fact]
        public void Unescape_MatchesTheEngine()
        {
            Assert.Equal("a\nb\nc", WfnTextMetrics.Unescape("a[b[c"));
            Assert.Equal("a[b", WfnTextMetrics.Unescape(@"a\[b"));
            Assert.Equal("\nx", WfnTextMetrics.Unescape("[x"));
            Assert.Equal(string.Empty, WfnTextMetrics.Unescape(null));
        }
    }

    public class StringLiteralTests
    {
        [Fact]
        public void ExtractStringLiterals_KeepsTextAsWrittenWithLines()
        {
            string script = "function f() {\r\n  player.Say(\"Hello \\\"you\\\"\");\r\n  Display(\"A\"); Display(\"B\");\r\n}";
            var lits = ScriptText.ExtractStringLiterals(script);
            Assert.Equal(3, lits.Count);
            Assert.Equal("Hello \\\"you\\\"", lits[0].Text);
            Assert.Equal(2, lits[0].Line);
            Assert.Equal("A", lits[1].Text);
            Assert.Equal(3, lits[2].Line);
        }

        [Fact]
        public void ExtractStringLiterals_SkipsCommentsAndCharLiterals()
        {
            string script = "// \"not this\"\n/* \"nor\n this\" */ int c = '\"';\nString s = \"yes\\\\\"; // \"no\"";
            var lits = ScriptText.ExtractStringLiterals(script);
            Assert.Single(lits);
            Assert.Equal("yes\\\\", lits[0].Text);
            Assert.Equal(4, lits[0].Line); // the block comment spans lines 2-3
        }

        [Fact]
        public void ExtractStringLiterals_DropsUnterminatedAndResumesNextLine()
        {
            var lits = ScriptText.ExtractStringLiterals("Display(\"broken);\nDisplay(\"ok\");");
            Assert.Single(lits);
            Assert.Equal("ok", lits[0].Text);
            Assert.Equal(2, lits[0].Line);
        }
    }
}

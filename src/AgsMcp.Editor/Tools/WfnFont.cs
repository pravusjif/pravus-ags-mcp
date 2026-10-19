using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AgsMcp.Editor.Tools
{
    /// <summary>
    /// A WFN bitmap font's glyph sizes, and the engine's text metrics over them (Common/font/wfnfont.cpp,
    /// wfnfontrenderer.cpp, fonts.cpp). Pure: no editor types, so it is unit-tested.
    /// File layout: the 15-byte signature "WGT Font File  ", a u16 offset table address, glyph records
    /// {u16 width, u16 height, ((width+7)/8)*height bytes}, then the u16 table of absolute glyph offsets
    /// (one per character code; 0 = no glyph), running to the end of the file. Little-endian.
    /// </summary>
    internal sealed class WfnFont
    {
        public const string Signature = "WGT Font File  ";
        private const int HeaderSize = 17; // signature + table address
        private const string HeightTestString = "ZHwypgfjqhkilIK"; // fonts.cpp font_post_init

        private readonly int[] _widths, _heights;

        private WfnFont(int[] widths, int[] heights) { _widths = widths; _heights = heights; }

        public int CharCount => _widths.Length;

        /// <summary>Parses a WFN file. Throws FormatException for a bad signature or table address.</summary>
        public static WfnFont Parse(byte[] data)
        {
            if (data == null || data.Length < HeaderSize || Encoding.ASCII.GetString(data, 0, Signature.Length) != Signature)
                throw new FormatException("Not a WFN font (bad signature).");
            int table = U16(data, Signature.Length);
            if (table < HeaderSize || table >= data.Length)
                throw new FormatException($"Bad WFN offset table address {table}.");
            int count = (data.Length - table) / 2;
            var widths = new int[count];
            var heights = new int[count];
            for (int code = 0; code < count; code++)
            {
                int off = U16(data, table + code * 2);
                // A missing glyph (0) or an offset outside the glyph data is an empty character, as in the engine.
                if (off == 0 || off < HeaderSize || off + 4 > table) continue;
                widths[code] = U16(data, off);
                heights[code] = U16(data, off + 2);
            }
            return new WfnFont(widths, heights);
        }

        private static int U16(byte[] d, int at) => d[at] | (d[at + 1] << 8);

        public int GlyphWidth(int code) => code >= 0 && code < _widths.Length ? _widths[code] : 0;
        public int GlyphHeight(int code) => code >= 0 && code < _heights.Length ? _heights[code] : 0;

        /// <summary>WFNFontRenderer.GetTextWidth: the sum of glyph widths, times the size multiplier.</summary>
        public int TextWidth(string text, int sizeMultiplier) => Codes(text).Sum(GlyphWidth) * sizeMultiplier;

        /// <summary>WFNFontRenderer.GetTextHeight: the tallest glyph, times the size multiplier.</summary>
        public int TextHeight(string text, int sizeMultiplier) =>
            Codes(text).Select(GlyphHeight).DefaultIfEmpty(0).Max() * sizeMultiplier;

        /// <summary>The font height the engine uses for a WFN font (its renderer reports none, so it measures a test string).</summary>
        public int FontHeight(int sizeMultiplier) => TextHeight(HeightTestString, sizeMultiplier);

        /// <summary>Character codes of a string: one per code point. The caller maps text to the game's encoding first.</summary>
        public static IEnumerable<int> Codes(string text)
        {
            if (string.IsNullOrEmpty(text)) yield break;
            for (int i = 0; i < text.Length; i++)
            {
                if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    yield return char.ConvertToUtf32(text[i], text[i + 1]);
                    i++;
                }
                else yield return text[i];
            }
        }
    }

    /// <summary>
    /// A font as the engine measures it: a WFN font with its size multiplier and outline (fonts.cpp
    /// get_text_width_outlined, get_font_height_with_outline, font_post_init's line spacing, split_lines).
    /// </summary>
    internal sealed class WfnTextMetrics
    {
        public WfnFont Font;
        public int SizeMultiplier = 1;
        /// <summary>Thickness of an automatic outline; 0 when the outline is not automatic (the engine zeroes it).</summary>
        public int AutoOutlineThickness;
        /// <summary>A separate outline font (OutlineStyle = UseOutlineFont), or null.</summary>
        public WfnFont OutlineFont;
        public int OutlineSizeMultiplier = 1;
        /// <summary>The font's LineSpacing property; 0 = default (font height plus the automatic outline).</summary>
        public int LineSpacing;

        /// <summary>get_text_width_outlined: 0 for empty text; the outline adds 2×thickness, an outline font takes the wider.</summary>
        public int Width(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            int self = Font.TextWidth(text, SizeMultiplier);
            if (OutlineFont != null) return Math.Max(self, OutlineFont.TextWidth(text, OutlineSizeMultiplier));
            return self + 2 * AutoOutlineThickness;
        }

        /// <summary>The font's own height (no outline), as get_font_height reports it.</summary>
        public int FontHeight => Font.FontHeight(SizeMultiplier);

        /// <summary>get_font_height_outlined.</summary>
        public int FontHeightOutlined => OutlineFont != null
            ? Math.Max(FontHeight, OutlineFont.FontHeight(OutlineSizeMultiplier))
            : FontHeight + 2 * AutoOutlineThickness;

        /// <summary>The step between lines: LineSpacing, or the font height plus the automatic outline.</summary>
        public int LineStep => LineSpacing > 0 ? LineSpacing : FontHeight + 2 * AutoOutlineThickness;

        /// <summary>get_text_lines_height: the step between lines times (lines - 1), plus one outlined line.</summary>
        public int LinesHeight(int lines) => lines <= 0 ? 0 : LineStep * (lines - 1) + FontHeightOutlined;

        /// <summary>
        /// fonts.cpp unescape_script_string: '[' is a line break and "\[" a literal '['. Returns the text with
        /// '\n' for the breaks.
        /// </summary>
        public static string Unescape(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            var sb = new StringBuilder(text.Length);
            int start = 0;
            if (text[0] == '[') { sb.Append('\n'); start = 1; }
            for (int i = start; i < text.Length; i++)
            {
                if (text[i] != '[') continue;
                if (i > 0 && text[i - 1] == '\\')
                {
                    sb.Append(text, start, i - 1 - start).Append('[');
                }
                else
                {
                    sb.Append(text, start, i - start).Append('\n');
                }
                start = i + 1;
            }
            sb.Append(text, start, text.Length - start);
            return sb.ToString();
        }

        /// <summary>
        /// fonts.cpp split_lines: the lines the engine draws the text in at a wrap width (maxWidth &lt;= 0: no
        /// wrapping, breaks only). The engine wraps at maxWidth - 1 (a legacy off-by-one it keeps), at the last
        /// space of the line, else inside a word. Returns null when not even one character fits, where the engine
        /// draws nothing.
        /// </summary>
        public List<string> Wrap(string text, int maxWidth)
        {
            int limit = maxWidth > 0 ? maxWidth - 1 : int.MaxValue;
            int[] buf = WfnFont.Codes(Unescape(text)).ToArray();
            var lines = new List<string>();
            var test = new List<int>();
            int theLine = 0, scan = 0, lastSpace = -1;
            while (true)
            {
                if (scan == buf.Length)
                {
                    if (scan > theLine) lines.Add(ToText(test));
                    break;
                }
                int prev = scan;
                if (buf[scan] == ' ') lastSpace = scan;
                int splitAt = -1;
                if (buf[scan] == '\n')
                {
                    splitAt = scan;
                    scan++;
                }
                else
                {
                    test.Add(buf[scan]);
                    scan++;
                    if (Width(ToText(test)) > limit) splitAt = lastSpace >= 0 ? lastSpace : prev;
                }
                if (splitAt < 0) continue;

                if (splitAt == theLine && buf[theLine] != ' ' && buf[theLine] != '\n') return null; // nothing fits
                test.RemoveRange(splitAt - theLine, test.Count - (splitAt - theLine));
                lines.Add(ToText(test));
                test.Clear();
                theLine = splitAt;
                if (buf[theLine] == ' ' || buf[theLine] == '\n') theLine++;
                scan = theLine;
                lastSpace = -1;
            }
            return lines;
        }

        private static string ToText(List<int> codes)
        {
            var sb = new StringBuilder(codes.Count);
            foreach (int c in codes)
            {
                if (c < 0x10000) sb.Append((char)c); // includes a lone surrogate, which ConvertFromUtf32 refuses
                else sb.Append(char.ConvertFromUtf32(c));
            }
            return sb.ToString();
        }
    }
}

using System;
using System.Text.RegularExpressions;

namespace AgsMcp.Editor.Tools
{
    /// <summary>
    /// Pure text operations behind the script tools, kept free of editor types so they can be unit-tested.
    /// Line numbers are 1-based and inclusive, matching how the editor and compiler report them.
    /// </summary>
    internal static class ScriptText
    {
        /// <summary>Number of lines of content. A trailing newline does not add an empty line.</summary>
        public static int CountLines(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            string norm = NormalizeNewlines(text, "\n");
            int newlines = 0;
            foreach (char c in norm) if (c == '\n') newlines++;
            // A trailing newline terminates the last line rather than starting an empty one.
            return norm.EndsWith("\n") ? newlines : newlines + 1;
        }

        /// <summary>
        /// Returns lines [startLine, endLine] (1-based, inclusive), joined with "\n". The range is
        /// clamped to the available lines; endLine &lt;= 0 means "to the end".
        /// </summary>
        public static string ExtractLineRange(string text, int startLine, int endLine, out int totalLines)
        {
            string[] lines = NormalizeNewlines(text ?? string.Empty, "\n").Split('\n');
            // A trailing newline produces a final empty element that is not real content.
            int count = lines.Length;
            if (count > 0 && lines[count - 1].Length == 0 && (text ?? string.Empty).Length > 0) count--;
            totalLines = count;

            if (count == 0) return string.Empty;
            if (startLine < 1) startLine = 1;
            if (endLine <= 0 || endLine > count) endLine = count;
            if (startLine > count) return string.Empty;
            if (endLine < startLine) endLine = startLine;

            return string.Join("\n", lines, startLine - 1, endLine - startLine + 1);
        }

        public sealed class EditResult
        {
            public string NewText;
            public int Count;
        }

        /// <summary>
        /// Exact find/replace. Newlines in the text, <paramref name="find"/> and <paramref name="replace"/>
        /// are all normalized to one style before matching, so a match depends neither on how the caller
        /// encoded line breaks nor on a file with mixed endings (the editor's own stub generator appends
        /// CRLF, so a file written with LF becomes mixed). <see cref="EditResult.Count"/> is the number of
        /// occurrences found; <see cref="EditResult.NewText"/> is the text (with uniform newlines) with the
        /// first (or all, when <paramref name="replaceAll"/>) occurrence replaced. The caller decides how to
        /// treat a zero or ambiguous count.
        /// </summary>
        public static EditResult ApplyEdit(string text, string find, string replace, bool replaceAll)
        {
            if (string.IsNullOrEmpty(find)) throw new ArgumentException("find must not be empty.", nameof(find));
            text = text ?? string.Empty;
            string nl = text.Contains("\r\n") ? "\r\n" : "\n";
            string t = NormalizeNewlines(text, nl);
            string f = NormalizeNewlines(find, nl);
            string r = NormalizeNewlines(replace ?? string.Empty, nl);

            int count = CountOccurrences(t, f);
            string newText = count == 0
                ? text
                : replaceAll ? t.Replace(f, r) : ReplaceFirst(t, f, r);
            return new EditResult { NewText = newText, Count = count };
        }

        public static int CountOccurrences(string text, string value)
        {
            if (string.IsNullOrEmpty(value)) return 0;
            int count = 0, index = 0;
            while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += value.Length;
            }
            return count;
        }

        private static string ReplaceFirst(string text, string value, string replacement)
        {
            int index = text.IndexOf(value, StringComparison.Ordinal);
            if (index < 0) return text;
            return text.Substring(0, index) + replacement + text.Substring(index + value.Length);
        }

        /// <summary>Converts every \r\n, lone \r and lone \n in the text to <paramref name="newline"/>.</summary>
        public static string NormalizeNewlines(string text, string newline)
        {
            if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
            return text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", newline);
        }

        /// <summary>
        /// True when the script defines a plain function of that name at the start of a line
        /// (<c>function name(</c>, <c>int name(</c>, <c>static void name(</c>, ...). Import lines, calls and
        /// struct member functions (<c>Type::name</c>) do not count.
        /// </summary>
        public static bool DefinesFunction(string text, string name)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(name)) return false;
            var definition = new Regex(@"^[ \t]*(?:(?:static|protected)[ \t]+)*(?!import\b|return\b|else\b)[A-Za-z_]\w*[ \t]*\*?[ \t]+" +
                                       Regex.Escape(name) + @"[ \t]*\(", RegexOptions.Multiline);
            return definition.IsMatch(text);
        }
    }
}

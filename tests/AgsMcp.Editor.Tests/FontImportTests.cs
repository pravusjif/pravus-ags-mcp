using AgsMcp.Editor.Tools;
using Xunit;

namespace AgsMcp.Editor.Tests
{
    public class FontImportTests
    {
        [Theory]
        // The enum is internal, so the expected kind is passed by name (a public test method cannot take it).
        [InlineData(@"C:\fonts\Serif.ttf", "Ttf")]
        [InlineData(@"C:\fonts\SERIF.TTF", "Ttf")]
        [InlineData(@"C:\g\agsfnt0.wfn", "Wfn")]
        [InlineData(@"C:\g\AGSFNT1.WFN", "Wfn")]
        [InlineData(@"C:\fonts\Serif.ttf ", "Ttf")]
        public void Classify_TtfAndWfn(string path, string expected)
        {
            Assert.Equal(expected, FontImport.Classify(path).ToString());
        }

        [Theory]
        [InlineData(@"C:\fonts\font.000")]
        [InlineData(@"C:\fonts\Serif.otf")]
        [InlineData(@"C:\fonts\Serif.ttf.bak")]
        [InlineData(@"C:\fonts\ttf")]
        [InlineData("")]
        [InlineData(null)]
        public void Classify_OtherIsRefused(string path)
        {
            Assert.Equal(FontFileKind.Other, FontImport.Classify(path));
        }
    }
}

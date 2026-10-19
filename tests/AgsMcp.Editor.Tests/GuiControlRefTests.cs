using AgsMcp.Editor.Mcp;
using AgsMcp.Editor.Tools;
using Xunit;

namespace AgsMcp.Editor.Tests
{
    public class GuiControlRefTests
    {
        [Theory]
        [InlineData("gMain:btnOk", "gMain", "btnOk")]
        [InlineData("2:0", "2", "0")]
        [InlineData("gMain:0", "gMain", "0")]
        [InlineData(" gMain : btnOk ", "gMain", "btnOk")]
        public void Parse_SplitsGuiAndControl(string id, string gui, string control)
        {
            var r = GuiTools.GuiControlRef.Parse(id);
            Assert.Equal(gui, r.Gui);
            Assert.Equal(control, r.Control);
        }

        [Theory]
        [InlineData("btnOk")]
        [InlineData("gMain:")]
        [InlineData(":btnOk")]
        [InlineData("a:b:c")]
        [InlineData("")]
        [InlineData(null)]
        public void Parse_RefusesBadForms(string id)
        {
            var e = Assert.Throws<ToolException>(() => GuiTools.GuiControlRef.Parse(id));
            Assert.Contains("<gui>:<control>", e.Message);
        }
    }
}

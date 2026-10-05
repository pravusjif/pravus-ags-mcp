using AgsMcp.Editor.Ui;
using Xunit;

namespace AgsMcp.Editor.Tests
{
    public class ClientSetupTests
    {
        private const string Url = "http://127.0.0.1:7471/mcp";

        [Theory]
        [InlineData("claude mcp add --transport http ags " + Url)]
        [InlineData("url = \"" + Url + "\"")]
        [InlineData("\"httpUrl\": \"" + Url + "\"")]
        [InlineData("-y mcp-remote " + Url)]
        public void ListsEachClientWithTheUrl(string expected)
        {
            Assert.Contains(expected, ClientSetup.Text(Url));
        }

        [Fact]
        public void UsesTheConfiguredPort()
        {
            string text = ClientSetup.Text("http://127.0.0.1:9000/mcp");
            Assert.DoesNotContain("7471", text);
            Assert.Contains("http://127.0.0.1:9000/mcp", text);
        }
    }
}

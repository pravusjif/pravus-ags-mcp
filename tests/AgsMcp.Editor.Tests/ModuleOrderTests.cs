using AgsMcp.Editor.Mcp;
using AgsMcp.Editor.Tools;
using Xunit;

namespace AgsMcp.Editor.Tests
{
    public class ModuleOrderTests
    {
        [Theory]
        [InlineData(4, 2, "up", 0, -1)]
        [InlineData(4, 0, "up", 0, 0)]
        [InlineData(4, 1, "down", 0, 1)]
        [InlineData(4, 3, "down", 0, 0)]
        [InlineData(4, 3, "index", 0, -3)]
        [InlineData(4, 0, "index", 3, 3)]
        [InlineData(4, 2, "index", 2, 0)]
        [InlineData(1, 0, "down", 0, 0)]
        public void Plan_CountsSignedSteps(int count, int from, string direction, int index, int expected)
        {
            Assert.Equal(expected, ModuleOrder.Plan(count, from, direction, index));
        }

        [Theory]
        [InlineData(4, 1, "index", 4)]
        [InlineData(4, 1, "index", -1)]
        [InlineData(4, 1, "sideways", 0)]
        [InlineData(4, 4, "up", 0)]
        [InlineData(0, 0, "up", 0)]
        public void Plan_RefusesBadInput(int count, int from, string direction, int index)
        {
            Assert.Throws<ToolException>(() => ModuleOrder.Plan(count, from, direction, index));
        }
    }
}

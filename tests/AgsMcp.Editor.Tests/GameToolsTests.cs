using AgsMcp.Editor.Engine;
using AgsMcp.Editor.Mcp;
using Newtonsoft.Json.Linq;
using Xunit;

namespace AgsMcp.Editor.Tests
{
    public class GameToolsTests
    {
        [Theory]
        [InlineData("Space", 32)]
        [InlineData("enter", 13)]
        [InlineData("Return", 13)]
        [InlineData("Escape", 27)]
        [InlineData("esc", 27)]
        [InlineData("F5", 363)]
        [InlineData("f12", 434)]
        [InlineData("Up", 372)]
        [InlineData("LeftArrow", 375)]
        [InlineData("Delete", 383)]
        public void KeyMap_NamedKeys(string name, int code)
        {
            Assert.Equal(code, KeyMap.Resolve(name));
        }

        [Theory]
        [InlineData("A", 65)]
        [InlineData("a", 65)]
        [InlineData("z", 90)]
        [InlineData("0", 48)]
        [InlineData("9", 57)]
        public void KeyMap_SingleChars(string name, int code)
        {
            Assert.Equal(code, KeyMap.Resolve(name));
        }

        [Fact]
        public void KeyMap_RawNumber()
        {
            Assert.Equal(363, KeyMap.Resolve("363"));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("NotAKey")]
        public void KeyMap_Unknown_Throws(string name)
        {
            Assert.Throws<ToolException>(() => KeyMap.Resolve(name));
        }

        [Fact]
        public void Wait_Room_MatchesTarget()
        {
            var state = new JObject { ["room"] = 5 };
            Assert.True(GameWait.IsSatisfied(state, GameWait.ConditionRoom, 5));
            Assert.False(GameWait.IsSatisfied(state, GameWait.ConditionRoom, 6));
        }

        [Fact]
        public void Wait_Room_RequiresTarget()
        {
            var state = new JObject { ["room"] = 5 };
            Assert.False(GameWait.IsSatisfied(state, GameWait.ConditionRoom, null));
        }

        [Fact]
        public void Wait_InterfaceEnabled()
        {
            Assert.True(GameWait.IsSatisfied(new JObject { ["interfaceEnabled"] = 1 }, GameWait.ConditionInterfaceEnabled, null));
            Assert.False(GameWait.IsSatisfied(new JObject { ["interfaceEnabled"] = 0 }, GameWait.ConditionInterfaceEnabled, null));
        }

        [Fact]
        public void Wait_Idle_RequiresNotWalkingNotAnimating()
        {
            var idle = new JObject { ["player"] = new JObject { ["walking"] = false, ["animating"] = false } };
            var walking = new JObject { ["player"] = new JObject { ["walking"] = true, ["animating"] = false } };
            var animating = new JObject { ["player"] = new JObject { ["walking"] = false, ["animating"] = true } };
            Assert.True(GameWait.IsSatisfied(idle, GameWait.ConditionIdle, null));
            Assert.False(GameWait.IsSatisfied(walking, GameWait.ConditionIdle, null));
            Assert.False(GameWait.IsSatisfied(animating, GameWait.ConditionIdle, null));
        }

        [Fact]
        public void Wait_UnknownCondition_IsFalse()
        {
            Assert.False(GameWait.IsSatisfied(new JObject(), "nope", null));
        }

        [Fact]
        public void Ready_NeedsInterfaceAndIdlePlayer()
        {
            JObject State(int ui, bool walking) => new JObject { ["interfaceEnabled"] = ui, ["player"] = new JObject { ["walking"] = walking, ["animating"] = false } };
            Assert.True(GameWait.IsSatisfied(State(1, false), GameWait.ConditionReady, null));
            Assert.False(GameWait.IsSatisfied(State(0, false), GameWait.ConditionReady, null));
            Assert.False(GameWait.IsSatisfied(State(1, true), GameWait.ConditionReady, null));
            Assert.False(GameWait.IsSatisfied(null, GameWait.ConditionIdle, null));
        }
    }
}

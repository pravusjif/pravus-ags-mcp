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

        [Theory]
        [InlineData(8)]   // Backspace
        [InlineData(9)]   // Tab
        [InlineData(13)]  // Enter
        [InlineData(359)] // F1
        [InlineData(368)] // F10
        [InlineData(433)] // F11
        [InlineData(434)] // F12
        [InlineData(371)] // Home
        [InlineData(372)] // Up
        [InlineData(373)] // PageUp
        [InlineData(375)] // Left
        [InlineData(377)] // Right
        [InlineData(379)] // End
        [InlineData(380)] // Down
        [InlineData(381)] // PageDown
        [InlineData(382)] // Insert
        [InlineData(383)] // Delete
        public void KeyMap_WindowRoutedKeys(int code)
        {
            Assert.True(KeyMap.IsWindowRouted(code));
        }

        [Theory]
        [InlineData(1)]   // Ctrl+A stays SimulateKeyPress
        [InlineData(27)]  // Escape
        [InlineData(32)]  // Space
        [InlineData(65)]  // A
        [InlineData(48)]  // 0
        [InlineData(374)] // keypad codes between the navigation keys
        [InlineData(376)]
        [InlineData(378)]
        [InlineData(369)]
        [InlineData(384)]
        [InlineData(301)] // Alt+A
        public void KeyMap_OtherKeysUseSimulateKeyPress(int code)
        {
            Assert.False(KeyMap.IsWindowRouted(code));
        }

        [Fact]
        public void KeyMap_NavigationAndFunctionKeyNamesAreWindowRouted()
        {
            foreach (string name in new[] { "Home", "Up", "PageUp", "Left", "Right", "End", "Down", "PageDown", "Insert", "Delete", "F1", "F7", "F11", "F12" })
                Assert.True(KeyMap.IsWindowRouted(KeyMap.Resolve(name)), name);
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

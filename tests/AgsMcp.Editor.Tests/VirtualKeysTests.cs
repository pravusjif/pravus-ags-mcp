using AgsMcp.Editor.Engine;
using AgsMcp.Editor.Mcp;
using Xunit;

namespace AgsMcp.Editor.Tests
{
    public class VirtualKeysTests
    {
        [Theory]
        [InlineData("Escape", 0x1B)]
        [InlineData("esc", 0x1B)]
        [InlineData("Enter", 0x0D)]
        [InlineData("Return", 0x0D)]
        [InlineData("Space", 0x20)]
        [InlineData("Tab", 0x09)]
        [InlineData("Backspace", 0x08)]
        [InlineData("a", 0x41)]
        [InlineData("Z", 0x5A)]
        [InlineData("7", 0x37)]
        [InlineData("F1", 0x70)]
        [InlineData("f12", 0x7B)]
        [InlineData("F24", 0x87)]
        [InlineData("Numpad5", 0x65)]
        [InlineData(" Up ", 0x26)]
        [InlineData("0x5B", 0x5B)]
        public void Resolve_Names(string name, int vk)
        {
            Assert.Equal(vk, VirtualKeys.Resolve(name).Code);
        }

        [Theory]
        [InlineData("PageUp", 0x21)]
        [InlineData("PageDown", 0x22)]
        [InlineData("End", 0x23)]
        [InlineData("Home", 0x24)]
        [InlineData("Left", 0x25)]
        [InlineData("Up", 0x26)]
        [InlineData("Right", 0x27)]
        [InlineData("Down", 0x28)]
        [InlineData("Insert", 0x2D)]
        [InlineData("Delete", 0x2E)]
        public void Resolve_NavigationKeysAreExtended(string name, int vk)
        {
            VirtualKey k = VirtualKeys.Resolve(name);
            Assert.Equal(vk, k.Code);
            Assert.True(k.Extended);
        }

        [Theory]
        [InlineData("Escape")]
        [InlineData("Enter")]
        [InlineData("A")]
        [InlineData("Numpad8")]
        [InlineData("F7")]
        [InlineData("0x2C")]
        public void Resolve_OtherKeysAreNotExtended(string name)
        {
            Assert.False(VirtualKeys.Resolve(name).Extended);
        }

        [Theory]
        [InlineData("")]
        [InlineData("F25")]
        [InlineData("F0")]
        [InlineData("Banana")]
        [InlineData("é")]
        [InlineData("0x")]
        [InlineData("0x1FF")]
        public void Resolve_RefusesUnknownKeys(string name)
        {
            Assert.Throws<ToolException>(() => VirtualKeys.Resolve(name));
        }

        [Fact]
        public void ResolveChord_ReadsModifiersInOrder()
        {
            KeyChord c = VirtualKeys.ResolveChord("Shift+Ctrl+Q");
            Assert.Equal(0x51, c.Key.Code);
            Assert.Equal(new[] { 0x10, 0x11 }, c.Modifiers.ConvertAll(m => (int)m.Code));
        }

        [Fact]
        public void ResolveChord_PlainKeyHasNoModifiers()
        {
            KeyChord c = VirtualKeys.ResolveChord("Up");
            Assert.Equal(0x26, c.Key.Code);
            Assert.True(c.Key.Extended);
            Assert.Empty(c.Modifiers);
        }

        [Theory]
        [InlineData("A+B")]
        [InlineData("Ctrl+Ctrl+Q")]
        [InlineData("Ctrl+")]
        public void ResolveChord_RefusesBadChords(string text)
        {
            Assert.Throws<ToolException>(() => VirtualKeys.ResolveChord(text));
        }
    }
}

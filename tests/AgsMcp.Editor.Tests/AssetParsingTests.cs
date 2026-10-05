using System.Linq;
using AGS.Types;
using AgsMcp.Editor.Mcp;
using AgsMcp.Editor.Tools;
using Newtonsoft.Json.Linq;
using Xunit;

namespace AgsMcp.Editor.Tests
{
    public class AssetParsingTests
    {
        [Theory]
        [InlineData("LeaveAsIs", SpriteImportTransparency.LeaveAsIs)]
        [InlineData("leaveasis", SpriteImportTransparency.LeaveAsIs)]
        [InlineData("NoTransparency", SpriteImportTransparency.NoTransparency)]
        [InlineData("TopLeft", SpriteImportTransparency.TopLeft)]
        [InlineData("", SpriteImportTransparency.LeaveAsIs)]
        [InlineData(null, SpriteImportTransparency.LeaveAsIs)]
        public void ParsesTransparency(string input, SpriteImportTransparency expected)
        {
            Assert.Equal(expected, AssetTools.ParseTransparency(input));
        }

        [Fact]
        public void RejectsUnknownTransparency()
        {
            Assert.Throws<ToolException>(() => AssetTools.ParseTransparency("Magenta"));
        }

        [Fact]
        public void IntegerFrameIsSpriteNumberWithDefaults()
        {
            var f = AssetTools.ParseFrame(new JValue(7));
            Assert.Equal(7, f.Image);
            Assert.Equal(0, f.Delay);
            Assert.False(f.Flipped);
            Assert.Null(f.Sound);
        }

        [Fact]
        public void ObjectFrameReadsAllFields()
        {
            var f = AssetTools.ParseFrame(JObject.Parse("{\"sprite\":12,\"delay\":5,\"flipped\":true,\"sound\":3}"));
            Assert.Equal(12, f.Image);
            Assert.Equal(5, f.Delay);
            Assert.True(f.Flipped);
            Assert.Equal(3, f.Sound);
        }

        [Fact]
        public void ObjectFrameAcceptsImageAlias()
        {
            var f = AssetTools.ParseFrame(JObject.Parse("{\"image\":4}"));
            Assert.Equal(4, f.Image);
        }

        [Theory]
        [InlineData("\"notanumber\"")]
        [InlineData("{\"delay\":5}")]
        [InlineData("[1,2]")]
        public void RejectsBadFrame(string json)
        {
            Assert.Throws<ToolException>(() => AssetTools.ParseFrame(JToken.Parse(json)));
        }

        [Fact]
        public void ArrayLoopsBecomeLoopsOfFrames()
        {
            var loops = AssetTools.ParseViewLoops(JArray.Parse("[[0,1,2],[3,4]]"));
            Assert.Equal(2, loops.Count);
            Assert.Equal(new[] { 0, 1, 2 }, loops[0].Frames.Select(f => f.Image).ToArray());
            Assert.Equal(new[] { 3, 4 }, loops[1].Frames.Select(f => f.Image).ToArray());
            Assert.False(loops[0].RunNextLoop);
        }

        [Fact]
        public void ObjectLoopCarriesRunNextLoop()
        {
            var loops = AssetTools.ParseViewLoops(JArray.Parse("[{\"frames\":[0,1],\"runNextLoop\":true}]"));
            Assert.Single(loops);
            Assert.True(loops[0].RunNextLoop);
            Assert.Equal(2, loops[0].Frames.Count);
        }

        [Fact]
        public void SchemaFormParses()
        {
            var loops = AssetTools.ParseViewLoops(JArray.Parse(
                "[{\"frames\":[{\"sprite\":5,\"delay\":4},{\"sprite\":6,\"flipped\":true}],\"runNextLoop\":true},{\"frames\":[{\"sprite\":7}]}]"));
            Assert.Equal(2, loops.Count);
            Assert.True(loops[0].RunNextLoop);
            Assert.Equal(new[] { 5, 6 }, loops[0].Frames.Select(f => f.Image).ToArray());
            Assert.Equal(4, loops[0].Frames[0].Delay);
            Assert.True(loops[0].Frames[1].Flipped);
            Assert.Equal(7, loops[1].Frames[0].Image);
        }

        [Fact]
        public void SchemaDeclaresOneObjectFormPerLevel()
        {
            JObject schema = AssetTools.CreateViewSchema();
            JToken loop = schema["properties"]["loops"]["items"];
            JToken frame = loop["properties"]["frames"]["items"];
            Assert.Equal("object", (string)loop["type"]);
            Assert.Equal("object", (string)frame["type"]);
            Assert.Equal("integer", (string)frame["properties"]["sprite"]["type"]);
            Assert.Contains("sprite", frame["required"].Values<string>());
            Assert.Contains("frames", loop["required"].Values<string>());
            string json = schema.ToString();
            Assert.DoesNotContain("anyOf", json);
            Assert.DoesNotContain("oneOf", json);
        }

        [Fact]
        public void EmptyLoopsRejected()
        {
            Assert.Throws<ToolException>(() => AssetTools.ParseViewLoops(JArray.Parse("[]")));
        }

        [Fact]
        public void LoopObjectWithoutFramesRejected()
        {
            Assert.Throws<ToolException>(() => AssetTools.ParseViewLoops(JArray.Parse("[{\"runNextLoop\":true}]")));
        }
    }
}

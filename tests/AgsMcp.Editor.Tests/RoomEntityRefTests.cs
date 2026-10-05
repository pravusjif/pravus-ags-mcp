using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using AgsMcp.Editor.Mcp;
using AgsMcp.Editor.Tools;
using Newtonsoft.Json.Linq;
using Xunit;

namespace AgsMcp.Editor.Tests
{
    public class RoomEntityRefTests
    {
        [Fact]
        public void Room_HasNoIndex()
        {
            var r = RoomTools.ParseEntity("room");
            Assert.Equal("room", r.Type);
            Assert.Equal(-1, r.Index);
        }

        [Theory]
        [InlineData("hotspot:3", "hotspot", 3)]
        [InlineData("hotspot 3", "hotspot", 3)]
        [InlineData("hotspot3", "hotspot", 3)]
        [InlineData("HOTSPOT:0", "hotspot", 0)]
        [InlineData("object:2", "object", 2)]
        [InlineData("region:1", "region", 1)]
        [InlineData("walkablearea:1", "walkablearea", 1)]
        [InlineData("walkable:2", "walkablearea", 2)]
        [InlineData("walkbehind:4", "walkbehind", 4)]
        public void ParsesTypeAndIndex(string input, string type, int index)
        {
            var r = RoomTools.ParseEntity(input);
            Assert.Equal(type, r.Type);
            Assert.Equal(index, r.Index);
        }

        [Theory]
        [InlineData("hDoor")]
        [InlineData("oKey")]
        [InlineData("hotspotDoor")]
        public void ParsesScriptNames(string input)
        {
            var r = RoomTools.ParseEntity(input);
            Assert.Equal("name", r.Type);
            Assert.Equal(input, r.Name);
        }

        [Theory]
        [InlineData("")]
        [InlineData("hotspot:x")]
        [InlineData("hotspot")]
        [InlineData("widget:1")]
        [InlineData("my door")]
        public void RejectsBadInput(string input)
        {
            Assert.Throws<ToolException>(() => RoomTools.ParseEntity(input));
        }
    }

    public class RoomMaskShapeTests
    {
        [Fact]
        public void ParsesEveryShapeType()
        {
            var shapes = RoomTools.ParseShapes(JArray.Parse(@"[
                {type:'rect', x:1, y:2, width:3, height:4},
                {type:'rect', x1:0, y1:0, x2:9, y2:4},
                {type:'polygon', points:[[0,0],[10,0],{x:5,y:8}]},
                {type:'ellipse', x:50, y:50, rx:10, ry:5},
                {type:'line', x1:0, y1:0, x2:5, y2:5},
                {type:'fill', x:3, y:3}]"));
            Assert.Equal(new[] { "rect", "rect", "polygon", "ellipse", "line", "fill" }, shapes.Select(s => s.Type));
            Assert.Equal(10, shapes[1].Width);
            Assert.Equal(5, shapes[1].Height);
            Assert.Equal(new Point(5, 8), shapes[2].Points[2]);
        }

        [Theory]
        [InlineData("[]")]
        [InlineData("[{type:'star'}]")]
        [InlineData("[{type:'rect', x:1, y:1}]")]
        [InlineData("[{type:'rect', x:1, y:1, width:0, height:2}]")]
        [InlineData("[{type:'polygon', points:[[0,0],[1,1]]}]")]
        [InlineData("[{type:'ellipse', x:1, y:1, rx:0, ry:2}]")]
        [InlineData("[5]")]
        public void RejectsBadShapes(string json)
        {
            Assert.Throws<ToolException>(() => RoomTools.ParseShapes(JArray.Parse(json)));
        }

        [Fact]
        public void PolygonSpans_FillRectangleExactly()
        {
            // A 10x4 axis-aligned square from (0,0) to (10,4) covers pixel columns 0..9 on rows 0..3.
            List<int[]> spans = RoomTools.PolygonSpans(new[] { new Point(0, 0), new Point(10, 0), new Point(10, 4), new Point(0, 4) });
            Assert.Equal(4, spans.Count);
            Assert.All(spans, s => { Assert.Equal(0, s[1]); Assert.Equal(9, s[2]); });
            Assert.Equal(new[] { 0, 1, 2, 3 }, spans.Select(s => s[0]));
        }

        [Fact]
        public void PolygonSpans_TriangleNarrowsTowardsApex()
        {
            List<int[]> spans = RoomTools.PolygonSpans(new[] { new Point(0, 10), new Point(20, 10), new Point(10, 0) });
            int Width(int[] s) => s[2] - s[1] + 1;
            Assert.True(Width(spans.First()) < Width(spans.Last()));
            Assert.All(spans, s => Assert.InRange(s[1], 0, 20));
        }
    }

    public class RoomScriptNameTests
    {
        [Theory]
        [InlineData("room1", 1)]
        [InlineData("Room12.asc", 12)]
        [InlineData("room:3", 3)]
        [InlineData("room 4", 4)]
        public void RecognisesRoomScripts(string name, int number)
        {
            Assert.True(ScriptTools.TryParseRoomScript(name, out int n));
            Assert.Equal(number, n);
        }

        [Theory]
        [InlineData("GlobalScript")]
        [InlineData("room")]
        [InlineData("room1.ash")]
        [InlineData("roomScripts")]
        public void IgnoresOtherNames(string name)
        {
            Assert.False(ScriptTools.TryParseRoomScript(name, out _));
        }
    }
}

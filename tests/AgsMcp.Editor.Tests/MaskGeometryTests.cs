using System.Linq;
using AgsMcp.Editor.Mcp;
using AgsMcp.Editor.Tools;
using Xunit;

namespace AgsMcp.Editor.Tests
{
    public class MaskGeometryTests
    {
        // Rows of digits, one character per pixel: the area number.
        private static byte[,] Mask(params string[] rows)
        {
            var m = new byte[rows.Length, rows[0].Length];
            for (int y = 0; y < rows.Length; y++)
                for (int x = 0; x < rows[y].Length; x++)
                    m[y, x] = (byte)(rows[y][x] - '0');
            return m;
        }

        [Fact]
        public void Centroid_IsTheRoundedMeanOfTheAreaPixels()
        {
            byte[,] m = Mask(
                "000000",
                "011100",
                "011100",
                "011100",
                "000022");
            MaskPoint c = MaskGeometry.Centroid(m, 1);
            Assert.Equal(2, c.X);
            Assert.Equal(2, c.Y);
            MaskPoint c2 = MaskGeometry.Centroid(m, 2);
            Assert.Equal(5, c2.X); // mean 4.5 rounds away from zero
            Assert.Equal(4, c2.Y);
        }

        [Fact]
        public void Centroid_IsNullForAnAbsentArea()
        {
            Assert.Null(MaskGeometry.Centroid(Mask("010", "000"), 3));
            Assert.Empty(MaskGeometry.SamplePoints(Mask("010", "000"), 3, 8));
        }

        [Fact]
        public void SamplePoints_StartAtTheCentroidWhenItIsInside()
        {
            byte[,] m = Mask(
                "11111",
                "11111",
                "11111");
            var pts = MaskGeometry.SamplePoints(m, 1, 8);
            Assert.Equal("2,1", pts[0].ToString());
            Assert.Equal(pts.Count, pts.Select(p => p.ToString()).Distinct().Count());
            Assert.True(pts.Count <= 8);
            Assert.All(pts, p => Assert.Equal(1, m[p.Y, p.X]));
        }

        [Fact]
        public void SamplePoints_OnAConcaveArea_StartInsideTheArea()
        {
            // A "U": the centroid (2,1) is in the gap, which belongs to no area.
            byte[,] m = Mask(
                "11011",
                "11011",
                "11111");
            MaskPoint c = MaskGeometry.Centroid(m, 1);
            Assert.Equal(0, m[c.Y, c.X]);
            var pts = MaskGeometry.SamplePoints(m, 1, 8);
            Assert.NotEmpty(pts);
            Assert.All(pts, p => Assert.Equal(1, m[p.Y, p.X]));
            Assert.Equal("1,1", pts[0].ToString()); // nearest the centroid; ties go to reading order
        }

        [Fact]
        public void SamplePoints_HonourMax()
        {
            byte[,] m = Mask(
                "111111111",
                "111111111",
                "111111111",
                "111111111",
                "111111111",
                "111111111");
            Assert.Equal(3, MaskGeometry.SamplePoints(m, 1, 3).Count);
            Assert.Equal(10, MaskGeometry.SamplePoints(m, 1, 20).Count); // the centroid (4,3) + the 9 cell centres
        }

        [Fact]
        public void RectSamplePoints_StartAtTheCentreAndStayInside()
        {
            var pts = MaskGeometry.RectSamplePoints(10, 20, 30, 60, 8);
            Assert.Equal("24,49", pts[0].ToString());
            Assert.Equal(8, pts.Count);
            Assert.All(pts, p => Assert.InRange(p.X, 10, 39));
            Assert.All(pts, p => Assert.InRange(p.Y, 20, 79));
            Assert.Empty(MaskGeometry.RectSamplePoints(0, 0, 0, 5, 8));
        }

        [Theory]
        [InlineData("hotspot:3", "hotspot", 3, null)]
        [InlineData("Object: 2", "object", 2, null)]
        [InlineData("character:cGuard", "character", null, "cGuard")]
        [InlineData("char:0", "character", 0, null)]
        [InlineData("hDoor", null, null, "hDoor")]
        public void InteractionTarget_Parse(string text, string kind, int? id, string name)
        {
            var t = GameTools.InteractionTarget.Parse(text);
            Assert.Equal(kind, t.Kind);
            Assert.Equal(id, t.Id);
            Assert.Equal(name, t.Name);
        }

        [Theory]
        [InlineData("")]
        [InlineData("3")]
        [InlineData("region:1")]
        [InlineData("hotspot:a b")]
        public void InteractionTarget_RefusesBadInput(string text)
        {
            Assert.Throws<ToolException>(() => GameTools.InteractionTarget.Parse(text));
        }
    }
}

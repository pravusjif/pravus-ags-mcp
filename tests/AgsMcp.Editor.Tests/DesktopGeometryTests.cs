using AgsMcp.Editor.Engine;
using Xunit;

namespace AgsMcp.Editor.Tests
{
    public class DesktopGeometryTests
    {
        [Fact]
        public void ExactFit_HasNoOffset()
        {
            GameArea a = DesktopGeometry.Area(960, 600, 320, 200);
            Assert.Equal(3, a.Scale);
            Assert.Equal(0, a.OffsetX);
            Assert.Equal(0, a.OffsetY);
            Assert.Equal(960, a.Width);
            Assert.Equal(600, a.Height);
        }

        [Fact]
        public void Letterbox_CentresTheLargestWholeScale()
        {
            // 1000x700 fits 320x200 three times across (960) and three times down (600).
            GameArea a = DesktopGeometry.Area(1000, 700, 320, 200);
            Assert.Equal(3, a.Scale);
            Assert.Equal(20, a.OffsetX);
            Assert.Equal(50, a.OffsetY);

            // A wide window is limited by its height.
            GameArea wide = DesktopGeometry.Area(1920, 450, 320, 200);
            Assert.Equal(2, wide.Scale);
            Assert.Equal((1920 - 640) / 2, wide.OffsetX);
            Assert.Equal(25, wide.OffsetY);
        }

        [Fact]
        public void WindowSmallerThanTheGame_IsScaleOneWithANegativeOffset()
        {
            Assert.Equal(1, DesktopGeometry.Scale(300, 150, 320, 200));
            GameArea a = DesktopGeometry.Area(301, 150, 320, 200);
            Assert.Equal(1, a.Scale);
            Assert.Equal(-10, a.OffsetX); // floor(-19 / 2)
            Assert.Equal(-25, a.OffsetY);
        }

        [Fact]
        public void GameToScreen_HitsThePixelCentre()
        {
            GameArea a = DesktopGeometry.Area(1000, 700, 320, 200); // scale 3, offset 20,50
            DesktopGeometry.GameToScreen(a, 100, 200, 0, 0, out int x0, out int y0);
            Assert.Equal(100 + 20 + 1, x0);
            Assert.Equal(200 + 50 + 1, y0);
            DesktopGeometry.GameToScreen(a, 100, 200, 319, 199, out int x1, out int y1);
            Assert.Equal(100 + 20 + 319 * 3 + 1, x1);
            Assert.Equal(200 + 50 + 199 * 3 + 1, y1);
        }
    }
}

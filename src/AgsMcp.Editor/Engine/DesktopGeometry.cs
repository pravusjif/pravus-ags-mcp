using System;

namespace AgsMcp.Editor.Engine
{
    /// <summary>Where the game image sits inside a window's client area: an integer scale and a centring offset.</summary>
    internal sealed class GameArea
    {
        public int Scale;
        public int OffsetX;
        public int OffsetY;
        public int GameWidth;
        public int GameHeight;
        public int Width => GameWidth * Scale;
        public int Height => GameHeight * Scale;
    }

    /// <summary>
    /// Pure window geometry for the desktop driver. AGS draws a windowed game at the largest whole-number scale that
    /// fits the client area (its default "round" filter) and centres it, letterboxing the rest.
    /// </summary>
    internal static class DesktopGeometry
    {
        /// <summary>max(1, min(floor(cw/W), floor(ch/H))): a window smaller than the game still counts as scale 1.</summary>
        public static int Scale(int clientWidth, int clientHeight, int gameWidth, int gameHeight)
        {
            if (gameWidth <= 0 || gameHeight <= 0) throw new ArgumentException("The game size must be positive.");
            return Math.Max(1, Math.Min(clientWidth / gameWidth, clientHeight / gameHeight));
        }

        /// <summary>The scaled game image's placement in the client area (offsets are negative when it does not fit).</summary>
        public static GameArea Area(int clientWidth, int clientHeight, int gameWidth, int gameHeight)
        {
            int s = Scale(clientWidth, clientHeight, gameWidth, gameHeight);
            return new GameArea
            {
                Scale = s,
                OffsetX = FloorHalf(clientWidth - gameWidth * s),
                OffsetY = FloorHalf(clientHeight - gameHeight * s),
                GameWidth = gameWidth,
                GameHeight = gameHeight,
            };
        }

        /// <summary>
        /// The screen point at the centre of game pixel (gx, gy), given the client area's top-left corner on screen.
        /// </summary>
        public static void GameToScreen(GameArea area, int clientLeft, int clientTop, int gx, int gy, out int sx, out int sy)
        {
            sx = clientLeft + area.OffsetX + gx * area.Scale + area.Scale / 2;
            sy = clientTop + area.OffsetY + gy * area.Scale + area.Scale / 2;
        }

        private static int FloorHalf(int v) => (int)Math.Floor(v / 2.0);
    }
}

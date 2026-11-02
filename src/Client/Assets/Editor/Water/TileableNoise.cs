using UnityEngine;

namespace Mmorpg.Client.Editor
{
    // Noise that repeats exactly over the unit square, so textures made from it tile without seams.
    // Every lattice coordinate wraps by the period of its octave, and an integer hash places the
    // gradients and cell points, so the same seed always gives the same texture.
    internal static class TileableNoise
    {
        // Gradient noise at (u, v) in the unit square with the given lattice cells per side, in
        // about -0.7 to 0.7.
        public static float Gradient(float u, float v, int period, uint seed)
        {
            return Gradient(u, v, period, period, seed);
        }

        // Gradient noise with its own number of cells across and down, so its features stretch,
        // such as streaks that run down a waterfall.
        public static float Gradient(float u, float v, int periodX, int periodY, uint seed)
        {
            var x = u * periodX;
            var y = v * periodY;
            var cellX = Mathf.FloorToInt(x);
            var cellY = Mathf.FloorToInt(y);
            var fractionX = x - cellX;
            var fractionY = y - cellY;
            var fadeX = Fade(fractionX);
            var fadeY = Fade(fractionY);
            var bottom = Mathf.Lerp(
                Corner(cellX, cellY, fractionX, fractionY, periodX, periodY, seed),
                Corner(cellX + 1, cellY, fractionX - 1f, fractionY, periodX, periodY, seed),
                fadeX);
            var top = Mathf.Lerp(
                Corner(cellX, cellY + 1, fractionX, fractionY - 1f, periodX, periodY, seed),
                Corner(cellX + 1, cellY + 1, fractionX - 1f, fractionY - 1f, periodX, periodY, seed),
                fadeX);
            return Mathf.Lerp(bottom, top, fadeY);
        }

        // Octaves of gradient noise, each with twice the cells and half the weight, in about
        // -0.7 to 0.7.
        public static float Fractal(float u, float v, int period, int octaves, uint seed)
        {
            return Fractal(u, v, period, period, octaves, seed);
        }

        public static float Fractal(float u, float v, int periodX, int periodY, int octaves, uint seed)
        {
            var sum = 0f;
            var weight = 0.5f;
            var total = 0f;
            for (var octave = 0; octave < octaves; octave++)
            {
                sum += weight * Gradient(u, v, periodX << octave, periodY << octave, seed + (uint)(octave * 101));
                total += weight;
                weight *= 0.5f;
            }

            return sum / total;
        }

        // The distances in cells from (u, v) to the nearest and the second nearest of one random
        // point per cell.
        public static void Cells(float u, float v, int period, uint seed, out float nearest, out float second)
        {
            var x = u * period;
            var y = v * period;
            var cellX = Mathf.FloorToInt(x);
            var cellY = Mathf.FloorToInt(y);
            nearest = float.MaxValue;
            second = float.MaxValue;
            for (var offsetY = -1; offsetY <= 1; offsetY++)
            {
                for (var offsetX = -1; offsetX <= 1; offsetX++)
                {
                    var neighborX = cellX + offsetX;
                    var neighborY = cellY + offsetY;
                    var hash = Hash(Wrap(neighborX, period), Wrap(neighborY, period), seed);
                    var pointX = neighborX + Unit(hash);
                    var pointY = neighborY + Unit(hash * 0x9E3779B9u + 0x7F4A7C15u);
                    var distance = Mathf.Sqrt(((pointX - x) * (pointX - x)) + ((pointY - y) * (pointY - y)));
                    if (distance < nearest)
                    {
                        second = nearest;
                        nearest = distance;
                    }
                    else if (distance < second)
                    {
                        second = distance;
                    }
                }
            }
        }

        private static float Corner(int cellX, int cellY, float offsetX, float offsetY, int periodX, int periodY, uint seed)
        {
            var angle = Unit(Hash(Wrap(cellX, periodX), Wrap(cellY, periodY), seed)) * Mathf.PI * 2f;
            return (Mathf.Cos(angle) * offsetX) + (Mathf.Sin(angle) * offsetY);
        }

        private static float Fade(float value)
        {
            return value * value * value * ((value * ((value * 6f) - 15f)) + 10f);
        }

        private static int Wrap(int value, int period)
        {
            var wrapped = value % period;
            return wrapped < 0 ? wrapped + period : wrapped;
        }

        private static uint Hash(int x, int y, uint seed)
        {
            var hash = ((uint)x * 0x8DA6B343u) ^ ((uint)y * 0xD8163841u) ^ (seed * 0xCB1AB31Fu);
            hash ^= hash >> 13;
            hash *= 0x85EBCA6Bu;
            hash ^= hash >> 16;
            hash *= 0xC2B2AE35u;
            hash ^= hash >> 15;
            return hash;
        }

        private static float Unit(uint hash)
        {
            return (hash & 0xFFFFFFu) / 16777216f;
        }
    }
}

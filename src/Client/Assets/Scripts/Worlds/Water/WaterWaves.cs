using UnityEngine;

namespace Mmorpg.Client.Worlds
{
    // The waves that lift the sea, computed as the water shader computes them, so presentation
    // can put things on the moving surface. Three directional waves of falling length and
    // height, relative to the material's wave length and height; each travels at the speed of a
    // deep water wave of its length, relative to the material's wave speed. A vertex rises by the
    // waves at its position times the green of its color.
    public static class WaterWaves
    {
        // Per wave: its direction in world X and Z, and its length and height. The shaders read
        // them through WaterShaderGlobals.
        internal static readonly Vector4[] Mix =
        {
            new Vector4(0.96f, 0.28f, 1f, 1f),
            new Vector4(-0.37f, 0.93f, 0.63f, 0.55f),
            new Vector4(0.6f, -0.8f, 0.41f, 0.3f)
        };

        // The height of the waves at a point in world X and Z at the water's clock.
        public static float Height(float x, float z, float clock, WaterWaveSettings settings)
        {
            if (!settings.HasWaves)
            {
                return 0f;
            }

            var height = 0f;
            for (var index = 0; index < Mix.Length; index++)
            {
                var wave = Mix[index];
                var meters = settings.Length * wave.z;
                var waveNumber = (2f * Mathf.PI) / meters;
                var cycles = WaterShaderGlobals.Cycles(settings.Speed * Mathf.Sqrt(wave.z) / meters, clock);
                var phase = (waveNumber * ((wave.x * x) + (wave.y * z))) - (2f * Mathf.PI * cycles);
                height += settings.Amplitude * wave.w * Mathf.Sin(phase);
            }

            return height;
        }
    }

    // The waves of one water material: how high, how long, and how fast.
    public readonly struct WaterWaveSettings
    {
        public WaterWaveSettings(float amplitude, float length, float speed)
        {
            Amplitude = amplitude;
            Length = length;
            Speed = speed;
        }

        public float Amplitude { get; }

        public float Length { get; }

        public float Speed { get; }

        public bool HasWaves
        {
            get { return Amplitude > 0f && Length > 0f; }
        }

        // The waves a water material draws, or none for another material.
        public static WaterWaveSettings Of(Material material)
        {
            if (material == null
                || !material.HasProperty("_WaveAmplitude")
                || !material.HasProperty("_WaveLength")
                || !material.HasProperty("_WaveSpeed"))
            {
                return default;
            }

            return new WaterWaveSettings(
                material.GetFloat("_WaveAmplitude"),
                material.GetFloat("_WaveLength"),
                material.GetFloat("_WaveSpeed"));
        }
    }
}

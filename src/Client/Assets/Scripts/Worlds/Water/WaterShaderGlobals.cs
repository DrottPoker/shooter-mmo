using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Mmorpg.Client.Worlds
{
    // The global inputs of the water shaders, which WaterCommon.hlsl reads: the water's clock,
    // which wraps at ClockPeriod in double precision before it reaches the GPU, so every
    // animation of the water stays precise however long the game runs, and the mix of waves that
    // lifts the sea, so the client computes the same surface as the shader. They are set before
    // every rendered frame, in the game and in the Editor alike.
    public static class WaterShaderGlobals
    {
        // An hour. Animation rates round to whole cycles per period, so no water animation jumps
        // when the clock wraps.
        public const double ClockPeriod = 3600d;

        private static readonly int ClockId = Shader.PropertyToID("_WaterClock");
        private static readonly int WavesId = Shader.PropertyToID("_WaterWaves");

        // The water's clock of the current frame, in seconds from 0 up to the period: game time
        // while playing, and real time in the Editor otherwise.
        public static float Clock
        {
            get { return Wrap(Application.isPlaying ? Time.timeAsDouble : Time.realtimeSinceStartupAsDouble); }
        }

        public static float Wrap(double seconds)
        {
            var wrapped = seconds % ClockPeriod;
            return (float)(wrapped < 0d ? wrapped + ClockPeriod : wrapped);
        }

        // How far an animation that repeats the given number of times per second is through its
        // cycle at the clock, from 0 to 1, rounded as WaterCycles in WaterCommon.hlsl rounds it.
        public static float Cycles(float cyclesPerSecond, float clock)
        {
            var perPeriod = Mathf.Floor((cyclesPerSecond * (float)ClockPeriod) + 0.5f);
            var cycles = perPeriod * (clock / (float)ClockPeriod);
            return cycles - Mathf.Floor(cycles);
        }

        public static void Apply()
        {
            Shader.SetGlobalFloat(ClockId, Clock);
            Shader.SetGlobalVectorArray(WavesId, WaterWaves.Mix);
        }

#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
#endif
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Install()
        {
            RenderPipelineManager.beginContextRendering -= OnBeginContextRendering;
            RenderPipelineManager.beginContextRendering += OnBeginContextRendering;
            Apply();
        }

        private static void OnBeginContextRendering(ScriptableRenderContext context, List<Camera> cameras)
        {
            Apply();
        }
    }
}

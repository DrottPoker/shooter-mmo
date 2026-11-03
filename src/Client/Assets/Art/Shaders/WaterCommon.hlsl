#ifndef MMORPG_WATER_COMMON_INCLUDED
#define MMORPG_WATER_COMMON_INCLUDED

// What the water shaders share: the water's clock and the waves of the sea, which
// WaterShaderGlobals sets before every frame. See docs/ART_PIPELINE.md under Water.

// The water's clock in seconds. The client wraps it at the period in double precision before it
// reaches the GPU, so every animation of the water stays precise however long the game runs.
#define WATER_CLOCK_PERIOD 3600.0
float _WaterClock;

// Per wave of the sea: its direction in world X and Z, and its length and height relative to the
// material's. WaterWaves holds the same values, so the client puts things on the same surface.
#define WATER_WAVE_COUNT 3
float4 _WaterWaves[WATER_WAVE_COUNT];

// How far an animation that repeats the given number of times per second is through its cycle,
// from 0 to 1. The rate rounds to whole cycles per period of the clock, so the animation runs on
// without a jump when the clock wraps.
float WaterCycles(float cyclesPerSecond)
{
    return frac(floor((cyclesPerSecond * WATER_CLOCK_PERIOD) + 0.5) * (_WaterClock / WATER_CLOCK_PERIOD));
}

float2 WaterCycles(float2 cyclesPerSecond)
{
    return frac(floor((cyclesPerSecond * WATER_CLOCK_PERIOD) + 0.5) * (_WaterClock / WATER_CLOCK_PERIOD));
}

// The height of the waves at a position in world X and Z, before the vertex's own weight, and the
// normal of their surface. Each wave travels at the speed of a deep water wave of its length,
// relative to the material's wave speed.
float WaterWaveHeight(float2 position, float amplitude, float waveLength, float speed, out float3 normal)
{
    float height = 0;
    float2 slope = 0;
    [unroll]
    for (int index = 0; index < WATER_WAVE_COUNT; index++)
    {
        float4 wave = _WaterWaves[index];
        if (wave.z > 0)
        {
            float meters = waveLength * wave.z;
            float waveNumber = TWO_PI / meters;
            float phase = (waveNumber * dot(wave.xy, position)) - (TWO_PI * WaterCycles(speed * sqrt(wave.z) / meters));
            float waveAmplitude = amplitude * wave.w;
            height += waveAmplitude * sin(phase);
            slope += waveAmplitude * waveNumber * cos(phase) * wave.xy;
        }
    }

    normal = normalize(float3(-slope.x, 1, -slope.y));
    return height;
}

#endif

using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace Valtiel.PlanetGenerator.Generation.Cpu
{
    // CPU equivalent of TerrestrialGen.compute. Mirrors the climate-driven
    // biome model: latitude-derived temperature + Hadley-pattern precipitation
    // blended with moisture fBm, bilinear lookup in a 3×3 Whittaker grid,
    // smoothstep ocean depth, T-based snow on land (Kilimanjaro effect),
    // optional polar override.
    //
    // Outputs: Colors (Color32 face-major) + Heights (float, in [-1,1] range
    // before quantization, used by the shared normal pass).
    [BurstCompile(FloatPrecision = FloatPrecision.Standard, FloatMode = FloatMode.Fast)]
    public struct CpuTerrestrialJob : IJobParallelFor
    {
        public int Size;
        public uint Seed;

        // Continent / elevation
        public float ContinentFrequency;
        public int   ContinentOctaves;
        public float ContinentLacunarity;
        public float ContinentGain;
        public float WarpStrength;
        public float SeaLevel;
        public float ElevationAmplitude;
        public float MountainStrength;
        public float MountainStart;
        public float MountainFull;
        public float BiomeContrast;

        // Moisture
        public float MoistureFrequency;
        public int   MoistureOctaves;

        // Climate model
        public float AltitudeCooling;
        public float TempNoiseFreq;
        public float TempNoiseStrength;
        public float HadleyStrength;
        public float SnowTempThreshold;
        public float SnowTempBlend;

        // Polar override
        public int   PolesEnabled;
        public float PoleLatitude;
        public float PoleBlendWidth;
        public float PoleNoiseStrength;

        // Whittaker 3×3 grid: index = 3 * tempBand + precipBand
        //   0 cold-dry → tundra      1 cold-mid → tundra      2 cold-wet → taiga
        //   3 mild-dry → desert      4 mild-mid → grass       5 mild-wet → forest
        //   6 hot-dry  → desert      7 hot-mid  → savanna     8 hot-wet  → jungle
        [ReadOnly] public NativeArray<float4> BiomeGrid;

        public float4 OceanDeepColor;
        public float4 OceanShallowColor;
        public float4 BeachColor;
        public float4 MountainColor;
        public float4 SnowColor;
        public float4 PolarColor;

        [WriteOnly] public NativeArray<Color32> Colors;
        [WriteOnly] public NativeArray<float>   Heights; // [-1, 1] before quantization

        // Hadley-cell precipitation: peaks at equator and ~60°, troughs at ~30°
        // and the poles. Envelope dampens the polar peak.
        static float HadleyPrecipitation(float lat01)
        {
            float wave = 0.5f + 0.5f * math.cos(3.0f * lat01 * math.PI);
            float envelope = 1.0f - 0.55f * lat01;
            return math.saturate(wave * envelope + 0.05f);
        }

        float3 SampleWhittaker(float T, float P)
        {
            float tIdx = math.saturate(T) * 2.0f;
            float pIdx = math.saturate(P) * 2.0f;
            int t0 = math.clamp((int)math.floor(tIdx), 0, 1);
            int p0 = math.clamp((int)math.floor(pIdx), 0, 1);
            float tf = math.saturate(tIdx - t0);
            float pf = math.saturate(pIdx - p0);

            float3 c00 = BiomeGrid[3 * t0       + p0    ].xyz;
            float3 c01 = BiomeGrid[3 * t0       + p0 + 1].xyz;
            float3 c10 = BiomeGrid[3 * (t0 + 1) + p0    ].xyz;
            float3 c11 = BiomeGrid[3 * (t0 + 1) + p0 + 1].xyz;
            return math.lerp(math.lerp(c00, c01, pf), math.lerp(c10, c11, pf), tf);
        }

        public void Execute(int index)
        {
            int faceSize = Size * Size;
            uint face = (uint)(index / faceSize);
            int local = index % faceSize;
            int x = local % Size;
            int y = local / Size;

            float3 dir = CpuNoise.CubemapTexelToDir(new float2(x, y), face, (uint)Size);

            // --- Elevation -------------------------------------------------
            float3 wp = dir * ContinentFrequency;
            if (WarpStrength > 0.0f)
                wp = CpuNoise.DomainWarp3D(wp, Seed ^ 0xA511E9B3u, WarpStrength);

            float rawE = CpuNoise.Fbm3D(wp, Seed, ContinentOctaves, ContinentLacunarity, ContinentGain);
            float baseE = math.tanh(rawE * BiomeContrast) * ElevationAmplitude;

            float ridge = CpuNoise.RidgeFbm3D(wp * 1.8f, Seed ^ 0x51BADC0Du,
                                              ContinentOctaves, 2.0f, 0.5f);
            float aboveSea = math.max(0.0f, baseE - SeaLevel);
            float mountainMask = math.smoothstep(MountainStart, MountainFull, aboveSea);
            float elevation = baseE + ridge * MountainStrength * mountainMask;

            float peakE = ElevationAmplitude + MountainStrength;
            float minE  = -ElevationAmplitude;

            // --- Climate fields --------------------------------------------
            float lat01 = math.saturate(math.abs(dir.y));

            float landElev = math.max(0.0f, elevation - SeaLevel);
            float T_lat = 1.0f - lat01;
            float T_alt = T_lat - landElev * AltitudeCooling;
            float tempN = CpuNoise.PerlinNoise3D(dir * TempNoiseFreq, Seed ^ 0x73A41C0Du)
                          * TempNoiseStrength;
            float T = math.saturate(T_alt + tempN);

            float rawM = CpuNoise.Fbm3D(dir * MoistureFrequency, Seed ^ 0x7A5C91EFu,
                                        MoistureOctaves, 2.0f, 0.5f);
            float moisture = math.saturate(math.tanh(rawM * BiomeContrast) * 0.5f + 0.5f);

            float P_hadley = HadleyPrecipitation(lat01);
            float P = math.saturate(math.lerp(moisture, P_hadley, math.saturate(HadleyStrength)));

            // --- Colour decision -------------------------------------------
            float3 color;
            if (elevation < SeaLevel)
            {
                float depthRaw = math.saturate((SeaLevel - elevation) / math.max(SeaLevel - minE, 1e-4f));
                float depthT   = math.smoothstep(0.10f, 0.55f, depthRaw);
                color = math.lerp(OceanShallowColor.xyz, OceanDeepColor.xyz, depthT);
            }
            else
            {
                float3 biome = SampleWhittaker(T, P);

                float land = math.saturate((elevation - SeaLevel) / math.max(peakE - SeaLevel, 1e-4f));
                float beachT = math.smoothstep(0.04f, 0.0f, land);
                color = math.lerp(biome, BeachColor.xyz, beachT);

                color = math.lerp(color, MountainColor.xyz, math.smoothstep(0.38f, 0.52f, land));

                float snowMask = math.smoothstep(SnowTempThreshold + SnowTempBlend,
                                                 SnowTempThreshold - SnowTempBlend, T);
                color = math.lerp(color, SnowColor.xyz, snowMask);
            }

            if (PolesEnabled != 0)
            {
                float pn = CpuNoise.PerlinNoise3D(dir * 6.0f, Seed ^ 0xBEE11CA7u) * PoleNoiseStrength;
                float poleT = math.smoothstep(PoleLatitude - PoleBlendWidth + pn,
                                              PoleLatitude + pn, lat01);
                color = math.lerp(color, PolarColor.xyz, poleT);
            }

            // --- Output ----------------------------------------------------
            byte br = (byte)math.clamp((int)(math.saturate(color.x) * 255f + 0.5f), 0, 255);
            byte bg = (byte)math.clamp((int)(math.saturate(color.y) * 255f + 0.5f), 0, 255);
            byte bb = (byte)math.clamp((int)(math.saturate(color.z) * 255f + 0.5f), 0, 255);
            Colors[index] = new Color32(br, bg, bb, 255);

            // Store the raw [-amp..+amp+ridge] elevation; the normal job
            // reads it directly so the centre-difference uses real heights.
            // Range normalization happens at the normal-pass call site
            // (passes HeightScale which already encodes the desired
            // visual relief magnitude).
            float fullSpan = math.max(peakE - minE, 1e-4f);
            Heights[index] = math.saturate((elevation - minE) / fullSpan) * 2.0f - 1.0f;
        }
    }
}

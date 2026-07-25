using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace Valtiel.PlanetGenerator.Generation.Cpu
{
    // Same memory layout as the HLSL `Storm` struct in GasGiantGen.compute
    // (three float4s back-to-back = 48 bytes). Used by both the CPU job and
    // — historically — the GPU StructuredBuffer; sharing the layout keeps
    // the two paths visually identical.
    [StructLayout(LayoutKind.Sequential)]
    public struct CpuStorm
    {
        public float4 Center;  // xyz unit-sphere centre, w = angular major radius
        public float4 Minor;   // x minor radius, y swirl (rotation amount), z intensity, w pad
        public float4 Tint;
    }

    // CPU equivalent of GasGiantGen.compute. Bands by anisotropic fBm with
    // y-axis stretch, zonal shear + curl via flow fBm, palette lookup with
    // band-noise-driven latitude shift, detail turbulence, storm ovals
    // stamped with Rodrigues-rotated swirl noise, pole darkening.
    [BurstCompile(FloatPrecision = FloatPrecision.Standard, FloatMode = FloatMode.Fast)]
    public struct CpuGasGiantJob : IJobParallelFor
    {
        public int Size;
        public uint Seed;

        // Bands
        public float BandStretch;
        public float BandFrequency;
        public int   BandOctaves;
        public float BandLacunarity;
        public float BandGain;
        public float BandContrast;
        public float BandRepetition;
        public float BandLatShift;
        public float BandWarp;

        // Flow
        public float FlowStrength;
        public float FlowFrequency;
        public int   FlowOctaves;
        public float CurlStrength;

        // Detail
        public float DetailFrequency;
        public int   DetailOctaves;
        public float DetailContrast;

        // Storms (passed in via the storms buffer below)
        public float StormNoiseFrequency;

        public float PoleLatitude;
        public float PoleDarken;

        // 6-colour palette laid out south→north.
        [ReadOnly] public NativeArray<float4> Palette; // length 6
        [ReadOnly] public NativeArray<CpuStorm> Storms;

        [WriteOnly] public NativeArray<Color32> Colors;
        [WriteOnly] public NativeArray<float>   Heights;

        static float3 EastTangent(float3 d, out float fadeFromPole)
        {
            float3 raw = math.cross(new float3(0, 1, 0), d);
            float len = math.length(raw);
            fadeFromPole = math.smoothstep(0.0f, 0.15f, len);
            return len > 1e-5f ? raw / len : new float3(1, 0, 0);
        }

        float3 SamplePalette(float latMinus1To1, float repetition)
        {
            float phase = math.saturate(latMinus1To1 * 0.5f + 0.5f) * repetition * 6.0f;
            phase = phase - math.floor(phase / 6.0f) * 6.0f;  // fmod(phase, 6)
            int idx = math.clamp((int)math.floor(phase), 0, 5);
            int nxt = (idx + 1) % 6;
            float f = phase - idx;
            float w = math.smoothstep(0.0f, 1.0f, f);
            return math.lerp(Palette[idx].xyz, Palette[nxt].xyz, w);
        }

        static float3 RotateAroundUnitAxis(float3 v, float3 k, float angle)
        {
            float c = math.cos(angle);
            float s = math.sin(angle);
            return v * c + math.cross(k, v) * s + k * math.dot(k, v) * (1.0f - c);
        }

        public void Execute(int index)
        {
            int faceSize = Size * Size;
            uint face = (uint)(index / faceSize);
            int local = index % faceSize;
            int x = local % Size;
            int y = local / Size;

            float3 dir = CpuNoise.CubemapTexelToDir(new float2(x, y), face, (uint)Size);

            float3 stretched = new float3(dir.x, dir.y * BandStretch, dir.z);

            float flowE = CpuNoise.Fbm3D(stretched * FlowFrequency, Seed ^ 0xF10A5A00u,
                                          FlowOctaves, 2.0f, 0.5f);
            float flowN = CpuNoise.Fbm3D(stretched * FlowFrequency * 1.3f + new float3(11.2f, 3.7f, 8.1f),
                                          Seed ^ 0xC012ADDAu,
                                          FlowOctaves, 2.0f, 0.5f);

            float3 east = EastTangent(dir, out float poleFade);
            float3 north = math.cross(dir, east);

            float3 warpDisp = east  * flowE * FlowStrength * poleFade
                            + north * flowN * CurlStrength * poleFade;
            float3 warpedDir = math.normalize(dir + warpDisp);
            float3 warpedStretched = new float3(warpedDir.x, warpedDir.y * BandStretch, warpedDir.z);

            float3 bandSamplePos = warpedStretched;
            if (BandWarp > 0.0f)
                bandSamplePos = CpuNoise.DomainWarp3D(bandSamplePos, Seed ^ 0xBAD4BA4Du, BandWarp);

            float bandNoise = CpuNoise.Fbm3D(bandSamplePos * BandFrequency, Seed,
                                              BandOctaves, BandLacunarity, BandGain);

            float shiftedLat = warpedDir.y + bandNoise * BandLatShift;
            float3 paletteCol = SamplePalette(shiftedLat, BandRepetition);

            float detail = CpuNoise.Fbm3D(bandSamplePos * DetailFrequency, Seed ^ 0xD37A11Du,
                                           DetailOctaves, 2.0f, 0.5f);

            float bandMul   = 1.0f + bandNoise * BandContrast;
            float detailMul = 1.0f + detail    * DetailContrast * 0.5f;
            float3 col = paletteCol * bandMul * detailMul;

            // --- Storms -----------------------------------------------------
            float3 stormAccum = new float3(0f);
            float  stormWeight = 0.0f;
            int stormCount = Storms.Length;
            for (int i = 0; i < stormCount; i++)
            {
                CpuStorm s = Storms[i];
                float3 sc = s.Center.xyz;
                float majorR = s.Center.w;
                float minorR = s.Minor.x;
                float swirl  = s.Minor.y;
                float intensity = s.Minor.z;
                if (majorR <= 1e-4f || minorR <= 1e-4f) continue;

                float cosA = math.clamp(math.dot(dir, sc), -1.0f, 1.0f);
                float angle = math.acos(cosA);
                if (angle > majorR * 1.25f) continue;

                float3 sEast = EastTangent(sc, out _);
                float3 sNorth = math.cross(sc, sEast);

                float3 delta = dir - sc * math.dot(dir, sc);
                float eProj = math.dot(delta, sEast);
                float nProj = math.dot(delta, sNorth);

                float t2 = (eProj * eProj) / (majorR * majorR)
                         + (nProj * nProj) / (minorR * minorR);
                float t = math.sqrt(t2);
                if (t > 1.15f) continue;

                uint stormSeed = CpuNoise.AsUint(sc.x)
                                ^ CpuNoise.AsUint(sc.y * 1.7f)
                                ^ CpuNoise.AsUint(sc.z * 2.3f)
                                ^ 0x51057E05u;

                float rotAngle = swirl * 6.28318530f * math.saturate(1.0f - t);
                float3 swirledDir = RotateAroundUnitAxis(dir, sc, rotAngle);
                float spiralNoise = CpuNoise.Fbm3D(swirledDir * StormNoiseFrequency,
                                                    stormSeed, 4, 2.0f, 0.5f);

                float edgeJitter = CpuNoise.PerlinNoise3D(dir * 22.0f + sc * 7.0f,
                                                           stormSeed ^ 0xEDu);
                float tRagged = t - edgeJitter * 0.08f;
                float mask = 1.0f - math.smoothstep(0.55f, 1.05f, tRagged);
                if (mask <= 0.0f) continue;

                float3 stormCol = s.Tint.xyz * (1.0f + spiralNoise * 0.45f);

                float w = mask * intensity;
                stormAccum += stormCol * w;
                stormWeight += w;
            }
            if (stormWeight > 1e-5f)
            {
                float3 stormCol = stormAccum / stormWeight;
                col = math.lerp(col, stormCol, math.saturate(stormWeight));
            }

            float absLat = math.abs(dir.y);
            float darkT = math.smoothstep(PoleLatitude, 1.0f, absLat) * PoleDarken;
            col *= 1.0f - darkT;

            byte br = (byte)math.clamp((int)(math.saturate(col.x) * 255f + 0.5f), 0, 255);
            byte bg = (byte)math.clamp((int)(math.saturate(col.y) * 255f + 0.5f), 0, 255);
            byte bb = (byte)math.clamp((int)(math.saturate(col.z) * 255f + 0.5f), 0, 255);
            Colors[index] = new Color32(br, bg, bb, 255);

            // Subtle relief: store the detail noise as the height signal.
            float height01 = math.saturate(detail * 0.5f + 0.5f);
            Heights[index] = height01 * 2.0f - 1.0f;
        }
    }
}

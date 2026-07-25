using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace Valtiel.PlanetGenerator.Generation.Cpu
{
    [StructLayout(LayoutKind.Sequential)]
    public struct CpuSunSpot
    {
        public float4 CenterRadius; // xyz centre, w major radius
        public float4 Params;       // x minor, y strength, z softness, w pad
    }

    // CPU equivalent of StarGen.compute. Writes two arrays in one pass:
    // surface colour (palette lookup driven by temperature) and emission
    // (greyscale intensity for bloom). Sunspots darken local regions and
    // drop emission via the spot mask.
    [BurstCompile(FloatPrecision = FloatPrecision.Standard, FloatMode = FloatMode.Fast)]
    public struct CpuStarJob : IJobParallelFor
    {
        public int Size;
        public uint Seed;
        public float Warp;

        public float MacroFrequency;
        public int   MacroOctaves;
        public float MacroLacunarity;
        public float MacroGain;

        public float GranuleFrequency;
        public int   GranuleOctaves;
        public float GranuleContrast;

        public float EmissionFloor;
        public float EmissionScale;
        public float SpotEmissionDarken;

        [ReadOnly] public NativeArray<float4>     Palette; // length 5, cold→hot
        [ReadOnly] public NativeArray<CpuSunSpot> Spots;

        [WriteOnly] public NativeArray<Color32> Colors;
        [WriteOnly] public NativeArray<Color32> Emission;

        float3 SampleStarPalette(float t01)
        {
            float t = math.saturate(t01) * 4.0f;
            int i = math.clamp((int)math.floor(t), 0, 3);
            int j = i + 1;
            float f = t - i;
            float w = math.smoothstep(0.0f, 1.0f, f);
            return math.lerp(Palette[i].xyz, Palette[j].xyz, w);
        }

        public void Execute(int index)
        {
            int faceSize = Size * Size;
            uint face = (uint)(index / faceSize);
            int local = index % faceSize;
            int x = local % Size;
            int y = local / Size;

            float3 dir = CpuNoise.CubemapTexelToDir(new float2(x, y), face, (uint)Size);

            float3 p = dir;
            if (Warp > 0.0f)
                p = CpuNoise.DomainWarp3D(p, Seed ^ 0xA511E9B3u, Warp);

            float macro = CpuNoise.Fbm3D(p * MacroFrequency, Seed,
                                          MacroOctaves, MacroLacunarity, MacroGain);
            float temp = math.saturate(macro * 0.5f + 0.5f);

            float gran = CpuNoise.Fbm3D(dir * GranuleFrequency, Seed ^ 0x0061A75Bu,
                                         GranuleOctaves, 2.0f, 0.5f);

            // --- Sunspots ---------------------------------------------------
            float spotMask = 0.0f;
            int spotCount = Spots.Length;
            for (int i = 0; i < spotCount; i++)
            {
                CpuSunSpot s = Spots[i];
                float3 sc = s.CenterRadius.xyz;
                float majorR = s.CenterRadius.w;
                float minorR = s.Params.x;
                float strength = s.Params.y;
                float softness = math.max(s.Params.z, 0.05f);
                if (majorR <= 1e-4f || minorR <= 1e-4f) continue;

                float cosA = math.clamp(math.dot(dir, sc), -1.0f, 1.0f);
                float angle = math.acos(cosA);
                if (angle > majorR * 1.2f) continue;

                float3 rawEast = math.cross(new float3(0, 1, 0), sc);
                float eLen = math.length(rawEast);
                float3 sEast = eLen > 1e-5f ? rawEast / eLen : new float3(1, 0, 0);
                float3 sNorth = math.cross(sc, sEast);

                float3 delta = dir - sc * math.dot(dir, sc);
                float eProj = math.dot(delta, sEast);
                float nProj = math.dot(delta, sNorth);

                float t2 = (eProj * eProj) / (majorR * majorR)
                         + (nProj * nProj) / (minorR * minorR);
                float t = math.sqrt(t2);
                if (t > 1.05f) continue;

                float m = (1.0f - math.smoothstep(1.0f - softness, 1.0f, t)) * strength;
                spotMask = math.max(spotMask, m);
            }

            float tempFinal = math.saturate(math.lerp(temp, 0.0f, spotMask));

            float3 col = SampleStarPalette(tempFinal);
            col *= 1.0f + gran * GranuleContrast;

            float emission = math.saturate(EmissionFloor
                                            + tempFinal * EmissionScale
                                            - spotMask * SpotEmissionDarken);

            byte br = (byte)math.clamp((int)(math.saturate(col.x) * 255f + 0.5f), 0, 255);
            byte bg = (byte)math.clamp((int)(math.saturate(col.y) * 255f + 0.5f), 0, 255);
            byte bb = (byte)math.clamp((int)(math.saturate(col.z) * 255f + 0.5f), 0, 255);
            Colors[index] = new Color32(br, bg, bb, 255);

            byte e = (byte)math.clamp((int)(emission * 255f + 0.5f), 0, 255);
            Emission[index] = new Color32(e, e, e, 255);
        }
    }
}

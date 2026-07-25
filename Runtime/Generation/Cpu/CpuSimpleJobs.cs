using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace Valtiel.PlanetGenerator.Generation.Cpu
{
    // Debug noise — fills every face with a greyscale fBm of 3D Perlin at the
    // sphere direction. CPU equivalent of PlanetGenDebug.compute. Output is
    // Color32 (grayscale × 3, alpha 1).
    [BurstCompile(FloatPrecision = FloatPrecision.Standard, FloatMode = FloatMode.Fast)]
    public struct CpuDebugNoiseJob : IJobParallelFor
    {
        public int Size;
        public uint Seed;
        public float Frequency;
        public int Octaves;
        public float Lacunarity;
        public float Gain;
        public float WarpStrength;

        [WriteOnly] public NativeArray<Color32> Colors; // 6 * Size * Size

        public void Execute(int index)
        {
            int faceSize = Size * Size;
            uint face = (uint)(index / faceSize);
            int local = index % faceSize;
            int x = local % Size;
            int y = local / Size;

            float3 dir = CpuNoise.CubemapTexelToDir(new float2(x, y), face, (uint)Size);

            float3 p = dir * Frequency;
            if (WarpStrength > 0.0f)
                p = CpuNoise.DomainWarp3D(p, Seed ^ 0x9E3779B9u, WarpStrength);

            float n = CpuNoise.Fbm3D(p, Seed, Octaves, Lacunarity, Gain);
            float g = math.saturate(n * 0.5f + 0.5f);

            byte b = (byte)math.clamp((int)(g * 255f + 0.5f), 0, 255);
            Colors[index] = new Color32(b, b, b, 255);
        }
    }

    // Cloud cubemap — CPU equivalent of CloudsGen.compute. Output: RGB =
    // cloudColor (constant), alpha = density (driven by warped fBm + coverage
    // threshold + detail layer).
    [BurstCompile(FloatPrecision = FloatPrecision.Standard, FloatMode = FloatMode.Fast)]
    public struct CpuCloudsJob : IJobParallelFor
    {
        public int Size;
        public uint Seed;
        public float CloudFrequency;
        public int CloudOctaves;
        public float CloudLacunarity;
        public float CloudGain;
        public float WarpStrength;
        public float Coverage;
        public float Softness;
        public float DetailStrength;
        public float4 CloudColor;

        [WriteOnly] public NativeArray<Color32> Colors;

        public void Execute(int index)
        {
            int faceSize = Size * Size;
            uint face = (uint)(index / faceSize);
            int local = index % faceSize;
            int x = local % Size;
            int y = local / Size;

            float3 dir = CpuNoise.CubemapTexelToDir(new float2(x, y), face, (uint)Size);

            float3 p = dir * CloudFrequency;
            if (WarpStrength > 0.0f)
                p = CpuNoise.DomainWarp3D(p, Seed ^ 0xC1011D5Au, WarpStrength);

            float baseN = CpuNoise.Fbm3D(p, Seed, CloudOctaves, CloudLacunarity, CloudGain);
            baseN = math.saturate(baseN * 0.5f + 0.5f);

            float detail = CpuNoise.Fbm3D(dir * CloudFrequency * 3.5f, Seed ^ 0x24AF8017u,
                                           math.max(2, CloudOctaves - 2), 2.0f, 0.5f);
            detail = math.saturate(detail * 0.5f + 0.5f);

            float n = math.saturate(baseN + (detail - 0.5f) * DetailStrength);

            float thresh = 1.0f - Coverage;
            float density = math.smoothstep(thresh - Softness, thresh + Softness, n);
            density *= math.saturate(baseN * 1.2f);

            float r = math.saturate(CloudColor.x);
            float g = math.saturate(CloudColor.y);
            float b = math.saturate(CloudColor.z);
            byte br = (byte)(r * 255f + 0.5f);
            byte bg = (byte)(g * 255f + 0.5f);
            byte bb = (byte)(b * 255f + 0.5f);
            byte ba = (byte)math.clamp((int)(math.saturate(density) * 255f + 0.5f), 0, 255);
            Colors[index] = new Color32(br, bg, bb, ba);
        }
    }

    // Normal-from-height — CPU equivalent of NormalFromHeight.compute. Reads
    // a per-face height NativeArray<float>, computes central-difference
    // tangent vectors via Displaced points, and writes object-space normals
    // encoded as (n * 0.5 + 0.5) in RGB. Edge texels clamp the ±1 neighbor
    // to the same face (the GPU path uses true cross-face sampling via the
    // cubemap sampler; clamping costs a negligible smoothing on the 4
    // outermost texel rows per face — visually imperceptible at typical
    // viewing distances).
    [BurstCompile(FloatPrecision = FloatPrecision.Standard, FloatMode = FloatMode.Fast)]
    public struct CpuNormalFromHeightJob : IJobParallelFor
    {
        public int Size;
        public float HeightScale;

        [ReadOnly]  public NativeArray<float>   Heights;   // [-1..1], face-major
        [WriteOnly] public NativeArray<Color32> Normals;

        public void Execute(int index)
        {
            int faceSize = Size * Size;
            uint face = (uint)(index / faceSize);
            int local = index % faceSize;
            int x = local % Size;
            int y = local / Size;

            int xRp = math.min(x + 1, Size - 1);
            int xRm = math.max(x - 1, 0);
            int yUp = math.min(y + 1, Size - 1);
            int yUm = math.max(y - 1, 0);

            int faceOffset = (int)face * faceSize;
            float hRp = Heights[faceOffset + y   * Size + xRp];
            float hRm = Heights[faceOffset + y   * Size + xRm];
            float hUp = Heights[faceOffset + yUp * Size + x  ];
            float hUm = Heights[faceOffset + yUm * Size + x  ];

            float3 dRp = CpuNoise.CubemapTexelToDir(new float2(xRp, y),   face, (uint)Size);
            float3 dRm = CpuNoise.CubemapTexelToDir(new float2(xRm, y),   face, (uint)Size);
            float3 dUp = CpuNoise.CubemapTexelToDir(new float2(x,   yUp), face, (uint)Size);
            float3 dUm = CpuNoise.CubemapTexelToDir(new float2(x,   yUm), face, (uint)Size);

            float3 pRp = dRp * (1.0f + hRp * HeightScale);
            float3 pRm = dRm * (1.0f + hRm * HeightScale);
            float3 pUp = dUp * (1.0f + hUp * HeightScale);
            float3 pUm = dUm * (1.0f + hUm * HeightScale);

            // Same cross order as the GPU version — cross(up, right) is the
            // outward-pointing direction under Unity's DirectX-style cubemap
            // tangent convention. Inverting kills lighting on every face.
            float3 n = math.normalize(math.cross(pUp - pUm, pRp - pRm));
            float3 enc = n * 0.5f + 0.5f;

            byte r = (byte)math.clamp((int)(enc.x * 255f + 0.5f), 0, 255);
            byte g = (byte)math.clamp((int)(enc.y * 255f + 0.5f), 0, 255);
            byte b = (byte)math.clamp((int)(enc.z * 255f + 0.5f), 0, 255);
            Normals[index] = new Color32(r, g, b, 255);
        }
    }
}

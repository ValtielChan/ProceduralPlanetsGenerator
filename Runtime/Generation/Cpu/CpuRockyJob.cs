using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace Valtiel.PlanetGenerator.Generation.Cpu
{
    // CPU equivalent of RockyGen.compute. Base fBm + optional mare + dust
    // colour noise + optional polar caps, with an optional tileable detail
    // texture sampled via triplanar projection (matches the GPU triplanar
    // blend math: pow(abs(dir), sharpness) weights × 3 planar samples).
    //
    // The detail texture is passed as a raw NativeArray<Color32> + width/height
    // (read on the C# side via Texture2D.GetPixelData<Color32>()). Bilinear
    // sampling is done in this job — no Texture sampling on the Burst path.
    [BurstCompile(FloatPrecision = FloatPrecision.Standard, FloatMode = FloatMode.Fast)]
    public struct CpuRockyJob : IJobParallelFor
    {
        public int Size;
        public uint Seed;

        public float BaseFrequency;
        public int   BaseOctaves;
        public float BaseLacunarity;
        public float BaseGain;
        public float BaseAmplitude;
        public float BaseWarp;

        public int   MareEnabled;
        public float MareFrequency;
        public float MareCoverage;
        public float MareSoftness;
        public float MareFlatten;
        public float MareDepth;

        public float DustFrequency;
        public float DustStrength;

        public int   PolesEnabled;
        public float PoleLatitude;
        public float PoleBlendWidth;
        public float PoleNoiseStrength;

        // Detail texture (triplanar). UseDetailMap=0 → skip sampling. The
        // pixel buffer is in Color32 RGBA (Unity layout), origin bottom-left.
        public int   UseDetailMap;
        public int   DetailWidth;
        public int   DetailHeight;
        public float DetailTiling;
        public float DetailOffsetU;
        public float DetailOffsetV;
        public float DetailStrength;
        public float DetailBlendSharpness;
        [ReadOnly] public NativeArray<Color32> DetailPixels;

        public float HeightRangeMin;
        public float HeightRangeMax;

        public float4 HighlandColor;
        public float4 MareColor;
        public float4 DustColor;
        public float4 PolarColor;

        [WriteOnly] public NativeArray<Color32> Colors;
        [WriteOnly] public NativeArray<float>   Heights;

        // Bilinear sample of the detail texture in tiling-space. uv may go
        // outside [0,1] — we wrap to honour Repeat semantics.
        float3 SampleDetailBilinear(float2 uv)
        {
            float fx = math.frac(uv.x) * (DetailWidth  - 1);
            float fy = math.frac(uv.y) * (DetailHeight - 1);
            int x0 = (int)math.floor(fx); int x1 = math.min(x0 + 1, DetailWidth  - 1);
            int y0 = (int)math.floor(fy); int y1 = math.min(y0 + 1, DetailHeight - 1);
            if (x0 < 0) x0 = 0;
            if (y0 < 0) y0 = 0;
            float wx = fx - x0;
            float wy = fy - y0;

            Color32 c00 = DetailPixels[y0 * DetailWidth + x0];
            Color32 c10 = DetailPixels[y0 * DetailWidth + x1];
            Color32 c01 = DetailPixels[y1 * DetailWidth + x0];
            Color32 c11 = DetailPixels[y1 * DetailWidth + x1];

            const float inv = 1.0f / 255.0f;
            float3 v00 = new float3(c00.r, c00.g, c00.b) * inv;
            float3 v10 = new float3(c10.r, c10.g, c10.b) * inv;
            float3 v01 = new float3(c01.r, c01.g, c01.b) * inv;
            float3 v11 = new float3(c11.r, c11.g, c11.b) * inv;

            float3 a = math.lerp(v00, v10, wx);
            float3 b = math.lerp(v01, v11, wx);
            return math.lerp(a, b, wy);
        }

        float3 SampleDetailTriplanar(float3 dir)
        {
            float2 off = new float2(DetailOffsetU, DetailOffsetV);
            float2 uvX = dir.yz * DetailTiling + off;
            float2 uvY = dir.xz * DetailTiling + off;
            float2 uvZ = dir.xy * DetailTiling + off;

            float3 cx = SampleDetailBilinear(uvX);
            float3 cy = SampleDetailBilinear(uvY);
            float3 cz = SampleDetailBilinear(uvZ);

            float3 w = math.pow(math.abs(dir), DetailBlendSharpness);
            w /= math.max(w.x + w.y + w.z, 1e-4f);
            return cx * w.x + cy * w.y + cz * w.z;
        }

        public void Execute(int index)
        {
            int faceSize = Size * Size;
            uint face = (uint)(index / faceSize);
            int local = index % faceSize;
            int x = local % Size;
            int y = local / Size;

            float3 dir = CpuNoise.CubemapTexelToDir(new float2(x, y), face, (uint)Size);

            float3 wp = dir * BaseFrequency;
            if (BaseWarp > 0.0f)
                wp = CpuNoise.DomainWarp3D(wp, Seed ^ 0xB0BAA55u, BaseWarp);
            float baseE = CpuNoise.Fbm3D(wp, Seed, BaseOctaves, BaseLacunarity, BaseGain) * BaseAmplitude;

            float mare = 0.0f;
            if (MareEnabled != 0)
            {
                float m = CpuNoise.Fbm3D(dir * MareFrequency, Seed ^ 0x1AF5E0Du, 4, 2.0f, 0.5f);
                m = math.saturate(m * 0.5f + 0.5f);
                mare = math.smoothstep(MareCoverage - MareSoftness,
                                       MareCoverage + MareSoftness, m);
            }

            float elevation = math.lerp(baseE,
                                        baseE * (1.0f - MareFlatten) - MareDepth,
                                        mare);

            float dust = CpuNoise.Fbm3D(dir * DustFrequency, Seed ^ 0xDEAD0005u, 3, 2.0f, 0.5f);
            dust = math.saturate(dust * 0.5f + 0.5f);

            float3 col = math.lerp(HighlandColor.xyz, MareColor.xyz, mare);
            col = math.lerp(col, DustColor.xyz, dust * DustStrength);

            if (UseDetailMap != 0 && DetailStrength > 0.0f && DetailPixels.Length > 0)
            {
                float3 detail = SampleDetailTriplanar(dir);
                float3 factor = math.lerp(new float3(1f, 1f, 1f), detail * 2.0f, DetailStrength);
                col *= factor;
            }

            if (PolesEnabled != 0)
            {
                float lat = math.abs(dir.y);
                float pn = CpuNoise.PerlinNoise3D(dir * 6.0f, Seed ^ 0xBEE11CA7u) * PoleNoiseStrength;
                float poleT = math.smoothstep(PoleLatitude - PoleBlendWidth + pn,
                                              PoleLatitude + pn, lat);
                col = math.lerp(col, PolarColor.xyz, poleT);
            }

            byte br = (byte)math.clamp((int)(math.saturate(col.x) * 255f + 0.5f), 0, 255);
            byte bg = (byte)math.clamp((int)(math.saturate(col.y) * 255f + 0.5f), 0, 255);
            byte bb = (byte)math.clamp((int)(math.saturate(col.z) * 255f + 0.5f), 0, 255);
            Colors[index] = new Color32(br, bg, bb, 255);

            float span = math.max(HeightRangeMax - HeightRangeMin, 1e-4f);
            float h01 = math.saturate((elevation - HeightRangeMin) / span);
            Heights[index] = h01 * 2.0f - 1.0f;
        }
    }
}

using Unity.Burst;
using Unity.Mathematics;

namespace Valtiel.PlanetGenerator.Generation.Cpu
{
    // C# port of PlanetNoise.hlsl, written to be Burst-compatible (no managed
    // types, no exceptions, all aggressive-inlinable). Output is bit-stable
    // with the HLSL version within float precision tolerances — both use
    // identical PCG hash constants, identical gradient encoding, and the
    // same multilinear-Perlin interpolation.
    //
    // All methods are `static` and free of side effects so jobs can share the
    // library without contention.
    [BurstCompile]
    public static class CpuNoise
    {
        // --- PCG hash ------------------------------------------------------
        public static uint PcgHash(uint x)
        {
            x = x * 747796405u + 2891336453u;
            uint w = ((x >> (int)((x >> 28) + 4u)) ^ x) * 277803737u;
            return (w >> 22) ^ w;
        }

        public static uint Hash3(int3 p, uint seed)
        {
            uint h = PcgHash((uint)p.x * 1597334677u ^ (uint)p.y * 3812015801u
                             ^ (uint)p.z * 2798796415u ^ seed);
            return PcgHash(h);
        }

        // Unit-sphere gradient from hash — same encoding as the HLSL version.
        public static float3 HashGradient(int3 p, uint seed)
        {
            uint h = Hash3(p, seed);
            float3 g = new float3(
                ((h)         & 0xFFFFu) / 32767.5f - 1.0f,
                ((h >> 16)   & 0xFFFFu) / 32767.5f - 1.0f,
                (PcgHash(h)  & 0xFFFFu) / 32767.5f - 1.0f);
            return math.normalize(g);
        }

        public static float Fade(float t) => t * t * t * (t * (t * 6.0f - 15.0f) + 10.0f);

        // Classic 3D Perlin noise. Output in roughly [-1, 1].
        public static float PerlinNoise3D(float3 p, uint seed)
        {
            int3 pi = (int3)math.floor(p);
            float3 pf = math.frac(p);
            float3 f = new float3(Fade(pf.x), Fade(pf.y), Fade(pf.z));

            float3 g000 = HashGradient(pi + new int3(0,0,0), seed);
            float3 g100 = HashGradient(pi + new int3(1,0,0), seed);
            float3 g010 = HashGradient(pi + new int3(0,1,0), seed);
            float3 g110 = HashGradient(pi + new int3(1,1,0), seed);
            float3 g001 = HashGradient(pi + new int3(0,0,1), seed);
            float3 g101 = HashGradient(pi + new int3(1,0,1), seed);
            float3 g011 = HashGradient(pi + new int3(0,1,1), seed);
            float3 g111 = HashGradient(pi + new int3(1,1,1), seed);

            float n000 = math.dot(g000, pf - new float3(0,0,0));
            float n100 = math.dot(g100, pf - new float3(1,0,0));
            float n010 = math.dot(g010, pf - new float3(0,1,0));
            float n110 = math.dot(g110, pf - new float3(1,1,0));
            float n001 = math.dot(g001, pf - new float3(0,0,1));
            float n101 = math.dot(g101, pf - new float3(1,0,1));
            float n011 = math.dot(g011, pf - new float3(0,1,1));
            float n111 = math.dot(g111, pf - new float3(1,1,1));

            float nx00 = math.lerp(n000, n100, f.x);
            float nx10 = math.lerp(n010, n110, f.x);
            float nx01 = math.lerp(n001, n101, f.x);
            float nx11 = math.lerp(n011, n111, f.x);

            float nxy0 = math.lerp(nx00, nx10, f.y);
            float nxy1 = math.lerp(nx01, nx11, f.y);

            return math.lerp(nxy0, nxy1, f.z);
        }

        // fBm — output in roughly [-1, 1] after amp-sum normalization.
        public static float Fbm3D(float3 p, uint seed, int octaves, float lacunarity, float gain)
        {
            float sum = 0.0f, amp = 1.0f, ampSum = 0.0f, freq = 1.0f;
            uint os = seed;
            for (int i = 0; i < octaves; i++)
            {
                sum += amp * PerlinNoise3D(p * freq, os);
                ampSum += amp;
                amp *= gain;
                freq *= lacunarity;
                os = PcgHash(os);
            }
            return sum / math.max(ampSum, 1e-6f);
        }

        // Ridge noise — inverted abs(fBm), squared, accumulated. Output ≥ 0.
        public static float RidgeFbm3D(float3 p, uint seed, int octaves, float lacunarity, float gain)
        {
            float sum = 0.0f, amp = 0.5f, freq = 1.0f;
            uint os = seed;
            for (int i = 0; i < octaves; i++)
            {
                float n = 1.0f - math.abs(PerlinNoise3D(p * freq, os));
                n *= n;
                sum += amp * n;
                amp *= gain;
                freq *= lacunarity;
                os = PcgHash(os);
            }
            return sum;
        }

        // Domain warp — shift the sample by a low-frequency vector noise so
        // grid-aligned Perlin artifacts vanish.
        public static float3 DomainWarp3D(float3 p, uint seed, float strength)
        {
            float3 w = new float3(
                PerlinNoise3D(p + new float3(1.7f, 9.2f, 3.1f), seed),
                PerlinNoise3D(p + new float3(8.3f, 2.8f, 5.4f), PcgHash(seed)),
                PerlinNoise3D(p + new float3(4.4f, 6.1f, 7.7f), PcgHash(PcgHash(seed))));
            return p + w * strength;
        }

        // --- Cubemap helpers ----------------------------------------------

        // Face order matches Unity's CubemapFace enum (0=+X, 1=-X, 2=+Y, 3=-Y,
        // 4=+Z, 5=-Z) and HLSL's CubemapTexelToDir from PlanetNoise.hlsl.
        // Accepts fractional texel coords for sub-texel sampling.
        public static float3 CubemapTexelToDir(float2 texel, uint face, uint size)
        {
            float2 uv = (texel + 0.5f) / (float)size;
            float u = uv.x * 2.0f - 1.0f;
            float v = uv.y * 2.0f - 1.0f;

            float3 dir;
            if      (face == 0u) dir = new float3( 1.0f, -v, -u);
            else if (face == 1u) dir = new float3(-1.0f, -v,  u);
            else if (face == 2u) dir = new float3( u,    1.0f, v);
            else if (face == 3u) dir = new float3( u,   -1.0f,-v);
            else if (face == 4u) dir = new float3( u,   -v,  1.0f);
            else                 dir = new float3(-u,   -v, -1.0f);
            return math.normalize(dir);
        }

        // Reinterpret the bit pattern of `f` as a uint — equivalent to HLSL
        // `asuint(f)`. Used to seed per-storm / per-spot RNGs from positions.
        public static uint AsUint(float f) => math.asuint(f);
    }
}

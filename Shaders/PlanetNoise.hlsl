#ifndef GALAXY_PLANET_NOISE_INCLUDED
#define GALAXY_PLANET_NOISE_INCLUDED

// 3D gradient (Perlin-style) noise + fBm.
// Seamless on a sphere when sampled at a unit direction: the input space is
// continuous across cube faces because there's no 2D UV wrap at all.

// --- pcg hashes -------------------------------------------------------------

uint PcgHash(uint x)
{
    x = x * 747796405u + 2891336453u;
    uint w = ((x >> ((x >> 28u) + 4u)) ^ x) * 277803737u;
    return (w >> 22u) ^ w;
}

uint Hash3(int3 p, uint seed)
{
    uint h = PcgHash((uint)p.x * 1597334677u ^ (uint)p.y * 3812015801u ^ (uint)p.z * 2798796415u ^ seed);
    return PcgHash(h);
}

// Unit-sphere gradient from hash.
float3 HashGradient(int3 p, uint seed)
{
    uint h = Hash3(p, seed);
    // Map to [-1, 1]^3 then normalize. Cheap, good enough for perlin-style noise.
    float3 g = float3(
        (float)((h      ) & 0xFFFFu) / 32767.5 - 1.0,
        (float)((h >> 16) & 0xFFFFu) / 32767.5 - 1.0,
        (float)((PcgHash(h)) & 0xFFFFu) / 32767.5 - 1.0);
    return normalize(g);
}

float Fade(float t) { return t * t * t * (t * (t * 6.0 - 15.0) + 10.0); }

// Classic 3D Perlin noise, output in roughly [-1, 1].
float PerlinNoise3D(float3 p, uint seed)
{
    int3 pi = int3(floor(p));
    float3 pf = frac(p);

    float3 f = float3(Fade(pf.x), Fade(pf.y), Fade(pf.z));

    float3 g000 = HashGradient(pi + int3(0,0,0), seed);
    float3 g100 = HashGradient(pi + int3(1,0,0), seed);
    float3 g010 = HashGradient(pi + int3(0,1,0), seed);
    float3 g110 = HashGradient(pi + int3(1,1,0), seed);
    float3 g001 = HashGradient(pi + int3(0,0,1), seed);
    float3 g101 = HashGradient(pi + int3(1,0,1), seed);
    float3 g011 = HashGradient(pi + int3(0,1,1), seed);
    float3 g111 = HashGradient(pi + int3(1,1,1), seed);

    float n000 = dot(g000, pf - float3(0,0,0));
    float n100 = dot(g100, pf - float3(1,0,0));
    float n010 = dot(g010, pf - float3(0,1,0));
    float n110 = dot(g110, pf - float3(1,1,0));
    float n001 = dot(g001, pf - float3(0,0,1));
    float n101 = dot(g101, pf - float3(1,0,1));
    float n011 = dot(g011, pf - float3(0,1,1));
    float n111 = dot(g111, pf - float3(1,1,1));

    float nx00 = lerp(n000, n100, f.x);
    float nx10 = lerp(n010, n110, f.x);
    float nx01 = lerp(n001, n101, f.x);
    float nx11 = lerp(n011, n111, f.x);

    float nxy0 = lerp(nx00, nx10, f.y);
    float nxy1 = lerp(nx01, nx11, f.y);

    return lerp(nxy0, nxy1, f.z);
}

// Fractional Brownian Motion. Output in roughly [-1, 1] after normalization.
float Fbm3D(float3 p, uint seed, int octaves, float lacunarity, float gain)
{
    float sum = 0.0;
    float amp = 1.0;
    float ampSum = 0.0;
    float freq = 1.0;
    uint os = seed;
    for (int i = 0; i < octaves; i++)
    {
        sum += amp * PerlinNoise3D(p * freq, os);
        ampSum += amp;
        amp *= gain;
        freq *= lacunarity;
        os = PcgHash(os);
    }
    return sum / max(ampSum, 1e-6);
}

// Ridge noise variant — inverts absolute fBm to give crisp ridges (good for
// mountains / continental outlines).
float RidgeFbm3D(float3 p, uint seed, int octaves, float lacunarity, float gain)
{
    float sum = 0.0;
    float amp = 0.5;
    float freq = 1.0;
    uint os = seed;
    for (int i = 0; i < octaves; i++)
    {
        float n = 1.0 - abs(PerlinNoise3D(p * freq, os));
        n *= n;
        sum += amp * n;
        amp *= gain;
        freq *= lacunarity;
        os = PcgHash(os);
    }
    return sum;
}

// Domain warp: shift the sample position by a low-frequency noise vector.
// Breaks up the grid-aligned look of raw Perlin.
float3 DomainWarp3D(float3 p, uint seed, float strength)
{
    float3 w;
    w.x = PerlinNoise3D(p + float3( 1.7, 9.2, 3.1), seed);
    w.y = PerlinNoise3D(p + float3( 8.3, 2.8, 5.4), PcgHash(seed));
    w.z = PerlinNoise3D(p + float3( 4.4, 6.1, 7.7), PcgHash(PcgHash(seed)));
    return p + w * strength;
}

// --- cubemap helpers --------------------------------------------------------

// Reconstruct the direction from a (texel, face) tuple. Face order matches
// Unity's CubemapFace enum: 0=+X, 1=-X, 2=+Y, 3=-Y, 4=+Z, 5=-Z.
// Accepts fractional texel coords so callers can offset sub-texel when
// computing finite-difference derivatives.
float3 CubemapTexelToDir(float2 texel, uint face, uint size)
{
    float2 uv = (texel + 0.5) / float(size);
    float u = uv.x * 2.0 - 1.0;
    float v = uv.y * 2.0 - 1.0;

    // DX/Unity cubemap convention (Y-down in image space).
    float3 dir;
    if      (face == 0u) dir = float3( 1.0, -v, -u);
    else if (face == 1u) dir = float3(-1.0, -v,  u);
    else if (face == 2u) dir = float3( u,    1.0, v);
    else if (face == 3u) dir = float3( u,   -1.0,-v);
    else if (face == 4u) dir = float3( u,   -v,  1.0);
    else                 dir = float3(-u,   -v, -1.0);
    return normalize(dir);
}

#endif

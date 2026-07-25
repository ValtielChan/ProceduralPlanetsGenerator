using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace Valtiel.PlanetGenerator.Generation.Cpu
{
    // Orchestrator for the CPU generation path. Allocates NativeArrays,
    // schedules the appropriate Burst job, waits, then uploads the result
    // to a Cubemap that the caller binds to the material.
    //
    // All cubemaps are created as R8G8B8A8_UNorm (linear) to match the live
    // GPU preview's behaviour — the RenderTexture used by the compute path
    // is also created with RenderTextureReadWrite.Linear, so the two
    // backends produce visually identical output.
    //
    // The caller owns the returned Cubemaps and must Destroy() them when
    // done (or call the dispose helper below).
    public static class CpuBakery
    {
        // ---------- Cubemap helpers ----------
        public static Cubemap NewCubemap(int size, bool mipChain = true)
        {
            return new Cubemap(size,
                GraphicsFormat.R8G8B8A8_UNorm,
                mipChain ? TextureCreationFlags.MipChain : TextureCreationFlags.None);
        }

        static void UploadFaces(Cubemap cube, NativeArray<Color32> faceMajor, int size)
        {
            int faceSize = size * size;
            for (int f = 0; f < 6; f++)
                cube.SetPixelData(faceMajor, 0, (CubemapFace)f, f * faceSize);
            cube.Apply(updateMipmaps: true, makeNoLongerReadable: false);
        }

        // Convert NativeArray<float> heights ([-1..1]) into the alpha channel
        // of a Color32 buffer when needed. Not used by the live demo (heights
        // feed directly into the normal job), but exposed for completeness.
        public static void HeightsToColorAlpha(NativeArray<float> heights, NativeArray<Color32> colors)
        {
            for (int i = 0; i < heights.Length; i++)
            {
                var c = colors[i];
                float h01 = math.saturate(heights[i] * 0.5f + 0.5f);
                c.a = (byte)math.clamp((int)(h01 * 255f + 0.5f), 0, 255);
                colors[i] = c;
            }
        }

        // ---------- Debug ----------
        public static Cubemap BakeDebug(int size, int seed, float frequency,
            int octaves, float lacunarity, float gain, float warpStrength)
        {
            int total = 6 * size * size;
            var colors = new NativeArray<Color32>(total, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            try
            {
                var job = new CpuDebugNoiseJob
                {
                    Size = size, Seed = (uint)seed,
                    Frequency = frequency, Octaves = octaves,
                    Lacunarity = lacunarity, Gain = gain,
                    WarpStrength = warpStrength,
                    Colors = colors,
                };
                job.Schedule(total, 1024).Complete();

                var cube = NewCubemap(size);
                UploadFaces(cube, colors, size);
                return cube;
            }
            finally { colors.Dispose(); }
        }

        // ---------- Terrestrial ----------
        public struct TerrestrialOutputs
        {
            public Cubemap Color;
            public Cubemap Normal;  // null if no relief baked
            public Cubemap Clouds;  // null if clouds disabled
        }

        public static TerrestrialOutputs BakeTerrestrial(TerrestrialPlanetConfig c, int size, int seed)
        {
            int total = 6 * size * size;
            var colors  = new NativeArray<Color32>(total, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            var heights = new NativeArray<float>  (total, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            var grid    = new NativeArray<float4> (9,     Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            FillTerrestrialGrid(c, grid);

            try
            {
                var job = new CpuTerrestrialJob
                {
                    Size = size, Seed = (uint)seed,
                    ContinentFrequency = c.continentFrequency,
                    ContinentOctaves   = c.continentOctaves,
                    ContinentLacunarity = c.continentLacunarity,
                    ContinentGain      = c.continentGain,
                    WarpStrength       = c.warpStrength,
                    SeaLevel           = c.seaLevel,
                    ElevationAmplitude = c.elevationAmplitude,
                    MountainStrength   = c.mountainStrength,
                    MountainStart      = c.mountainStart,
                    MountainFull       = c.mountainFull,
                    BiomeContrast      = math.max(0.1f, c.biomeContrast),
                    MoistureFrequency  = c.moistureFrequency,
                    MoistureOctaves    = c.moistureOctaves,
                    AltitudeCooling    = c.altitudeCooling,
                    TempNoiseFreq      = c.tempNoiseFreq,
                    TempNoiseStrength  = c.tempNoiseStrength,
                    HadleyStrength     = c.hadleyStrength,
                    SnowTempThreshold  = c.snowTempThreshold,
                    SnowTempBlend      = math.max(0.001f, c.snowTempBlend),
                    PolesEnabled       = c.polesEnabled ? 1 : 0,
                    PoleLatitude       = c.poleLatitude,
                    PoleBlendWidth     = c.poleBlendWidth,
                    PoleNoiseStrength  = c.poleNoiseStrength,
                    BiomeGrid          = grid,
                    OceanDeepColor     = (Vector4)c.oceanDeep,
                    OceanShallowColor  = (Vector4)c.oceanShallow,
                    BeachColor         = (Vector4)c.beach,
                    MountainColor      = (Vector4)c.mountain,
                    SnowColor          = (Vector4)c.snow,
                    PolarColor         = (Vector4)c.polar,
                    Colors             = colors,
                    Heights            = heights,
                };
                job.Schedule(total, 1024).Complete();

                var outputs = new TerrestrialOutputs
                {
                    Color = NewCubemap(size),
                };
                UploadFaces(outputs.Color, colors, size);

                outputs.Normal = BakeNormalFromHeights(heights, size, c.heightScale);

                if (c.cloudsEnabled)
                    outputs.Clouds = BakeClouds(c, size, seed);

                return outputs;
            }
            finally
            {
                colors.Dispose();
                heights.Dispose();
                grid.Dispose();
            }
        }

        static void FillTerrestrialGrid(TerrestrialPlanetConfig c, NativeArray<float4> grid)
        {
            grid[0] = (Vector4)c.tundra;  grid[1] = (Vector4)c.tundra;  grid[2] = (Vector4)c.taiga;
            grid[3] = (Vector4)c.desert;  grid[4] = (Vector4)c.grass;   grid[5] = (Vector4)c.forest;
            grid[6] = (Vector4)c.desert;  grid[7] = (Vector4)c.savanna; grid[8] = (Vector4)c.jungle;
        }

        // ---------- Rocky ----------
        public struct RockyOutputs { public Cubemap Color; public Cubemap Normal; }

        public static RockyOutputs BakeRocky(RockyPlanetConfig c, int size, int seed, Texture2D detailMap)
        {
            int total = 6 * size * size;
            var colors  = new NativeArray<Color32>(total, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            var heights = new NativeArray<float>  (total, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);

            // Snapshot the detail texture into a NativeArray so the job has a
            // Burst-safe read. We copy via GetPixels32 to avoid requiring
            // the source texture to be Read/Write enabled and to dodge any
            // partial-readback edge cases.
            NativeArray<Color32> detailPixels = default;
            int dw = 1, dh = 1;
            bool detailActive = c.detailEnabled && detailMap != null;
            if (detailActive)
            {
                try
                {
                    Color32[] managed = detailMap.GetPixels32();
                    detailPixels = new NativeArray<Color32>(managed.Length, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
                    detailPixels.CopyFrom(managed);
                    dw = detailMap.width;
                    dh = detailMap.height;
                }
                catch (UnityException)
                {
                    // Texture not readable → fall back to no-detail rather
                    // than crashing the bakery. User can re-enable Read/Write
                    // in the importer to fix.
                    detailActive = false;
                    Debug.LogWarning("[CpuBakery] Rocky detail texture is not Read/Write enabled — " +
                                     "skipping detail layer. Enable Read/Write on the texture importer to use it.");
                }
            }
            if (!detailActive)
                detailPixels = new NativeArray<Color32>(1, Allocator.TempJob);

            try
            {
                float maxAmp = c.baseAmplitude;
                float hMin = -maxAmp - (c.mareEnabled ? c.mareDepth : 0f) - 0.05f;
                float hMax =  maxAmp + 0.05f;

                var job = new CpuRockyJob
                {
                    Size = size, Seed = (uint)seed,
                    BaseFrequency = c.baseFrequency,
                    BaseOctaves   = c.baseOctaves,
                    BaseLacunarity = c.baseLacunarity,
                    BaseGain      = c.baseGain,
                    BaseAmplitude = c.baseAmplitude,
                    BaseWarp      = c.baseWarp,

                    MareEnabled   = c.mareEnabled ? 1 : 0,
                    MareFrequency = c.mareFrequency,
                    MareCoverage  = c.mareCoverage,
                    MareSoftness  = c.mareSoftness,
                    MareFlatten   = c.mareFlatten,
                    MareDepth     = c.mareDepth,

                    DustFrequency = c.dustFrequency,
                    DustStrength  = c.dustStrength,

                    PolesEnabled      = c.polesEnabled ? 1 : 0,
                    PoleLatitude      = c.poleLatitude,
                    PoleBlendWidth    = c.poleBlendWidth,
                    PoleNoiseStrength = c.poleNoiseStrength,

                    UseDetailMap     = detailActive ? 1 : 0,
                    DetailWidth      = dw,
                    DetailHeight     = dh,
                    DetailTiling     = c.detailTiling,
                    DetailOffsetU    = c.detailOffsetU,
                    DetailOffsetV    = c.detailOffsetV,
                    DetailStrength   = c.detailStrength,
                    DetailBlendSharpness = math.max(1f, c.detailBlendSharpness),
                    DetailPixels     = detailPixels,

                    HeightRangeMin = hMin,
                    HeightRangeMax = hMax,
                    HighlandColor  = (Vector4)c.highlandColor,
                    MareColor      = (Vector4)c.mareColor,
                    DustColor      = (Vector4)c.dustColor,
                    PolarColor     = (Vector4)c.polarColor,

                    Colors  = colors,
                    Heights = heights,
                };
                job.Schedule(total, 1024).Complete();

                var outputs = new RockyOutputs { Color = NewCubemap(size) };
                UploadFaces(outputs.Color, colors, size);
                outputs.Normal = BakeNormalFromHeights(heights, size, c.heightScale);
                return outputs;
            }
            finally
            {
                colors.Dispose();
                heights.Dispose();
                detailPixels.Dispose();
            }
        }

        // ---------- Gas Giant ----------
        public struct GasOutputs { public Cubemap Color; public Cubemap Normal; }

        public static GasOutputs BakeGasGiant(GasPlanetConfig c, int size, int seed)
        {
            int total = 6 * size * size;
            var colors  = new NativeArray<Color32>(total, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            var heights = new NativeArray<float>  (total, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            var palette = new NativeArray<float4> (6,     Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            palette[0] = (Vector4)c.palette0; palette[1] = (Vector4)c.palette1; palette[2] = (Vector4)c.palette2;
            palette[3] = (Vector4)c.palette3; palette[4] = (Vector4)c.palette4; palette[5] = (Vector4)c.palette5;

            var storms = BuildStorms(c, seed);

            try
            {
                var job = new CpuGasGiantJob
                {
                    Size = size, Seed = (uint)seed,
                    BandStretch = c.bandStretch, BandFrequency = c.bandFrequency,
                    BandOctaves = c.bandOctaves, BandLacunarity = c.bandLacunarity,
                    BandGain = c.bandGain, BandContrast = c.bandContrast,
                    BandRepetition = c.bandRepetition, BandLatShift = c.bandLatShift,
                    BandWarp = c.bandWarp,
                    FlowStrength = c.flowStrength, FlowFrequency = c.flowFrequency,
                    FlowOctaves = c.flowOctaves, CurlStrength = c.curlStrength,
                    DetailFrequency = c.detailFrequency,
                    DetailOctaves   = c.detailOctaves,
                    DetailContrast  = c.detailContrast,
                    StormNoiseFrequency = c.stormNoiseFrequency,
                    PoleLatitude = c.poleLatitude, PoleDarken = c.poleDarken,
                    Palette = palette, Storms = storms,
                    Colors = colors, Heights = heights,
                };
                job.Schedule(total, 1024).Complete();

                var outputs = new GasOutputs { Color = NewCubemap(size) };
                UploadFaces(outputs.Color, colors, size);
                if (c.heightScale > 0f)
                    outputs.Normal = BakeNormalFromHeights(heights, size, c.heightScale);
                return outputs;
            }
            finally
            {
                colors.Dispose();
                heights.Dispose();
                palette.Dispose();
                storms.Dispose();
            }
        }

        static NativeArray<CpuStorm> BuildStorms(GasPlanetConfig c, int seed)
        {
            var list = new List<CpuStorm>(c.bigStormCount + c.smallStormCount);
            var rng = new System.Random(seed ^ unchecked((int)0x5701A000));
            AppendStormTier(list, rng, c.bigStormCount,
                c.bigStormRadius, 0.35f, c.bigStormIntensity, c.bigStormSwirl, c.bigStormTint);
            AppendStormTier(list, rng, c.smallStormCount,
                c.smallStormRadius, 0.25f, c.smallStormIntensity, c.smallStormSwirl, c.smallStormTint);

            int n = math.max(list.Count, 1);
            var arr = new NativeArray<CpuStorm>(n, Allocator.TempJob, NativeArrayOptions.ClearMemory);
            for (int i = 0; i < list.Count; i++) arr[i] = list[i];
            return arr;
        }

        static void AppendStormTier(List<CpuStorm> list, System.Random rng,
            int count, float radiusMean, float radiusJitter,
            float intensity, float swirl, Color tint)
        {
            for (int i = 0; i < count; i++)
            {
                float lat = (float)(rng.NextDouble() * 1.3 - 0.65);
                float lon = (float)(rng.NextDouble() * 2.0 * math.PI);
                float cosLat = math.cos(lat * math.PI * 0.5f);
                Vector3 center = new Vector3(
                    cosLat * math.cos(lon),
                    math.sin(lat * math.PI * 0.5f),
                    cosLat * math.sin(lon)).normalized;
                float jitter = 1f + ((float)rng.NextDouble() * 2f - 1f) * radiusJitter;
                float majorR = math.max(0.005f, radiusMean * jitter);
                float aspect = 2.5f + (float)rng.NextDouble() * 1.5f;
                float minorR = majorR / aspect;
                float swirlV = swirl * (0.7f + (float)rng.NextDouble() * 0.6f);
                list.Add(new CpuStorm
                {
                    Center = new float4(center.x, center.y, center.z, majorR),
                    Minor  = new float4(minorR, swirlV, intensity, 0f),
                    Tint   = new float4(tint.r, tint.g, tint.b, 1f),
                });
            }
        }

        // ---------- Star ----------
        public struct StarOutputs { public Cubemap Color; public Cubemap Emission; }

        public static StarOutputs BakeStar(StarConfig c, int size, int seed)
        {
            int total = 6 * size * size;
            var colors   = new NativeArray<Color32>(total, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            var emission = new NativeArray<Color32>(total, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            var palette  = new NativeArray<float4> (5,     Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            palette[0] = (Vector4)c.palette0; palette[1] = (Vector4)c.palette1; palette[2] = (Vector4)c.palette2;
            palette[3] = (Vector4)c.palette3; palette[4] = (Vector4)c.palette4;
            var spots = BuildSpots(c, seed);

            try
            {
                var job = new CpuStarJob
                {
                    Size = size, Seed = (uint)seed,
                    Warp = c.warp,
                    MacroFrequency = c.macroFrequency,
                    MacroOctaves   = c.macroOctaves,
                    MacroLacunarity = c.macroLacunarity,
                    MacroGain      = c.macroGain,
                    GranuleFrequency = c.granuleFrequency,
                    GranuleOctaves   = c.granuleOctaves,
                    GranuleContrast  = c.granuleContrast,
                    EmissionFloor      = c.emissionFloor,
                    EmissionScale      = c.emissionScale,
                    SpotEmissionDarken = c.spotEmissionDarken,
                    Palette = palette,
                    Spots   = spots,
                    Colors  = colors,
                    Emission = emission,
                };
                job.Schedule(total, 1024).Complete();

                var outputs = new StarOutputs
                {
                    Color    = NewCubemap(size),
                    Emission = NewCubemap(size),
                };
                UploadFaces(outputs.Color,    colors,   size);
                UploadFaces(outputs.Emission, emission, size);
                return outputs;
            }
            finally
            {
                colors.Dispose();
                emission.Dispose();
                palette.Dispose();
                spots.Dispose();
            }
        }

        static NativeArray<CpuSunSpot> BuildSpots(StarConfig c, int seed)
        {
            var list = new List<CpuSunSpot>(c.spotCount);
            var rng = new System.Random(seed ^ unchecked((int)0x5A01E900));
            for (int i = 0; i < c.spotCount; i++)
            {
                Vector3 center = RandomUnitSphere(rng);
                float jitter = 0.6f + (float)rng.NextDouble() * 0.8f;
                float majorR = math.max(0.005f, c.spotRadius * jitter);
                float aspect = 1.0f + (float)rng.NextDouble() * 0.8f;
                float minorR = majorR / aspect;
                float softV = math.saturate(c.spotSoftness * (0.8f + (float)rng.NextDouble() * 0.4f));
                list.Add(new CpuSunSpot
                {
                    CenterRadius = new float4(center.x, center.y, center.z, majorR),
                    Params       = new float4(minorR, c.spotStrength, softV, 0f),
                });
            }
            int n = math.max(list.Count, 1);
            var arr = new NativeArray<CpuSunSpot>(n, Allocator.TempJob, NativeArrayOptions.ClearMemory);
            for (int i = 0; i < list.Count; i++) arr[i] = list[i];
            return arr;
        }

        static Vector3 RandomUnitSphere(System.Random rng)
        {
            while (true)
            {
                float u = (float)(rng.NextDouble() * 2.0 - 1.0);
                float v = (float)(rng.NextDouble() * 2.0 - 1.0);
                float s = u * u + v * v;
                if (s >= 1f || s <= 1e-6f) continue;
                float f = 2f * math.sqrt(1f - s);
                return new Vector3(u * f, v * f, 1f - 2f * s);
            }
        }

        // ---------- Clouds ----------
        public static Cubemap BakeClouds(TerrestrialPlanetConfig c, int size, int seed)
        {
            int total = 6 * size * size;
            var colors = new NativeArray<Color32>(total, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            try
            {
                var job = new CpuCloudsJob
                {
                    Size = size,
                    Seed = (uint)((uint)seed ^ 0x9E17Ca11u),
                    CloudFrequency  = c.cloudFrequency,
                    CloudOctaves    = c.cloudOctaves,
                    CloudLacunarity = c.cloudLacunarity,
                    CloudGain       = c.cloudGain,
                    WarpStrength    = c.cloudWarpStrength,
                    Coverage        = c.cloudCoverage,
                    Softness        = c.cloudSoftness,
                    DetailStrength  = c.cloudDetailStrength,
                    CloudColor      = (Vector4)c.cloudColor,
                    Colors          = colors,
                };
                job.Schedule(total, 1024).Complete();
                var cube = NewCubemap(size);
                UploadFaces(cube, colors, size);
                return cube;
            }
            finally { colors.Dispose(); }
        }

        // ---------- Normal-from-height ----------
        public static Cubemap BakeNormalFromHeights(NativeArray<float> heights, int size, float heightScale)
        {
            int total = 6 * size * size;
            var normals = new NativeArray<Color32>(total, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            try
            {
                var job = new CpuNormalFromHeightJob
                {
                    Size = size,
                    HeightScale = heightScale,
                    Heights = heights,
                    Normals = normals,
                };
                job.Schedule(total, 1024).Complete();
                var cube = NewCubemap(size);
                UploadFaces(cube, normals, size);
                return cube;
            }
            finally { normals.Dispose(); }
        }
    }
}

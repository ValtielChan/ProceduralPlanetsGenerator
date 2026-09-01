using System;
using UnityEngine;

namespace Valtiel.PlanetGenerator.Generation
{
    // Header used by the loader to discriminate between planet types before
    // parsing the full config. All concrete configs carry a `planetType` field
    // — we parse into this first and then into the matching concrete class.
    [Serializable]
    public class PlanetConfigHeader
    {
        public string planetType = "";
    }

    // Serializable snapshot of every knob that feeds the terrestrial + clouds
    // generators. Round-tripped through JsonUtility so a saved planet can be
    // re-loaded and regenerated bit-for-bit (seed is reproducible).
    //
    // Note: uint is unsupported by JsonUtility, so seed lives as int here and
    // is reinterpreted to uint at shader-dispatch time.
    [Serializable]
    public class TerrestrialPlanetConfig
    {
        public string planetType = "terrestrial";

        // Defaults are the saved "Earth" preset (Library/Earth/params.json)
        // so a fresh install / reset lands on an Earth-like planet.
        public string name = "Planet";
        public int   seed = 1;
        public int   cubemapSize = 1024;

        // Continents
        public float continentFrequency  = 0.93f;
        public int   continentOctaves    = 8;
        public float continentLacunarity = 2.18f;
        public float continentGain       = 0.608f;
        public float warpStrength        = 0.0f;
        public float seaLevel            = 0.195f;
        public float elevationAmplitude  = 1.11f;
        public float mountainStrength    = 0.55f;
        public float mountainStart       = 0.0f;
        public float mountainFull        = 0.68f;

        // Histogram shaping applied to both elevation and moisture fBm before
        // scaling. 1.0 = raw (cluster around 0, deep ocean / mountains / snow
        // rarely visible), 2.5 = balanced spread (default), 4+ = aggressive
        // bimodal distribution.
        public float biomeContrast       = 3.1f;

        // Moisture
        public float moistureFrequency = 6.37f;
        public int   moistureOctaves   = 8;

        // Poles (optional white-cap override on top of climate-driven biomes)
        public bool  polesEnabled      = true;
        public float poleLatitude      = 0.929f;
        public float poleBlendWidth    = 0.143f;
        public float poleNoiseStrength = 0.1112f;

        // Relief
        public float heightScale = 0.0086f;

        // Climate model (Hadley circulation + altitude lapse rate).
        public float altitudeCooling   = 0.263f;  // T loss per unit elevation above sea
        public float tempNoiseFreq     = 16.0f;   // small scale, breaks perfect latitude bands
        public float tempNoiseStrength = 0.2f;
        public float hadleyStrength    = 0.593f;  // 0 = pure moisture noise, 1 = pure latitude pattern
        public float snowTempThreshold = 0.2f;    // T below this turns land to snow
        public float snowTempBlend     = 0.1209f; // softness of the snow transition

        // Water-only specular (body material): sheen on ocean, land matte.
        public float waterSpecular      = 1f;      // 0 = off
        public float waterSpecularPower = 19f;     // Blinn-Phong exponent
        public float waterLevel         = 0.49f;   // packed-height cutoff for "is water"

        // Biome palette — Whittaker 3×3 grid (cold/mild/hot × dry/mid/wet)
        // shares 7 distinct colours plus oceans, beach, mountain, snow, polar.
        public Color oceanDeep    = new(0.00392f, 0.01569f, 0.07451f, 1f);
        public Color oceanShallow = new(0.07451f, 0.19608f, 0.39608f, 1f);
        public Color beach        = new(0.47170f, 0.38870f, 0.25587f, 1f);
        public Color tundra       = new(0.19216f, 0.20392f, 0.11373f, 1f);  // cold, dry/mid
        public Color taiga        = new(0.10196f, 0.14902f, 0.05490f, 1f);  // cold, wet — boreal forest
        public Color desert       = new(0.77255f, 0.68235f, 0.53725f, 1f);  // hot, dry
        public Color grass        = new(0.15686f, 0.23922f, 0.07843f, 1f);  // mild, mid
        public Color forest       = new(0.09020f, 0.14118f, 0.03922f, 1f);  // mild, wet — temperate
        public Color savanna      = new(0.52549f, 0.44706f, 0.30980f, 1f);  // hot, mid
        public Color jungle       = new(0.14510f, 0.20000f, 0.06275f, 1f);  // hot, wet — tropical rainforest
        public Color mountain     = new(0.54118f, 0.52157f, 0.46667f, 1f);
        public Color snow         = new(1.00f, 1.00f, 1.00f, 1f);
        public Color polar        = new(0.92f, 0.94f, 0.98f, 1f);

        // Clouds
        public bool  cloudsEnabled        = true;
        public float cloudAltitude        = 0.0128f;
        public float cloudFrequency       = 4.15f;
        public int   cloudOctaves         = 7;
        public float cloudLacunarity      = 3.4f;
        public float cloudGain            = 0.564f;
        public float cloudWarpStrength    = 2.04f;
        public float cloudCoverage        = 0.431f;
        public float cloudSoftness        = 0.113f;
        public float cloudDetailStrength  = 0.369f;
        public Color cloudColor           = Color.white;
        public float cloudDensity         = 1.25f;
        public float cloudShadowStrength  = 0.725f;
        public float cloudParallax        = 0.0405f;
        public float cloudAmbient         = 0.04f;
    }

    [Serializable]
    public class RockyPlanetConfig
    {
        public string planetType = "rocky";

        public string name = "Moon";
        public int    seed = 1;
        public int    cubemapSize = 512;

        public float baseFrequency  = 1.8f;
        public int   baseOctaves    = 5;
        public float baseLacunarity = 2.0f;
        public float baseGain       = 0.5f;
        public float baseAmplitude  = 0.18f;
        public float baseWarp       = 0.4f;

        public bool  mareEnabled   = true;
        public float mareFrequency = 1.1f;
        public float mareCoverage  = 0.58f;
        public float mareSoftness  = 0.08f;
        public float mareFlatten   = 0.6f;
        public float mareDepth     = 0.05f;

        public float dustFrequency = 3.5f;
        public float dustStrength  = 0.25f;

        public bool  polesEnabled      = false;
        public float poleLatitude      = 0.82f;
        public float poleBlendWidth    = 0.10f;
        public float poleNoiseStrength = 0.05f;

        // Detail texture: tileable surface photo (e.g. real asteroid) mapped
        // triplanar onto the sphere. JsonUtility can't serialize a Texture2D
        // ref so we store the asset path; the loader resolves it via
        // AssetDatabase. Empty = no detail map.
        public bool   detailEnabled         = false;
        public string detailMapAssetPath    = "";
        public float  detailTiling          = 4.0f;
        public float  detailOffsetU         = 0.0f;
        public float  detailOffsetV         = 0.0f;
        public float  detailStrength        = 0.6f;
        public float  detailBlendSharpness  = 4.0f;

        // Detail normal — sampled at runtime by the shader, tiling/offset/
        // sharpness shared with the color path above.
        public bool   detailNormalEnabled       = true;
        public string detailNormalMapAssetPath  = "";
        public float  detailNormalStrength      = 1.0f;

        public float heightScale = 0.02f;

        public Color highlandColor    = new(0.74f, 0.72f, 0.68f, 1f);
        public Color mareColor        = new(0.25f, 0.24f, 0.23f, 1f);
        public Color dustColor        = new(0.55f, 0.51f, 0.46f, 1f);
        public Color polarColor       = new(0.95f, 0.95f, 0.97f, 1f);
    }

    [Serializable]
    public class GasPlanetConfig
    {
        public string planetType = "gas";

        public string name = "Gas Giant";
        public int    seed = 1;
        public int    cubemapSize = 512;

        public float bandStretch    = 6.0f;
        public float bandFrequency  = 1.8f;
        public int   bandOctaves    = 5;
        public float bandLacunarity = 2.0f;
        public float bandGain       = 0.55f;
        public float bandContrast   = 0.25f;
        public float bandRepetition = 3.0f;
        public float bandLatShift   = 0.20f;
        public float bandWarp       = 0.5f;

        public float flowStrength  = 0.35f;
        public float flowFrequency = 0.8f;
        public int   flowOctaves   = 4;
        public float curlStrength  = 0.20f;

        public float detailFrequency = 6.0f;
        public int   detailOctaves   = 5;
        public float detailContrast  = 0.35f;

        public float stormNoiseFrequency = 10.0f;

        public float poleLatitude = 0.75f;
        public float poleDarken   = 0.25f;

        public Color palette0 = new(0.55f, 0.48f, 0.40f, 1f);
        public Color palette1 = new(0.76f, 0.56f, 0.42f, 1f);
        public Color palette2 = new(0.88f, 0.78f, 0.64f, 1f);
        public Color palette3 = new(0.78f, 0.55f, 0.38f, 1f);
        public Color palette4 = new(0.88f, 0.80f, 0.68f, 1f);
        public Color palette5 = new(0.58f, 0.50f, 0.42f, 1f);

        public int   bigStormCount     = 2;
        public float bigStormRadius    = 0.20f;
        public float bigStormIntensity = 0.80f;
        public float bigStormSwirl     = 0.55f;
        public Color bigStormTint      = new(0.78f, 0.32f, 0.22f, 1f);

        public int   smallStormCount     = 14;
        public float smallStormRadius    = 0.045f;
        public float smallStormIntensity = 0.60f;
        public float smallStormSwirl     = 0.25f;
        public Color smallStormTint      = new(0.95f, 0.90f, 0.78f, 1f);

        public float heightScale = 0.004f;
    }

    [Serializable]
    public class StarConfig
    {
        public string planetType = "star";

        public string name = "Sun";
        public int    seed = 1;
        public int    cubemapSize = 512;

        public float warp = 1.2f;

        public float macroFrequency  = 2.0f;
        public int   macroOctaves    = 5;
        public float macroLacunarity = 2.0f;
        public float macroGain       = 0.55f;

        public float granuleFrequency = 14.0f;
        public int   granuleOctaves   = 4;
        public float granuleContrast  = 0.25f;

        public float emissionFloor       = 0.5f;
        public float emissionScale       = 0.5f;
        public float spotEmissionDarken  = 0.6f;

        public int   spotCount      = 5;
        public float spotRadius     = 0.06f;
        public float spotStrength   = 0.85f;
        public float spotSoftness   = 0.35f;

        public Color palette0 = new(0.20f, 0.08f, 0.03f, 1f);
        public Color palette1 = new(0.70f, 0.25f, 0.08f, 1f);
        public Color palette2 = new(0.95f, 0.55f, 0.20f, 1f);
        public Color palette3 = new(1.00f, 0.82f, 0.45f, 1f);
        public Color palette4 = new(1.00f, 0.95f, 0.78f, 1f);

        public Color baseTint              = new(2.5f, 2.35f, 1.8f, 1f);
        public float materialEmissionFloor = 1.0f;
        public float materialEmissionBoost = 1.5f;
    }
}

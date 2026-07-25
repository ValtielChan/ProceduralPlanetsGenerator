using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Valtiel.PlanetGenerator.Generation;
using Valtiel.PlanetGenerator.Generation.Cpu;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Valtiel.PlanetGenerator.Editor
{
    public enum PlanetGenMode
    {
        DebugNoise,
        Terrestrial,
        Rocky,
        GasGiant,
        Star,
    }

    // Live preview: spawns a quad-sphere in the active scene and re-dispatches
    // the selected generator on every param change. The compute writes to a
    // Tex2DArray RT (one slice per face); we then copy each slice into a Cube
    // RT that the preview material samples by object-space normal.
    //
    // The preview material uses the asset's production shaders
    // (Valtiel/Planet/Surface, .../Clouds, .../Star), so the preview is
    // lit by the scene's directional light, additional lights, fog, and SH
    // probe — what you see is what you get when the prefab is dropped.
    public class PlanetGeneratorWindow : EditorWindow
    {
        const string PreviewObjectName     = "__PlanetGenerator_Preview";
        const string CloudPreviewObjectName = "__PlanetGenerator_Clouds";

        const string ShadersRoot           = "Assets/Procedural Planets Generator/Shaders";
        const string DebugComputePath      = ShadersRoot + "/PlanetGenDebug.compute";
        const string TerrestrialComputePath = ShadersRoot + "/TerrestrialGen.compute";
        const string RockyComputePath      = ShadersRoot + "/RockyGen.compute";
        const string GasGiantComputePath   = ShadersRoot + "/GasGiantGen.compute";
        const string StarComputePath       = ShadersRoot + "/StarGen.compute";
        const string NormalComputePath     = ShadersRoot + "/NormalFromHeight.compute";
        const string CloudsComputePath     = ShadersRoot + "/CloudsGen.compute";

        const string SurfaceShaderName = "Valtiel/Planet/Surface";
        const string CloudsShaderName  = "Valtiel/Planet/Clouds";
        const string StarShaderName    = "Valtiel/Planet/Star";

        [SerializeField] PlanetGenMode mode = PlanetGenMode.Terrestrial;
        [SerializeField] GenerationBackend backend = GenerationBackend.GPU;
        [SerializeField] int lodSubdivisions = 32;
        [SerializeField] int cubemapSize = 512;
        [SerializeField] int  seed = 1;

        // CPU-backend baked cubemaps (owned by the window, disposed on regen).
        Cubemap cpuColorCube, cpuNormalCube, cpuCloudCube, cpuEmissionCube;
        [SerializeField] string planetName = "Planet";
        [SerializeField] int exportResolution = 1024;
        [SerializeField] bool exportWithLods = true;

        // Debug-noise params
        [SerializeField] float dbgFrequency = 2.0f;
        [SerializeField] int   dbgOctaves = 5;
        [SerializeField] float dbgLacunarity = 2.0f;
        [SerializeField] float dbgGain = 0.5f;
        [SerializeField] float dbgWarpStrength = 0.0f;

        // Terrestrial params
        [SerializeField] float continentFrequency = 1.5f;
        [SerializeField] int   continentOctaves = 6;
        [SerializeField] float continentLacunarity = 2.0f;
        [SerializeField] float continentGain = 0.5f;
        [SerializeField] float warpStrength = 0.8f;
        [SerializeField] float seaLevel = -0.05f;
        [SerializeField] float elevationAmplitude = 1.5f;
        [SerializeField] float mountainStrength = 0.8f;
        [SerializeField] float mountainStart = 0.15f;
        [SerializeField] float mountainFull = 0.55f;
        [SerializeField] float biomeContrast = 2.5f;
        [SerializeField] float moistureFrequency = 2.5f;
        [SerializeField] int   moistureOctaves = 4;
        [SerializeField] bool  polesEnabled = true;
        [SerializeField] float poleLatitude = 0.78f;
        [SerializeField] float poleBlendWidth = 0.12f;
        [SerializeField] float poleNoiseStrength = 0.05f;
        [SerializeField] float heightScale = 0.025f;

        // Climate model
        [SerializeField] float altitudeCooling   = 0.55f;
        [SerializeField] float tempNoiseFreq     = 4.0f;
        [SerializeField] float tempNoiseStrength = 0.06f;
        [SerializeField] float hadleyStrength    = 0.7f;
        [SerializeField] float snowTempThreshold = 0.22f;
        [SerializeField] float snowTempBlend     = 0.06f;

        // Clouds
        [SerializeField] bool  cloudsEnabled = true;
        [SerializeField] float cloudAltitude = 0.012f;
        [SerializeField] float cloudFrequency = 2.2f;
        [SerializeField] int   cloudOctaves = 6;
        [SerializeField] float cloudLacunarity = 2.1f;
        [SerializeField] float cloudGain = 0.55f;
        [SerializeField] float cloudWarpStrength = 1.6f;
        [SerializeField] float cloudCoverage = 0.45f;
        [SerializeField] float cloudSoftness = 0.12f;
        [SerializeField] float cloudDetailStrength = 0.35f;
        [SerializeField] Color cloudColor = Color.white;
        [SerializeField] float cloudDensity = 1.0f;
        [SerializeField] float cloudShadowStrength = 0.55f;
        [SerializeField] float cloudParallax = 0.02f;
        [SerializeField] float cloudAmbient = 0.04f;

        // Rocky (airless) params — mirrors RockyPlanetConfig fields.
        [SerializeField] float rkBaseFrequency  = 1.8f;
        [SerializeField] int   rkBaseOctaves    = 5;
        [SerializeField] float rkBaseLacunarity = 2.0f;
        [SerializeField] float rkBaseGain       = 0.5f;
        [SerializeField] float rkBaseAmplitude  = 0.18f;
        [SerializeField] float rkBaseWarp       = 0.4f;

        [SerializeField] bool  rkMareEnabled   = true;
        [SerializeField] float rkMareFrequency = 1.1f;
        [SerializeField] float rkMareCoverage  = 0.58f;
        [SerializeField] float rkMareSoftness  = 0.08f;
        [SerializeField] float rkMareFlatten   = 0.6f;
        [SerializeField] float rkMareDepth     = 0.05f;

        [SerializeField] float rkDustFrequency = 3.5f;
        [SerializeField] float rkDustStrength  = 0.25f;

        [SerializeField] bool  rkPolesEnabled      = false;
        [SerializeField] float rkPoleLatitude      = 0.82f;
        [SerializeField] float rkPoleBlendWidth    = 0.10f;
        [SerializeField] float rkPoleNoiseStrength = 0.05f;

        // Detail texture: tileable surface photo (asteroid-like) mapped
        // triplanar onto the sphere. Replaces the old procedural crater
        // system — gives a much more organic, real-looking surface.
        // Default textures shipped with the asset. Editor auto-loads them when
        // the corresponding slot is null on OnEnable; the user is free to swap
        // in their own at any time (only re-resolved next session if cleared).
        const string DefaultRockyDetailMapPath       = "Assets/Procedural Planets Generator/Textures/RockyAlbedo.tif";
        const string DefaultRockyDetailNormalMapPath = "Assets/Procedural Planets Generator/Textures/RockyNormal.tif";

        [SerializeField] bool      rkDetailEnabled        = true;
        [SerializeField] Texture2D rkDetailMap;
        [SerializeField] float     rkDetailTiling         = 1.0f;
        [SerializeField] Vector2   rkDetailOffset         = Vector2.zero;
        [SerializeField] float     rkDetailStrength       = 0.6f;
        [SerializeField] float     rkDetailBlendSharpness = 4.0f;

        // Detail normal map — sampled at runtime by the shader (not baked into
        // the planet's normal cubemap) so tiling/strength can be tweaked live
        // on the material without re-export. Tiling/offset/sharpness are
        // shared with the color detail so the two stay aligned.
        [SerializeField] bool      rkDetailNormalEnabled  = true;
        [SerializeField] Texture2D rkDetailNormalMap;
        [SerializeField] float     rkDetailNormalStrength = 1.0f;

        [SerializeField] float rkHeightScale = 0.02f;

        [SerializeField] Color rkHighland    = new(0.74f, 0.72f, 0.68f);
        [SerializeField] Color rkMare        = new(0.25f, 0.24f, 0.23f);
        [SerializeField] Color rkDust        = new(0.55f, 0.51f, 0.46f);
        [SerializeField] Color rkPolar       = new(0.95f, 0.95f, 0.97f);

        // Gas giant params — mirrors GasPlanetConfig fields.
        [SerializeField] float ggBandStretch    = 6.0f;
        [SerializeField] float ggBandFrequency  = 1.8f;
        [SerializeField] int   ggBandOctaves    = 5;
        [SerializeField] float ggBandLacunarity = 2.0f;
        [SerializeField] float ggBandGain       = 0.55f;
        [SerializeField] float ggBandContrast   = 0.25f;
        [SerializeField] float ggBandRepetition = 3.0f;
        [SerializeField] float ggBandLatShift   = 0.20f;
        [SerializeField] float ggBandWarp       = 0.5f;

        [SerializeField] float ggFlowStrength  = 0.35f;
        [SerializeField] float ggFlowFrequency = 0.8f;
        [SerializeField] int   ggFlowOctaves   = 4;
        [SerializeField] float ggCurlStrength  = 0.20f;

        [SerializeField] float ggDetailFrequency = 6.0f;
        [SerializeField] int   ggDetailOctaves   = 5;
        [SerializeField] float ggDetailContrast  = 0.35f;

        [SerializeField] float ggStormNoiseFrequency = 10.0f;

        [SerializeField] float ggPoleLatitude = 0.75f;
        [SerializeField] float ggPoleDarken   = 0.25f;

        [SerializeField] Color ggPalette0 = new(0.55f, 0.48f, 0.40f);
        [SerializeField] Color ggPalette1 = new(0.76f, 0.56f, 0.42f);
        [SerializeField] Color ggPalette2 = new(0.88f, 0.78f, 0.64f);
        [SerializeField] Color ggPalette3 = new(0.78f, 0.55f, 0.38f);
        [SerializeField] Color ggPalette4 = new(0.88f, 0.80f, 0.68f);
        [SerializeField] Color ggPalette5 = new(0.58f, 0.50f, 0.42f);

        [SerializeField] int   ggBigStormCount     = 2;
        [SerializeField] float ggBigStormRadius    = 0.20f;
        [SerializeField] float ggBigStormIntensity = 0.80f;
        [SerializeField] float ggBigStormSwirl     = 0.55f;
        [SerializeField] Color ggBigStormTint      = new(0.78f, 0.32f, 0.22f);

        [SerializeField] int   ggSmallStormCount     = 14;
        [SerializeField] float ggSmallStormRadius    = 0.045f;
        [SerializeField] float ggSmallStormIntensity = 0.60f;
        [SerializeField] float ggSmallStormSwirl     = 0.25f;
        [SerializeField] Color ggSmallStormTint      = new(0.95f, 0.90f, 0.78f);

        [SerializeField] float ggHeightScale = 0.004f;

        // Star params — mirrors StarConfig fields.
        [SerializeField] float stWarp            = 1.2f;
        [SerializeField] float stMacroFrequency  = 2.0f;
        [SerializeField] int   stMacroOctaves    = 5;
        [SerializeField] float stMacroLacunarity = 2.0f;
        [SerializeField] float stMacroGain       = 0.55f;
        [SerializeField] float stGranuleFrequency = 14.0f;
        [SerializeField] int   stGranuleOctaves   = 4;
        [SerializeField] float stGranuleContrast  = 0.25f;
        [SerializeField] float stEmissionFloor       = 0.5f;
        [SerializeField] float stEmissionScale       = 0.5f;
        [SerializeField] float stSpotEmissionDarken  = 0.6f;
        [SerializeField] int   stSpotCount    = 5;
        [SerializeField] float stSpotRadius   = 0.06f;
        [SerializeField] float stSpotStrength = 0.85f;
        [SerializeField] float stSpotSoftness = 0.35f;
        [SerializeField] Color stPalette0 = new(0.20f, 0.08f, 0.03f);
        [SerializeField] Color stPalette1 = new(0.70f, 0.25f, 0.08f);
        [SerializeField] Color stPalette2 = new(0.95f, 0.55f, 0.20f);
        [SerializeField] Color stPalette3 = new(1.00f, 0.82f, 0.45f);
        [SerializeField] Color stPalette4 = new(1.00f, 0.95f, 0.78f);
        [SerializeField] Color stBaseTint             = new(2.5f, 2.35f, 1.8f);
        [SerializeField] float stMaterialEmissionFloor = 1.0f;
        [SerializeField] float stMaterialEmissionBoost = 1.5f;

        [SerializeField] Color oceanDeep    = new(0.01f, 0.06f, 0.18f);
        [SerializeField] Color oceanShallow = new(0.10f, 0.32f, 0.55f);
        [SerializeField] Color beach        = new(0.78f, 0.72f, 0.50f);
        [SerializeField] Color tundra       = new(0.55f, 0.55f, 0.45f); // cold-dry/mid
        [SerializeField] Color taiga        = new(0.18f, 0.30f, 0.20f); // cold-wet
        [SerializeField] Color desert       = new(0.85f, 0.72f, 0.42f); // hot-dry (also mild-dry)
        [SerializeField] Color grass        = new(0.40f, 0.60f, 0.25f); // mild-mid
        [SerializeField] Color forest       = new(0.12f, 0.32f, 0.12f); // mild-wet
        [SerializeField] Color savanna      = new(0.72f, 0.70f, 0.32f); // hot-mid
        [SerializeField] Color jungle       = new(0.06f, 0.30f, 0.08f); // hot-wet
        [SerializeField] Color mountain     = new(0.42f, 0.38f, 0.33f);
        [SerializeField] Color snow         = new(0.95f, 0.95f, 0.98f);
        [SerializeField] Color polar        = new(0.92f, 0.94f, 0.98f);

        bool colorsFoldout = true;

        ComputeShader debugCompute;
        ComputeShader terrestrialCompute;
        ComputeShader rockyCompute;
        ComputeShader gasGiantCompute;
        ComputeShader starCompute;
        ComputeShader normalCompute;
        ComputeShader cloudsCompute;
        ComputeBuffer stormBuffer;
        ComputeBuffer spotBuffer;
        RenderTexture emissionArrayRT;
        RenderTexture emissionCubeRT;
        bool rockyDetailFoldout = true;
        bool rockyPaletteFoldout = true;
        bool gasPaletteFoldout = true;
        bool gasStormsFoldout = true;
        bool starPaletteFoldout = true;
        bool starSpotsFoldout = true;
        string currentPreviewShader;
        RenderTexture arrayRT;
        RenderTexture cubeRT;
        RenderTexture normalArrayRT;
        RenderTexture normalCubeRT;
        RenderTexture cloudArrayRT;
        RenderTexture cloudCubeRT;
        GameObject cloudGO;
        Material cloudMaterial;
        Material previewMaterial;
        GameObject previewGO;
        Mesh previewMesh;
        int lastMeshSubdivisions = -1;
        Vector2 scroll;

        [MenuItem("Window/Planet Generator")]
        public static void Open()
        {
            var w = GetWindow<PlanetGeneratorWindow>("Planet Generator");
            w.minSize = new Vector2(340, 520);
        }

        void OnEnable()
        {
            debugCompute       = AssetDatabase.LoadAssetAtPath<ComputeShader>(DebugComputePath);
            terrestrialCompute = AssetDatabase.LoadAssetAtPath<ComputeShader>(TerrestrialComputePath);
            rockyCompute       = AssetDatabase.LoadAssetAtPath<ComputeShader>(RockyComputePath);
            gasGiantCompute    = AssetDatabase.LoadAssetAtPath<ComputeShader>(GasGiantComputePath);
            starCompute        = AssetDatabase.LoadAssetAtPath<ComputeShader>(StarComputePath);
            normalCompute      = AssetDatabase.LoadAssetAtPath<ComputeShader>(NormalComputePath);
            cloudsCompute      = AssetDatabase.LoadAssetAtPath<ComputeShader>(CloudsComputePath);

            // Auto-resolve the default rocky detail texture if the user hasn't
            // explicitly assigned one. Missing-file silent failure is fine —
            // the field stays null and the compute falls back to Texture2D.whiteTexture.
            if (rkDetailMap == null)
                rkDetailMap = AssetDatabase.LoadAssetAtPath<Texture2D>(DefaultRockyDetailMapPath);
            if (rkDetailNormalMap == null)
                rkDetailNormalMap = AssetDatabase.LoadAssetAtPath<Texture2D>(DefaultRockyDetailNormalMapPath);

            EnsurePreview();
            Regenerate();
        }

        void OnDisable()
        {
            if (arrayRT != null) { arrayRT.Release(); DestroyImmediate(arrayRT); arrayRT = null; }
            if (cubeRT != null)  { cubeRT.Release();  DestroyImmediate(cubeRT);  cubeRT = null; }
            if (normalArrayRT != null) { normalArrayRT.Release(); DestroyImmediate(normalArrayRT); normalArrayRT = null; }
            if (normalCubeRT != null)  { normalCubeRT.Release();  DestroyImmediate(normalCubeRT);  normalCubeRT = null; }
            if (cloudArrayRT != null)  { cloudArrayRT.Release();  DestroyImmediate(cloudArrayRT);  cloudArrayRT = null; }
            if (cloudCubeRT != null)   { cloudCubeRT.Release();   DestroyImmediate(cloudCubeRT);   cloudCubeRT = null; }
            if (cloudMaterial != null) { DestroyImmediate(cloudMaterial); cloudMaterial = null; }
            if (cloudGO != null)       { DestroyImmediate(cloudGO);       cloudGO = null; }
            if (previewMaterial != null) { DestroyImmediate(previewMaterial); previewMaterial = null; }
            if (previewGO != null)       { DestroyImmediate(previewGO);       previewGO = null; }
            if (previewMesh != null)     { DestroyImmediate(previewMesh);     previewMesh = null; }
            if (stormBuffer != null)     { stormBuffer.Release();             stormBuffer = null; }
            if (spotBuffer != null)      { spotBuffer.Release();              spotBuffer = null; }
            if (emissionArrayRT != null) { emissionArrayRT.Release();         DestroyImmediate(emissionArrayRT); emissionArrayRT = null; }
            if (emissionCubeRT != null)  { emissionCubeRT.Release();          DestroyImmediate(emissionCubeRT); emissionCubeRT = null; }
            DisposeCpuCubemaps();
        }

        void DisposeCpuCubemaps()
        {
            if (cpuColorCube    != null) { DestroyImmediate(cpuColorCube);    cpuColorCube    = null; }
            if (cpuNormalCube   != null) { DestroyImmediate(cpuNormalCube);   cpuNormalCube   = null; }
            if (cpuCloudCube    != null) { DestroyImmediate(cpuCloudCube);    cpuCloudCube    = null; }
            if (cpuEmissionCube != null) { DestroyImmediate(cpuEmissionCube); cpuEmissionCube = null; }
        }

        void OnGUI()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);
            EditorGUI.BeginChangeCheck();

            GUILayout.Label("Mode", EditorStyles.boldLabel);
            mode = (PlanetGenMode)EditorGUILayout.EnumPopup("Generator", mode);
            backend = (GenerationBackend)EditorGUILayout.EnumPopup(new GUIContent("Backend",
                "GPU = compute shaders (instant, needs DX11+/Vulkan/Metal/WebGPU). " +
                "CPU = Burst Jobs (slower per regen, works on every platform incl. WebGL 2.0)."),
                backend);

            GUILayout.Space(6);
            GUILayout.Label("Mesh / Texture", EditorStyles.boldLabel);
            lodSubdivisions = EditorGUILayout.IntSlider("Subdivisions", lodSubdivisions, 4, 128);
            cubemapSize = EditorGUILayout.IntPopup("Cubemap Size", cubemapSize,
                new[] { "256", "512", "1024", "2048" },
                new[] { 256, 512, 1024, 2048 });

            GUILayout.Space(6);
            GUILayout.Label("Identity", EditorStyles.boldLabel);
            planetName = EditorGUILayout.TextField("Name", planetName);
            seed = EditorGUILayout.IntField("Seed", seed);

            GUILayout.Space(6);
            if (mode == PlanetGenMode.DebugNoise) DrawDebugUI();
            else if (mode == PlanetGenMode.Terrestrial) DrawTerrestrialUI();
            else if (mode == PlanetGenMode.Rocky) DrawRockyUI();
            else if (mode == PlanetGenMode.GasGiant) DrawGasGiantUI();
            else if (mode == PlanetGenMode.Star) DrawStarUI();

            EditorGUILayout.HelpBox(
                "Preview is lit by the active scene's directional light and ambient.\n" +
                "Add a Directional Light to your scene for the best preview.",
                MessageType.None);

            bool changed = EditorGUI.EndChangeCheck();

            GUILayout.Space(10);
            if (GUILayout.Button("Randomize Seed"))
            {
                seed = Random.Range(1, int.MaxValue);
                changed = true;
            }
            if (GUILayout.Button("Recenter Preview"))
            {
                EnsurePreview();
                if (previewGO != null && SceneView.lastActiveSceneView != null)
                    SceneView.lastActiveSceneView.LookAt(previewGO.transform.position,
                        Quaternion.LookRotation(Vector3.forward), 3f);
            }

            GUILayout.Space(10);
            GUILayout.Label("Library", EditorStyles.boldLabel);
            exportResolution = EditorGUILayout.IntPopup("Export Resolution",
                exportResolution,
                new[] { "256", "512", "1024", "2048", "4096" },
                new[] { 256, 512, 1024, 2048, 4096 });
            exportWithLods = EditorGUILayout.Toggle("Include LODs", exportWithLods);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Save Planet")) SavePlanet();
                if (GUILayout.Button("Load Planet")) LoadPlanet();
            }

            EditorGUILayout.EndScrollView();

            if (changed) Regenerate();
        }

        void DrawDebugUI()
        {
            GUILayout.Label("Debug Noise", EditorStyles.boldLabel);
            dbgFrequency    = EditorGUILayout.Slider("Frequency", dbgFrequency, 0.1f, 16f);
            dbgOctaves      = EditorGUILayout.IntSlider("Octaves", dbgOctaves, 1, 10);
            dbgLacunarity   = EditorGUILayout.Slider("Lacunarity", dbgLacunarity, 1.5f, 4f);
            dbgGain         = EditorGUILayout.Slider("Gain", dbgGain, 0.2f, 0.8f);
            dbgWarpStrength = EditorGUILayout.Slider("Warp Strength", dbgWarpStrength, 0f, 2f);
        }

        void DrawTerrestrialUI()
        {
            GUILayout.Label("Continents", EditorStyles.boldLabel);
            continentFrequency  = EditorGUILayout.Slider("Frequency", continentFrequency, 0.3f, 8f);
            continentOctaves    = EditorGUILayout.IntSlider("Octaves", continentOctaves, 1, 10);
            continentLacunarity = EditorGUILayout.Slider("Lacunarity", continentLacunarity, 1.5f, 4f);
            continentGain       = EditorGUILayout.Slider("Gain", continentGain, 0.2f, 0.8f);
            warpStrength        = EditorGUILayout.Slider("Warp Strength", warpStrength, 0f, 2f);
            seaLevel            = EditorGUILayout.Slider("Sea Level", seaLevel, -0.8f, 0.8f);
            elevationAmplitude  = EditorGUILayout.Slider("Elevation Amplitude", elevationAmplitude, 0.3f, 3.0f);
            mountainStrength    = EditorGUILayout.Slider("Mountain Strength", mountainStrength, 0f, 2.5f);
            mountainStart       = EditorGUILayout.Slider("Mountain Start", mountainStart, 0.0f, 1.5f);
            mountainFull        = EditorGUILayout.Slider("Mountain Full", mountainFull, mountainStart + 0.05f, 2.5f);

            GUILayout.Space(2);
            biomeContrast       = EditorGUILayout.Slider(
                new GUIContent("Biome Contrast",
                    "Pushes elevation+moisture distributions toward extremes. " +
                    "Low (~1) = mostly mid-tones, no deep ocean / no mountains / no forest. " +
                    "Default (~2.5) = full biome panel visible. High (4+) = strong bimodal split."),
                biomeContrast, 0.5f, 6f);

            GUILayout.Space(4);
            GUILayout.Label("Moisture", EditorStyles.boldLabel);
            moistureFrequency = EditorGUILayout.Slider("Frequency", moistureFrequency, 0.3f, 8f);
            moistureOctaves   = EditorGUILayout.IntSlider("Octaves", moistureOctaves, 1, 8);

            GUILayout.Space(4);
            GUILayout.Label("Climate", EditorStyles.boldLabel);
            hadleyStrength    = EditorGUILayout.Slider(new GUIContent("Hadley Strength",
                "0 = precipitation from noise only (chaotic biomes). " +
                "1 = pure latitude-driven (jungle band at equator, deserts at ±30°, " +
                "temperate forests at ±60°). Default 0.7 mixes both."),
                hadleyStrength, 0f, 1f);
            altitudeCooling   = EditorGUILayout.Slider(new GUIContent("Altitude Cooling",
                "Temperature loss per unit of elevation above sea. Higher = " +
                "easier snow on mountains (Kilimanjaro effect)."),
                altitudeCooling, 0f, 1.5f);
            snowTempThreshold = EditorGUILayout.Slider(new GUIContent("Snow Temperature",
                "Land colder than this (normalized 0=pole, 1=equator) turns to snow."),
                snowTempThreshold, 0f, 0.6f);
            snowTempBlend     = EditorGUILayout.Slider("Snow Edge Softness", snowTempBlend, 0.01f, 0.2f);
            tempNoiseStrength = EditorGUILayout.Slider("Temp Noise Amount", tempNoiseStrength, 0f, 0.2f);
            tempNoiseFreq     = EditorGUILayout.Slider("Temp Noise Freq",   tempNoiseFreq,    0.5f, 16f);

            GUILayout.Space(4);
            GUILayout.Label("Polar Cap (override)", EditorStyles.boldLabel);
            polesEnabled = EditorGUILayout.Toggle("Enabled", polesEnabled);
            using (new EditorGUI.DisabledScope(!polesEnabled))
            {
                poleLatitude      = EditorGUILayout.Slider("Latitude (|y|)", poleLatitude, 0.4f, 1f);
                poleBlendWidth    = EditorGUILayout.Slider("Blend Width", poleBlendWidth, 0.01f, 0.4f);
                poleNoiseStrength = EditorGUILayout.Slider("Edge Noise", poleNoiseStrength, 0f, 0.2f);
            }

            GUILayout.Space(4);
            GUILayout.Label("Relief", EditorStyles.boldLabel);
            heightScale = EditorGUILayout.Slider("Height Scale", heightScale, 0f, 0.15f);

            GUILayout.Space(4);
            GUILayout.Label("Clouds", EditorStyles.boldLabel);
            cloudsEnabled        = EditorGUILayout.Toggle("Enabled", cloudsEnabled);
            using (new EditorGUI.DisabledScope(!cloudsEnabled))
            {
                cloudAltitude        = EditorGUILayout.Slider("Altitude", cloudAltitude, 0.001f, 0.08f);
                cloudFrequency       = EditorGUILayout.Slider("Frequency", cloudFrequency, 0.5f, 6f);
                cloudOctaves         = EditorGUILayout.IntSlider("Octaves", cloudOctaves, 1, 10);
                cloudLacunarity      = EditorGUILayout.Slider("Lacunarity", cloudLacunarity, 1.5f, 4f);
                cloudGain            = EditorGUILayout.Slider("Gain", cloudGain, 0.2f, 0.8f);
                cloudWarpStrength    = EditorGUILayout.Slider("Warp Strength", cloudWarpStrength, 0f, 3f);
                cloudCoverage        = EditorGUILayout.Slider("Coverage", cloudCoverage, 0f, 1f);
                cloudSoftness        = EditorGUILayout.Slider("Softness", cloudSoftness, 0.01f, 0.4f);
                cloudDetailStrength  = EditorGUILayout.Slider("Detail", cloudDetailStrength, 0f, 1f);
                cloudDensity         = EditorGUILayout.Slider("Density Scale", cloudDensity, 0f, 3f);
                cloudColor           = EditorGUILayout.ColorField("Color", cloudColor);
                cloudShadowStrength  = EditorGUILayout.Slider("Shadow Strength", cloudShadowStrength, 0f, 1f);
                cloudParallax        = EditorGUILayout.Slider("Shadow Parallax", cloudParallax, 0f, 0.15f);
                cloudAmbient         = EditorGUILayout.Slider("Night-Side Fade", cloudAmbient, 0f, 0.3f);
            }

            GUILayout.Space(4);
            colorsFoldout = EditorGUILayout.Foldout(colorsFoldout, "Biome Colors (Whittaker grid)", true);
            if (colorsFoldout)
            {
                EditorGUI.indentLevel++;
                GUILayout.Label("Ocean", EditorStyles.miniBoldLabel);
                oceanDeep    = EditorGUILayout.ColorField("Deep",    oceanDeep);
                oceanShallow = EditorGUILayout.ColorField("Shallow", oceanShallow);
                beach        = EditorGUILayout.ColorField("Beach",   beach);

                GUILayout.Label("Cold biomes", EditorStyles.miniBoldLabel);
                tundra       = EditorGUILayout.ColorField("Tundra (cold dry/mid)",  tundra);
                taiga        = EditorGUILayout.ColorField("Taiga (cold wet)",       taiga);

                GUILayout.Label("Temperate biomes", EditorStyles.miniBoldLabel);
                grass        = EditorGUILayout.ColorField("Grassland (mild mid)",   grass);
                forest       = EditorGUILayout.ColorField("Forest (mild wet)",      forest);

                GUILayout.Label("Hot biomes", EditorStyles.miniBoldLabel);
                desert       = EditorGUILayout.ColorField("Desert (hot dry, mild dry)", desert);
                savanna      = EditorGUILayout.ColorField("Savanna (hot mid)",      savanna);
                jungle       = EditorGUILayout.ColorField("Jungle (hot wet)",       jungle);

                GUILayout.Label("Overrides", EditorStyles.miniBoldLabel);
                mountain     = EditorGUILayout.ColorField("Mountain Rock", mountain);
                snow         = EditorGUILayout.ColorField("Snow",          snow);
                polar        = EditorGUILayout.ColorField("Polar Cap",     polar);
                EditorGUI.indentLevel--;
            }
        }

        void DrawRockyUI()
        {
            GUILayout.Label("Base Terrain", EditorStyles.boldLabel);
            rkBaseFrequency  = EditorGUILayout.Slider("Frequency", rkBaseFrequency, 0.3f, 8f);
            rkBaseOctaves    = EditorGUILayout.IntSlider("Octaves", rkBaseOctaves, 1, 10);
            rkBaseLacunarity = EditorGUILayout.Slider("Lacunarity", rkBaseLacunarity, 1.5f, 4f);
            rkBaseGain       = EditorGUILayout.Slider("Gain", rkBaseGain, 0.2f, 0.8f);
            rkBaseAmplitude  = EditorGUILayout.Slider("Amplitude", rkBaseAmplitude, 0.02f, 1.0f);
            rkBaseWarp       = EditorGUILayout.Slider("Warp Strength", rkBaseWarp, 0f, 2f);

            GUILayout.Space(4);
            GUILayout.Label("Mare (Dark Plains)", EditorStyles.boldLabel);
            rkMareEnabled = EditorGUILayout.Toggle("Enabled", rkMareEnabled);
            using (new EditorGUI.DisabledScope(!rkMareEnabled))
            {
                rkMareFrequency = EditorGUILayout.Slider("Frequency", rkMareFrequency, 0.3f, 4f);
                rkMareCoverage  = EditorGUILayout.Slider("Coverage",  rkMareCoverage, 0f, 1f);
                rkMareSoftness  = EditorGUILayout.Slider("Softness",  rkMareSoftness, 0.01f, 0.4f);
                rkMareFlatten   = EditorGUILayout.Slider("Flatten",   rkMareFlatten,  0f, 1f);
                rkMareDepth     = EditorGUILayout.Slider("Depth Drop", rkMareDepth,   0f, 0.4f);
            }

            GUILayout.Space(4);
            GUILayout.Label("Regolith / Dust", EditorStyles.boldLabel);
            rkDustFrequency = EditorGUILayout.Slider("Frequency", rkDustFrequency, 0.5f, 10f);
            rkDustStrength  = EditorGUILayout.Slider("Strength",  rkDustStrength,  0f, 1f);

            GUILayout.Space(4);
            GUILayout.Label("Poles (Optional)", EditorStyles.boldLabel);
            rkPolesEnabled = EditorGUILayout.Toggle("Enabled", rkPolesEnabled);
            using (new EditorGUI.DisabledScope(!rkPolesEnabled))
            {
                rkPoleLatitude      = EditorGUILayout.Slider("Latitude (|y|)", rkPoleLatitude, 0.4f, 1f);
                rkPoleBlendWidth    = EditorGUILayout.Slider("Blend Width", rkPoleBlendWidth, 0.01f, 0.4f);
                rkPoleNoiseStrength = EditorGUILayout.Slider("Edge Noise", rkPoleNoiseStrength, 0f, 0.2f);
            }

            GUILayout.Space(4);
            rockyDetailFoldout = EditorGUILayout.Foldout(rockyDetailFoldout, "Detail Texture (triplanar)", true);
            if (rockyDetailFoldout)
            {
                EditorGUI.indentLevel++;
                rkDetailEnabled = EditorGUILayout.Toggle("Color Enabled", rkDetailEnabled);
                using (new EditorGUI.DisabledScope(!rkDetailEnabled))
                {
                    rkDetailMap = (Texture2D)EditorGUILayout.ObjectField(
                        new GUIContent("Color Map", "Tileable surface texture (e.g. real asteroid photo). Wrap mode = Repeat."),
                        rkDetailMap, typeof(Texture2D), false);
                    rkDetailStrength = EditorGUILayout.Slider("Color Strength", rkDetailStrength, 0f, 1f);
                }

                GUILayout.Space(2);
                rkDetailNormalEnabled = EditorGUILayout.Toggle("Normal Enabled", rkDetailNormalEnabled);
                using (new EditorGUI.DisabledScope(!rkDetailNormalEnabled))
                {
                    rkDetailNormalMap = (Texture2D)EditorGUILayout.ObjectField(
                        new GUIContent("Normal Map", "Tileable normal map matching the color texture. Texture Type must be set to Normal Map in its importer."),
                        rkDetailNormalMap, typeof(Texture2D), false);
                    rkDetailNormalStrength = EditorGUILayout.Slider("Normal Strength", rkDetailNormalStrength, 0f, 3f);
                }

                GUILayout.Space(2);
                GUILayout.Label("Shared mapping", EditorStyles.miniBoldLabel);
                rkDetailTiling   = EditorGUILayout.Slider("Tiling",   rkDetailTiling,   0f, 3f);
                rkDetailOffset   = EditorGUILayout.Vector2Field("Offset", rkDetailOffset);
                rkDetailBlendSharpness = EditorGUILayout.Slider("Blend Sharpness", rkDetailBlendSharpness, 1f, 16f);
                EditorGUI.indentLevel--;
            }

            GUILayout.Space(4);
            GUILayout.Label("Relief", EditorStyles.boldLabel);
            rkHeightScale = EditorGUILayout.Slider("Height Scale", rkHeightScale, 0f, 0.15f);

            GUILayout.Space(4);
            rockyPaletteFoldout = EditorGUILayout.Foldout(rockyPaletteFoldout, "Palette", true);
            if (rockyPaletteFoldout)
            {
                EditorGUI.indentLevel++;
                rkHighland = EditorGUILayout.ColorField("Highland", rkHighland);
                rkMare     = EditorGUILayout.ColorField("Mare", rkMare);
                rkDust     = EditorGUILayout.ColorField("Dust", rkDust);
                rkPolar    = EditorGUILayout.ColorField("Polar", rkPolar);
                EditorGUI.indentLevel--;
            }
        }

        void DrawGasGiantUI()
        {
            GUILayout.Label("Bands", EditorStyles.boldLabel);
            ggBandStretch    = EditorGUILayout.Slider("Band Stretch", ggBandStretch, 1f, 16f);
            ggBandFrequency  = EditorGUILayout.Slider("Frequency", ggBandFrequency, 0.2f, 6f);
            ggBandOctaves    = EditorGUILayout.IntSlider("Octaves", ggBandOctaves, 1, 10);
            ggBandLacunarity = EditorGUILayout.Slider("Lacunarity", ggBandLacunarity, 1.5f, 4f);
            ggBandGain       = EditorGUILayout.Slider("Gain", ggBandGain, 0.2f, 0.8f);
            ggBandContrast   = EditorGUILayout.Slider("Contrast", ggBandContrast, 0f, 0.8f);
            ggBandRepetition = EditorGUILayout.Slider("Palette Repeat", ggBandRepetition, 0.5f, 8f);
            ggBandLatShift   = EditorGUILayout.Slider("Lat Noise Shift", ggBandLatShift, 0f, 0.6f);
            ggBandWarp       = EditorGUILayout.Slider("Warp Strength",   ggBandWarp,     0f, 2.5f);

            GUILayout.Space(4);
            GUILayout.Label("Zonal Flow (Twist)", EditorStyles.boldLabel);
            ggFlowStrength  = EditorGUILayout.Slider("Shear (East)", ggFlowStrength, 0f, 1.5f);
            ggCurlStrength  = EditorGUILayout.Slider("Curl (North)", ggCurlStrength, 0f, 1.0f);
            ggFlowFrequency = EditorGUILayout.Slider("Frequency", ggFlowFrequency, 0.1f, 4f);
            ggFlowOctaves   = EditorGUILayout.IntSlider("Octaves", ggFlowOctaves, 1, 8);

            GUILayout.Space(4);
            GUILayout.Label("Detail Turbulence", EditorStyles.boldLabel);
            ggDetailFrequency = EditorGUILayout.Slider("Frequency", ggDetailFrequency, 1f, 16f);
            ggDetailOctaves   = EditorGUILayout.IntSlider("Octaves", ggDetailOctaves, 1, 10);
            ggDetailContrast  = EditorGUILayout.Slider("Contrast", ggDetailContrast, 0f, 1f);

            GUILayout.Space(4);
            GUILayout.Label("Poles", EditorStyles.boldLabel);
            ggPoleLatitude = EditorGUILayout.Slider("Latitude (|y|)", ggPoleLatitude, 0.3f, 1f);
            ggPoleDarken   = EditorGUILayout.Slider("Darken", ggPoleDarken, 0f, 0.6f);

            GUILayout.Space(4);
            GUILayout.Label("Relief", EditorStyles.boldLabel);
            ggHeightScale = EditorGUILayout.Slider("Height Scale", ggHeightScale, 0f, 0.03f);

            GUILayout.Space(4);
            gasPaletteFoldout = EditorGUILayout.Foldout(gasPaletteFoldout, "Palette (south → north)", true);
            if (gasPaletteFoldout)
            {
                EditorGUI.indentLevel++;
                ggPalette0 = EditorGUILayout.ColorField("0 (South Pole)", ggPalette0);
                ggPalette1 = EditorGUILayout.ColorField("1", ggPalette1);
                ggPalette2 = EditorGUILayout.ColorField("2", ggPalette2);
                ggPalette3 = EditorGUILayout.ColorField("3 (Equator)", ggPalette3);
                ggPalette4 = EditorGUILayout.ColorField("4", ggPalette4);
                ggPalette5 = EditorGUILayout.ColorField("5 (North Pole)", ggPalette5);
                EditorGUI.indentLevel--;
            }

            GUILayout.Space(4);
            gasStormsFoldout = EditorGUILayout.Foldout(gasStormsFoldout, "Storms", true);
            if (gasStormsFoldout)
            {
                EditorGUI.indentLevel++;

                ggStormNoiseFrequency = EditorGUILayout.Slider("Internal Noise Freq", ggStormNoiseFrequency, 2f, 40f);

                GUILayout.Label("Big (Great-Red-Spot class)", EditorStyles.miniBoldLabel);
                ggBigStormCount     = EditorGUILayout.IntSlider("Count", ggBigStormCount, 0, 6);
                ggBigStormRadius    = EditorGUILayout.Slider("Radius (rad)", ggBigStormRadius, 0.02f, 0.5f);
                ggBigStormIntensity = EditorGUILayout.Slider("Intensity", ggBigStormIntensity, 0f, 1f);
                ggBigStormSwirl     = EditorGUILayout.Slider("Swirl", ggBigStormSwirl, 0f, 1.5f);
                ggBigStormTint      = EditorGUILayout.ColorField("Tint", ggBigStormTint);

                GUILayout.Label("Small Ovals", EditorStyles.miniBoldLabel);
                ggSmallStormCount     = EditorGUILayout.IntSlider("Count", ggSmallStormCount, 0, 60);
                ggSmallStormRadius    = EditorGUILayout.Slider("Radius (rad)", ggSmallStormRadius, 0.005f, 0.15f);
                ggSmallStormIntensity = EditorGUILayout.Slider("Intensity", ggSmallStormIntensity, 0f, 1f);
                ggSmallStormSwirl     = EditorGUILayout.Slider("Swirl", ggSmallStormSwirl, 0f, 1.5f);
                ggSmallStormTint      = EditorGUILayout.ColorField("Tint", ggSmallStormTint);

                EditorGUI.indentLevel--;
            }
        }

        void DrawStarUI()
        {
            GUILayout.Label("Macro Temperature", EditorStyles.boldLabel);
            stWarp            = EditorGUILayout.Slider("Warp Strength", stWarp, 0f, 3f);
            stMacroFrequency  = EditorGUILayout.Slider("Frequency", stMacroFrequency, 0.3f, 6f);
            stMacroOctaves    = EditorGUILayout.IntSlider("Octaves", stMacroOctaves, 1, 10);
            stMacroLacunarity = EditorGUILayout.Slider("Lacunarity", stMacroLacunarity, 1.5f, 4f);
            stMacroGain       = EditorGUILayout.Slider("Gain", stMacroGain, 0.2f, 0.8f);

            GUILayout.Space(4);
            GUILayout.Label("Granulation", EditorStyles.boldLabel);
            stGranuleFrequency = EditorGUILayout.Slider("Frequency", stGranuleFrequency, 2f, 40f);
            stGranuleOctaves   = EditorGUILayout.IntSlider("Octaves", stGranuleOctaves, 1, 8);
            stGranuleContrast  = EditorGUILayout.Slider("Contrast", stGranuleContrast, 0f, 1f);

            GUILayout.Space(4);
            GUILayout.Label("Emission Map (alpha channel)", EditorStyles.boldLabel);
            stEmissionFloor      = EditorGUILayout.Slider("Floor", stEmissionFloor, 0f, 1f);
            stEmissionScale      = EditorGUILayout.Slider("Temperature Scale", stEmissionScale, 0f, 1f);
            stSpotEmissionDarken = EditorGUILayout.Slider("Spot Darken", stSpotEmissionDarken, 0f, 1f);

            GUILayout.Space(4);
            starSpotsFoldout = EditorGUILayout.Foldout(starSpotsFoldout, "Sunspots", true);
            if (starSpotsFoldout)
            {
                EditorGUI.indentLevel++;
                stSpotCount    = EditorGUILayout.IntSlider("Count", stSpotCount, 0, 40);
                stSpotRadius   = EditorGUILayout.Slider("Radius (rad)", stSpotRadius, 0.005f, 0.2f);
                stSpotStrength = EditorGUILayout.Slider("Darken", stSpotStrength, 0f, 1f);
                stSpotSoftness = EditorGUILayout.Slider("Edge Softness", stSpotSoftness, 0.05f, 1f);
                EditorGUI.indentLevel--;
            }

            GUILayout.Space(4);
            starPaletteFoldout = EditorGUILayout.Foldout(starPaletteFoldout, "Temperature Palette (cold → hot)", true);
            if (starPaletteFoldout)
            {
                EditorGUI.indentLevel++;
                stPalette0 = EditorGUILayout.ColorField("0 (Spot Umbra)", stPalette0);
                stPalette1 = EditorGUILayout.ColorField("1 (Cool)", stPalette1);
                stPalette2 = EditorGUILayout.ColorField("2 (Mid)", stPalette2);
                stPalette3 = EditorGUILayout.ColorField("3 (Warm)", stPalette3);
                stPalette4 = EditorGUILayout.ColorField("4 (Hot)", stPalette4);
                EditorGUI.indentLevel--;
            }

            GUILayout.Space(4);
            GUILayout.Label("Material (HDR, drives bloom)", EditorStyles.boldLabel);
            stBaseTint                = EditorGUILayout.ColorField(new GUIContent("Base Tint"), stBaseTint, true, true, true);
            stMaterialEmissionFloor   = EditorGUILayout.Slider("Emission Floor",  stMaterialEmissionFloor, 0f, 3f);
            stMaterialEmissionBoost   = EditorGUILayout.Slider("Emission Boost",  stMaterialEmissionBoost, 0f, 8f);
        }

        void EnsurePreview()
        {
            if (previewGO == null)
            {
                var existing = GameObject.Find(PreviewObjectName);
                if (existing != null) DestroyImmediate(existing);

                previewGO = new GameObject(PreviewObjectName) { hideFlags = HideFlags.DontSave };
                previewGO.AddComponent<MeshFilter>();
                previewGO.AddComponent<MeshRenderer>();
            }

            if (previewMesh == null || lastMeshSubdivisions != lodSubdivisions)
            {
                if (previewMesh != null) DestroyImmediate(previewMesh);
                previewMesh = QuadSphereMesh.Build(lodSubdivisions, 1.0f);
                previewMesh.hideFlags = HideFlags.DontSave;
                lastMeshSubdivisions = lodSubdivisions;
                previewGO.GetComponent<MeshFilter>().sharedMesh = previewMesh;
            }

            string wantedShader = mode == PlanetGenMode.Star
                ? StarShaderName
                : SurfaceShaderName;
            if (previewMaterial != null && currentPreviewShader != wantedShader)
            {
                DestroyImmediate(previewMaterial);
                previewMaterial = null;
            }
            if (previewMaterial == null)
            {
                var sh = Shader.Find(wantedShader);
                if (sh == null)
                {
                    Debug.LogError($"[PlanetGenerator] Shader '{wantedShader}' not found.");
                    return;
                }
                previewMaterial = new Material(sh) { hideFlags = HideFlags.DontSave };
                currentPreviewShader = wantedShader;
                previewGO.GetComponent<MeshRenderer>().sharedMaterial = previewMaterial;
            }

            if (cloudGO == null)
            {
                var existing = GameObject.Find(CloudPreviewObjectName);
                if (existing != null) DestroyImmediate(existing);
                cloudGO = new GameObject(CloudPreviewObjectName) { hideFlags = HideFlags.DontSave };
                cloudGO.transform.SetParent(previewGO.transform, worldPositionStays: false);
                cloudGO.AddComponent<MeshFilter>();
                cloudGO.AddComponent<MeshRenderer>();
            }
            cloudGO.GetComponent<MeshFilter>().sharedMesh = previewMesh;
            cloudGO.transform.localScale = Vector3.one * (1f + cloudAltitude);

            if (cloudMaterial == null)
            {
                var sh = Shader.Find(CloudsShaderName);
                if (sh == null)
                {
                    Debug.LogError($"[PlanetGenerator] Shader '{CloudsShaderName}' not found.");
                    return;
                }
                cloudMaterial = new Material(sh) { hideFlags = HideFlags.DontSave };
                cloudGO.GetComponent<MeshRenderer>().sharedMaterial = cloudMaterial;
            }
        }

        void EnsureCubeRT()
        {
            if (arrayRT != null && arrayRT.width == cubemapSize
                && cubeRT != null && cubeRT.width == cubemapSize
                && normalArrayRT != null && normalArrayRT.width == cubemapSize
                && normalCubeRT != null && normalCubeRT.width == cubemapSize
                && cloudArrayRT != null && cloudArrayRT.width == cubemapSize
                && cloudCubeRT != null && cloudCubeRT.width == cubemapSize
                && emissionCubeRT != null && emissionCubeRT.width == cubemapSize) return;

            if (arrayRT != null) { arrayRT.Release(); DestroyImmediate(arrayRT); }
            if (cubeRT  != null) { cubeRT.Release();  DestroyImmediate(cubeRT); }
            if (normalArrayRT != null) { normalArrayRT.Release(); DestroyImmediate(normalArrayRT); }
            if (normalCubeRT  != null) { normalCubeRT.Release();  DestroyImmediate(normalCubeRT); }
            if (cloudArrayRT != null)  { cloudArrayRT.Release();  DestroyImmediate(cloudArrayRT); }
            if (cloudCubeRT  != null)  { cloudCubeRT.Release();   DestroyImmediate(cloudCubeRT); }

            // Color/height RT is ARGBHalf: the alpha carries height for the normal
            // pass, and 8-bit quantization there produces visible topographic-contour
            // banding in the derived normal map.
            arrayRT = MakeRT(TextureDimension.Tex2DArray, true,  "PlanetGen_ArrayRT",       RenderTextureFormat.ARGBHalf);
            cubeRT  = MakeRT(TextureDimension.Cube,       false, "PlanetGen_CubeRT",        RenderTextureFormat.ARGBHalf);
            normalArrayRT = MakeRT(TextureDimension.Tex2DArray, true,  "PlanetGen_NormalArrayRT", RenderTextureFormat.ARGB32);
            normalCubeRT  = MakeRT(TextureDimension.Cube,       false, "PlanetGen_NormalCubeRT",  RenderTextureFormat.ARGB32);
            cloudArrayRT  = MakeRT(TextureDimension.Tex2DArray, true,  "PlanetGen_CloudArrayRT",  RenderTextureFormat.ARGB32);
            cloudCubeRT   = MakeRT(TextureDimension.Cube,       false, "PlanetGen_CloudCubeRT",   RenderTextureFormat.ARGB32);
            if (emissionArrayRT != null) { emissionArrayRT.Release(); DestroyImmediate(emissionArrayRT); }
            if (emissionCubeRT  != null) { emissionCubeRT.Release();  DestroyImmediate(emissionCubeRT); }
            emissionArrayRT = MakeRT(TextureDimension.Tex2DArray, true,  "PlanetGen_EmissionArrayRT", RenderTextureFormat.ARGB32);
            emissionCubeRT  = MakeRT(TextureDimension.Cube,       false, "PlanetGen_EmissionCubeRT",  RenderTextureFormat.ARGB32);
        }

        RenderTexture MakeRT(TextureDimension dim, bool randomWrite, string name, RenderTextureFormat format)
        {
            var rt = new RenderTexture(cubemapSize, cubemapSize, 0,
                format, RenderTextureReadWrite.Linear)
            {
                dimension = dim,
                volumeDepth = 6,
                enableRandomWrite = randomWrite,
                useMipMap = false,
                autoGenerateMips = false,
                name = name,
                hideFlags = HideFlags.DontSave,
            };
            rt.Create();
            return rt;
        }

        void Regenerate()
        {
            EnsurePreview();
            if (backend == GenerationBackend.CPU)
            {
                RegenerateCpu();
                return;
            }
            EnsureCubeRT();
            RegenerateGpu();
        }

        void RegenerateGpu()
        {
            ComputeShader cs = null;
            string path = null;
            switch (mode)
            {
                case PlanetGenMode.DebugNoise:
                    if (debugCompute == null)
                        debugCompute = AssetDatabase.LoadAssetAtPath<ComputeShader>(DebugComputePath);
                    cs = debugCompute; path = DebugComputePath; break;
                case PlanetGenMode.Terrestrial:
                    if (terrestrialCompute == null)
                        terrestrialCompute = AssetDatabase.LoadAssetAtPath<ComputeShader>(TerrestrialComputePath);
                    cs = terrestrialCompute; path = TerrestrialComputePath; break;
                case PlanetGenMode.Rocky:
                    if (rockyCompute == null)
                        rockyCompute = AssetDatabase.LoadAssetAtPath<ComputeShader>(RockyComputePath);
                    cs = rockyCompute; path = RockyComputePath; break;
                case PlanetGenMode.GasGiant:
                    if (gasGiantCompute == null)
                        gasGiantCompute = AssetDatabase.LoadAssetAtPath<ComputeShader>(GasGiantComputePath);
                    cs = gasGiantCompute; path = GasGiantComputePath; break;
                case PlanetGenMode.Star:
                    if (starCompute == null)
                        starCompute = AssetDatabase.LoadAssetAtPath<ComputeShader>(StarComputePath);
                    cs = starCompute; path = StarComputePath; break;
            }
            if (cs == null)
            {
                Debug.LogError($"[PlanetGenerator] Compute missing for mode {mode} — expected at '{path}'. " +
                               "Check the file exists and Unity has imported it (right-click the folder → Reimport).");
                return;
            }

            int k = cs.FindKernel("CSMain");
            cs.SetInt("_Size", cubemapSize);
            cs.SetInt("_Seed", seed);

            if (mode == PlanetGenMode.DebugNoise)
            {
                cs.SetTexture(k, "_Output", arrayRT);
                cs.SetFloat("_Frequency", dbgFrequency);
                cs.SetInt("_Octaves", dbgOctaves);
                cs.SetFloat("_Lacunarity", dbgLacunarity);
                cs.SetFloat("_Gain", dbgGain);
                cs.SetFloat("_WarpStrength", dbgWarpStrength);
            }
            else if (mode == PlanetGenMode.Terrestrial)
            {
                cs.SetTexture(k, "_ColorOutput", arrayRT);
                // Identical binding to the export path — keeps preview and
                // exported textures bit-identical (modulo the resolution gap).
                SetTerrestrialUniforms(cs, cubemapSize);
            }
            else if (mode == PlanetGenMode.Rocky)
            {
                SetRockyUniforms(cs, k, arrayRT, cubemapSize);
            }
            else if (mode == PlanetGenMode.GasGiant)
            {
                SetGasGiantUniforms(cs, k, arrayRT, cubemapSize);
            }
            else if (mode == PlanetGenMode.Star)
            {
                SetStarUniforms(cs, k, arrayRT, cubemapSize);
            }

            int groups = Mathf.CeilToInt(cubemapSize / 8f);
            cs.Dispatch(k, groups, groups, 6);

            for (int f = 0; f < 6; f++)
                Graphics.CopyTexture(arrayRT, f, 0, cubeRT, f, 0);

            if (mode == PlanetGenMode.Star)
            {
                for (int f = 0; f < 6; f++)
                    Graphics.CopyTexture(emissionArrayRT, f, 0, emissionCubeRT, f, 0);
            }

            bool hasNormal = false;
            bool hasClouds = false;
            if (mode == PlanetGenMode.Terrestrial)
            {
                hasNormal = DispatchNormalPass(heightScale);
                if (cloudsEnabled)
                    hasClouds = DispatchCloudsPass();
            }
            else if (mode == PlanetGenMode.Rocky)
            {
                hasNormal = DispatchNormalPass(rkHeightScale);
            }
            else if (mode == PlanetGenMode.GasGiant)
            {
                hasNormal = ggHeightScale > 0f && DispatchNormalPass(ggHeightScale);
            }

            if (previewMaterial != null)
            {
                if (mode == PlanetGenMode.Star)
                {
                    previewMaterial.SetTexture("_BaseCube",     cubeRT);
                    previewMaterial.SetTexture("_EmissionCube", emissionCubeRT);
                    SetToggleKeyword(previewMaterial, "_UseStarCube",     "_USE_STAR_CUBE",     true);
                    SetToggleKeyword(previewMaterial, "_UseEmissionCube", "_USE_EMISSION_CUBE", true);
                    previewMaterial.SetColor("_BaseColor",     stBaseTint);
                    previewMaterial.SetFloat("_EmissionFloor", stMaterialEmissionFloor);
                    previewMaterial.SetFloat("_EmissionBoost", stMaterialEmissionBoost);
                }
                else
                {
                    previewMaterial.SetTexture("_BaseCube",   cubeRT);
                    previewMaterial.SetTexture("_NormalCube", hasNormal ? normalCubeRT : null);
                    previewMaterial.SetTexture("_CloudCube",  hasClouds ? cloudCubeRT : null);
                    SetToggleKeyword(previewMaterial, "_UseBaseCube",   "_USE_BASE_CUBE",   true);
                    SetToggleKeyword(previewMaterial, "_UseNormalCube", "_USE_NORMAL_CUBE", hasNormal);
                    SetToggleKeyword(previewMaterial, "_UseCloudCube",  "_USE_CLOUD_CUBE",  hasClouds);
                    previewMaterial.SetColor("_BaseColor",           Color.white);
                    previewMaterial.SetFloat("_CloudShadowStrength", cloudShadowStrength);
                    previewMaterial.SetFloat("_CloudParallax",       cloudParallax);

                    // Detail normal is rocky-only for now; for other modes we
                    // explicitly disable the keyword so a previous rocky preview
                    // doesn't leak its state into a terrestrial/gas preview.
                    bool detailNormalActive = mode == PlanetGenMode.Rocky
                        && rkDetailNormalEnabled
                        && rkDetailNormalMap != null;
                    ApplyDetailNormalToMaterial(previewMaterial, detailNormalActive);
                }
            }

            if (cloudGO != null)
            {
                cloudGO.SetActive(hasClouds);
                cloudGO.transform.localScale = Vector3.one * (1f + cloudAltitude);
            }

            if (cloudMaterial != null && hasClouds)
            {
                cloudMaterial.SetTexture("_CloudCube", cloudCubeRT);
                cloudMaterial.SetColor("_CloudTint",   cloudColor);
                cloudMaterial.SetFloat("_Density",     cloudDensity);
                cloudMaterial.SetFloat("_AmbientFloor", cloudAmbient);
            }

            SceneView.RepaintAll();
        }

        // CPU backend: builds a config from the current editor state, runs
        // the Burst bakery jobs, binds the resulting Cubemaps to the preview
        // material. Each call disposes the previously-baked Cubemaps.
        void RegenerateCpu()
        {
            DisposeCpuCubemaps();
            Texture colorTex = null, normalTex = null, cloudTex = null, emissionTex = null;

            switch (mode)
            {
                case PlanetGenMode.DebugNoise:
                    cpuColorCube = CpuBakery.BakeDebug(cubemapSize, seed,
                        dbgFrequency, dbgOctaves, dbgLacunarity, dbgGain, dbgWarpStrength);
                    colorTex = cpuColorCube;
                    break;

                case PlanetGenMode.Terrestrial:
                {
                    var o = CpuBakery.BakeTerrestrial(ExportConfig(), cubemapSize, seed);
                    cpuColorCube = o.Color; cpuNormalCube = o.Normal; cpuCloudCube = o.Clouds;
                    colorTex = cpuColorCube; normalTex = cpuNormalCube; cloudTex = cpuCloudCube;
                    break;
                }
                case PlanetGenMode.Rocky:
                {
                    var o = CpuBakery.BakeRocky(ExportRockyConfig(), cubemapSize, seed, rkDetailMap);
                    cpuColorCube = o.Color; cpuNormalCube = o.Normal;
                    colorTex = cpuColorCube; normalTex = cpuNormalCube;
                    break;
                }
                case PlanetGenMode.GasGiant:
                {
                    var o = CpuBakery.BakeGasGiant(ExportGasConfig(), cubemapSize, seed);
                    cpuColorCube = o.Color; cpuNormalCube = o.Normal;
                    colorTex = cpuColorCube; normalTex = cpuNormalCube;
                    break;
                }
                case PlanetGenMode.Star:
                {
                    var o = CpuBakery.BakeStar(ExportStarConfig(), cubemapSize, seed);
                    cpuColorCube = o.Color; cpuEmissionCube = o.Emission;
                    colorTex = cpuColorCube; emissionTex = cpuEmissionCube;
                    break;
                }
            }

            // Bind to preview material — same keyword logic as the GPU path.
            if (previewMaterial != null)
            {
                if (mode == PlanetGenMode.Star)
                {
                    previewMaterial.SetTexture("_BaseCube",     colorTex);
                    previewMaterial.SetTexture("_EmissionCube", emissionTex);
                    SetToggleKeyword(previewMaterial, "_UseStarCube",     "_USE_STAR_CUBE",     colorTex != null);
                    SetToggleKeyword(previewMaterial, "_UseEmissionCube", "_USE_EMISSION_CUBE", emissionTex != null);
                    previewMaterial.SetColor("_BaseColor",     stBaseTint);
                    previewMaterial.SetFloat("_EmissionFloor", stMaterialEmissionFloor);
                    previewMaterial.SetFloat("_EmissionBoost", stMaterialEmissionBoost);
                }
                else
                {
                    bool hasNormal = normalTex != null;
                    bool hasClouds = cloudTex != null;
                    previewMaterial.SetTexture("_BaseCube",   colorTex);
                    previewMaterial.SetTexture("_NormalCube", normalTex);
                    previewMaterial.SetTexture("_CloudCube",  cloudTex);
                    SetToggleKeyword(previewMaterial, "_UseBaseCube",   "_USE_BASE_CUBE",   colorTex != null);
                    SetToggleKeyword(previewMaterial, "_UseNormalCube", "_USE_NORMAL_CUBE", hasNormal);
                    SetToggleKeyword(previewMaterial, "_UseCloudCube",  "_USE_CLOUD_CUBE",  hasClouds);
                    previewMaterial.SetColor("_BaseColor",           Color.white);
                    previewMaterial.SetFloat("_CloudShadowStrength", cloudShadowStrength);
                    previewMaterial.SetFloat("_CloudParallax",       cloudParallax);

                    bool detailNormalActive = mode == PlanetGenMode.Rocky
                        && rkDetailNormalEnabled && rkDetailNormalMap != null;
                    ApplyDetailNormalToMaterial(previewMaterial, detailNormalActive);
                }
            }

            bool cloudActive = (mode == PlanetGenMode.Terrestrial) && cloudTex != null;
            if (cloudGO != null)
            {
                cloudGO.SetActive(cloudActive);
                cloudGO.transform.localScale = Vector3.one * (1f + cloudAltitude);
            }
            if (cloudMaterial != null && cloudActive)
            {
                cloudMaterial.SetTexture("_CloudCube", cloudTex);
                cloudMaterial.SetColor("_CloudTint",   cloudColor);
                cloudMaterial.SetFloat("_Density",     cloudDensity);
                cloudMaterial.SetFloat("_AmbientFloor", cloudAmbient);
            }

            SceneView.RepaintAll();
        }

        bool DispatchCloudsPass()
        {
            if (cloudsCompute == null)
                cloudsCompute = AssetDatabase.LoadAssetAtPath<ComputeShader>(CloudsComputePath);
            if (cloudsCompute == null)
            {
                Debug.LogError($"[PlanetGenerator] Clouds compute not found at '{CloudsComputePath}'");
                return false;
            }

            int k = cloudsCompute.FindKernel("CSMain");
            cloudsCompute.SetTexture(k, "_Output", cloudArrayRT);
            cloudsCompute.SetInt("_Size", cubemapSize);
            cloudsCompute.SetInt("_Seed", (int)((uint)seed ^ 0x9E17Ca11u));
            cloudsCompute.SetFloat("_CloudFrequency", cloudFrequency);
            cloudsCompute.SetInt("_CloudOctaves", cloudOctaves);
            cloudsCompute.SetFloat("_CloudLacunarity", cloudLacunarity);
            cloudsCompute.SetFloat("_CloudGain", cloudGain);
            cloudsCompute.SetFloat("_WarpStrength", cloudWarpStrength);
            cloudsCompute.SetFloat("_Coverage", cloudCoverage);
            cloudsCompute.SetFloat("_Softness", cloudSoftness);
            cloudsCompute.SetFloat("_DetailStrength", cloudDetailStrength);
            cloudsCompute.SetVector("_CloudColor", cloudColor);

            int groups = Mathf.CeilToInt(cubemapSize / 8f);
            cloudsCompute.Dispatch(k, groups, groups, 6);

            for (int f = 0; f < 6; f++)
                Graphics.CopyTexture(cloudArrayRT, f, 0, cloudCubeRT, f, 0);

            return true;
        }

        // --- Save / Load -----------------------------------------------------

        const string LibraryRoot      = "Assets/Procedural Planets Generator/Library";
        const string SharedMeshFolder = "Assets/Procedural Planets Generator/Meshes";

        TerrestrialPlanetConfig ExportConfig() => new TerrestrialPlanetConfig
        {
            name = planetName, seed = seed, cubemapSize = cubemapSize,
            continentFrequency = continentFrequency, continentOctaves = continentOctaves,
            continentLacunarity = continentLacunarity, continentGain = continentGain,
            warpStrength = warpStrength, seaLevel = seaLevel,
            elevationAmplitude = elevationAmplitude, mountainStrength = mountainStrength,
            mountainStart = mountainStart, mountainFull = mountainFull,
            biomeContrast = biomeContrast,
            moistureFrequency = moistureFrequency, moistureOctaves = moistureOctaves,
            polesEnabled = polesEnabled,
            poleLatitude = poleLatitude, poleBlendWidth = poleBlendWidth,
            poleNoiseStrength = poleNoiseStrength, heightScale = heightScale,
            altitudeCooling = altitudeCooling, tempNoiseFreq = tempNoiseFreq,
            tempNoiseStrength = tempNoiseStrength, hadleyStrength = hadleyStrength,
            snowTempThreshold = snowTempThreshold, snowTempBlend = snowTempBlend,
            oceanDeep = oceanDeep, oceanShallow = oceanShallow, beach = beach,
            tundra = tundra, taiga = taiga, desert = desert,
            grass = grass, forest = forest, savanna = savanna, jungle = jungle,
            mountain = mountain, snow = snow, polar = polar,
            cloudsEnabled = cloudsEnabled, cloudAltitude = cloudAltitude,
            cloudFrequency = cloudFrequency, cloudOctaves = cloudOctaves,
            cloudLacunarity = cloudLacunarity, cloudGain = cloudGain,
            cloudWarpStrength = cloudWarpStrength, cloudCoverage = cloudCoverage,
            cloudSoftness = cloudSoftness, cloudDetailStrength = cloudDetailStrength,
            cloudColor = cloudColor, cloudDensity = cloudDensity,
            cloudShadowStrength = cloudShadowStrength, cloudParallax = cloudParallax,
            cloudAmbient = cloudAmbient,
        };

        void ImportConfig(TerrestrialPlanetConfig c)
        {
            planetName = c.name; seed = c.seed; cubemapSize = c.cubemapSize;
            continentFrequency = c.continentFrequency; continentOctaves = c.continentOctaves;
            continentLacunarity = c.continentLacunarity; continentGain = c.continentGain;
            warpStrength = c.warpStrength; seaLevel = c.seaLevel;
            elevationAmplitude = c.elevationAmplitude; mountainStrength = c.mountainStrength;
            mountainStart = c.mountainStart; mountainFull = c.mountainFull;
            biomeContrast = c.biomeContrast > 0f ? c.biomeContrast : 2.5f;
            moistureFrequency = c.moistureFrequency; moistureOctaves = c.moistureOctaves;
            polesEnabled = c.polesEnabled;
            poleLatitude = c.poleLatitude; poleBlendWidth = c.poleBlendWidth;
            poleNoiseStrength = c.poleNoiseStrength; heightScale = c.heightScale;
            altitudeCooling = c.altitudeCooling > 0f ? c.altitudeCooling : 0.55f;
            tempNoiseFreq = c.tempNoiseFreq > 0f ? c.tempNoiseFreq : 4.0f;
            tempNoiseStrength = c.tempNoiseStrength;
            hadleyStrength = c.hadleyStrength;
            snowTempThreshold = c.snowTempThreshold > 0f ? c.snowTempThreshold : 0.22f;
            snowTempBlend = c.snowTempBlend > 0f ? c.snowTempBlend : 0.06f;
            oceanDeep = c.oceanDeep; oceanShallow = c.oceanShallow; beach = c.beach;
            tundra = c.tundra; taiga = c.taiga; desert = c.desert;
            grass = c.grass; forest = c.forest; savanna = c.savanna; jungle = c.jungle;
            mountain = c.mountain; snow = c.snow; polar = c.polar;
            cloudsEnabled = c.cloudsEnabled; cloudAltitude = c.cloudAltitude;
            cloudFrequency = c.cloudFrequency; cloudOctaves = c.cloudOctaves;
            cloudLacunarity = c.cloudLacunarity; cloudGain = c.cloudGain;
            cloudWarpStrength = c.cloudWarpStrength; cloudCoverage = c.cloudCoverage;
            cloudSoftness = c.cloudSoftness; cloudDetailStrength = c.cloudDetailStrength;
            cloudColor = c.cloudColor; cloudDensity = c.cloudDensity;
            cloudShadowStrength = c.cloudShadowStrength; cloudParallax = c.cloudParallax;
            cloudAmbient = c.cloudAmbient;
        }

        RockyPlanetConfig ExportRockyConfig() => new RockyPlanetConfig
        {
            name = planetName, seed = seed, cubemapSize = cubemapSize,
            baseFrequency = rkBaseFrequency, baseOctaves = rkBaseOctaves,
            baseLacunarity = rkBaseLacunarity, baseGain = rkBaseGain,
            baseAmplitude = rkBaseAmplitude, baseWarp = rkBaseWarp,
            mareEnabled = rkMareEnabled, mareFrequency = rkMareFrequency,
            mareCoverage = rkMareCoverage, mareSoftness = rkMareSoftness,
            mareFlatten = rkMareFlatten, mareDepth = rkMareDepth,
            dustFrequency = rkDustFrequency, dustStrength = rkDustStrength,
            polesEnabled = rkPolesEnabled, poleLatitude = rkPoleLatitude,
            poleBlendWidth = rkPoleBlendWidth, poleNoiseStrength = rkPoleNoiseStrength,
            detailEnabled  = rkDetailEnabled,
            detailMapAssetPath = rkDetailMap != null ? AssetDatabase.GetAssetPath(rkDetailMap) : "",
            detailTiling   = rkDetailTiling,
            detailOffsetU  = rkDetailOffset.x,
            detailOffsetV  = rkDetailOffset.y,
            detailStrength = rkDetailStrength,
            detailBlendSharpness = rkDetailBlendSharpness,
            detailNormalEnabled      = rkDetailNormalEnabled,
            detailNormalMapAssetPath = rkDetailNormalMap != null ? AssetDatabase.GetAssetPath(rkDetailNormalMap) : "",
            detailNormalStrength     = rkDetailNormalStrength,
            heightScale = rkHeightScale,
            highlandColor = rkHighland, mareColor = rkMare, dustColor = rkDust,
            polarColor = rkPolar,
        };

        void ImportRockyConfig(RockyPlanetConfig c)
        {
            planetName = c.name; seed = c.seed; cubemapSize = c.cubemapSize;
            rkBaseFrequency = c.baseFrequency; rkBaseOctaves = c.baseOctaves;
            rkBaseLacunarity = c.baseLacunarity; rkBaseGain = c.baseGain;
            rkBaseAmplitude = c.baseAmplitude; rkBaseWarp = c.baseWarp;
            rkMareEnabled = c.mareEnabled; rkMareFrequency = c.mareFrequency;
            rkMareCoverage = c.mareCoverage; rkMareSoftness = c.mareSoftness;
            rkMareFlatten = c.mareFlatten; rkMareDepth = c.mareDepth;
            rkDustFrequency = c.dustFrequency; rkDustStrength = c.dustStrength;
            rkPolesEnabled = c.polesEnabled; rkPoleLatitude = c.poleLatitude;
            rkPoleBlendWidth = c.poleBlendWidth; rkPoleNoiseStrength = c.poleNoiseStrength;
            rkDetailEnabled  = c.detailEnabled;
            rkDetailMap      = !string.IsNullOrEmpty(c.detailMapAssetPath)
                ? AssetDatabase.LoadAssetAtPath<Texture2D>(c.detailMapAssetPath)
                : null;
            rkDetailTiling   = c.detailTiling;
            rkDetailOffset   = new Vector2(c.detailOffsetU, c.detailOffsetV);
            rkDetailStrength = c.detailStrength;
            rkDetailBlendSharpness = c.detailBlendSharpness;
            rkDetailNormalEnabled  = c.detailNormalEnabled;
            rkDetailNormalMap      = !string.IsNullOrEmpty(c.detailNormalMapAssetPath)
                ? AssetDatabase.LoadAssetAtPath<Texture2D>(c.detailNormalMapAssetPath)
                : null;
            rkDetailNormalStrength = c.detailNormalStrength;
            rkHeightScale = c.heightScale;
            rkHighland = c.highlandColor; rkMare = c.mareColor; rkDust = c.dustColor;
            rkPolar = c.polarColor;
        }

        GasPlanetConfig ExportGasConfig() => new GasPlanetConfig
        {
            name = planetName, seed = seed, cubemapSize = cubemapSize,
            bandStretch = ggBandStretch, bandFrequency = ggBandFrequency,
            bandOctaves = ggBandOctaves, bandLacunarity = ggBandLacunarity,
            bandGain = ggBandGain, bandContrast = ggBandContrast,
            bandRepetition = ggBandRepetition, bandLatShift = ggBandLatShift,
            bandWarp = ggBandWarp,
            flowStrength = ggFlowStrength, flowFrequency = ggFlowFrequency,
            flowOctaves = ggFlowOctaves, curlStrength = ggCurlStrength,
            detailFrequency = ggDetailFrequency, detailOctaves = ggDetailOctaves,
            detailContrast = ggDetailContrast,
            stormNoiseFrequency = ggStormNoiseFrequency,
            poleLatitude = ggPoleLatitude, poleDarken = ggPoleDarken,
            palette0 = ggPalette0, palette1 = ggPalette1, palette2 = ggPalette2,
            palette3 = ggPalette3, palette4 = ggPalette4, palette5 = ggPalette5,
            bigStormCount = ggBigStormCount, bigStormRadius = ggBigStormRadius,
            bigStormIntensity = ggBigStormIntensity, bigStormSwirl = ggBigStormSwirl,
            bigStormTint = ggBigStormTint,
            smallStormCount = ggSmallStormCount, smallStormRadius = ggSmallStormRadius,
            smallStormIntensity = ggSmallStormIntensity, smallStormSwirl = ggSmallStormSwirl,
            smallStormTint = ggSmallStormTint,
            heightScale = ggHeightScale,
        };

        void ImportGasConfig(GasPlanetConfig c)
        {
            planetName = c.name; seed = c.seed; cubemapSize = c.cubemapSize;
            ggBandStretch = c.bandStretch; ggBandFrequency = c.bandFrequency;
            ggBandOctaves = c.bandOctaves; ggBandLacunarity = c.bandLacunarity;
            ggBandGain = c.bandGain; ggBandContrast = c.bandContrast;
            ggBandRepetition = c.bandRepetition; ggBandLatShift = c.bandLatShift;
            ggBandWarp = c.bandWarp;
            ggFlowStrength = c.flowStrength; ggFlowFrequency = c.flowFrequency;
            ggFlowOctaves = c.flowOctaves; ggCurlStrength = c.curlStrength;
            ggDetailFrequency = c.detailFrequency; ggDetailOctaves = c.detailOctaves;
            ggDetailContrast = c.detailContrast;
            ggStormNoiseFrequency = c.stormNoiseFrequency;
            ggPoleLatitude = c.poleLatitude; ggPoleDarken = c.poleDarken;
            ggPalette0 = c.palette0; ggPalette1 = c.palette1; ggPalette2 = c.palette2;
            ggPalette3 = c.palette3; ggPalette4 = c.palette4; ggPalette5 = c.palette5;
            ggBigStormCount = c.bigStormCount; ggBigStormRadius = c.bigStormRadius;
            ggBigStormIntensity = c.bigStormIntensity; ggBigStormSwirl = c.bigStormSwirl;
            ggBigStormTint = c.bigStormTint;
            ggSmallStormCount = c.smallStormCount; ggSmallStormRadius = c.smallStormRadius;
            ggSmallStormIntensity = c.smallStormIntensity; ggSmallStormSwirl = c.smallStormSwirl;
            ggSmallStormTint = c.smallStormTint;
            ggHeightScale = c.heightScale;
        }

        StarConfig ExportStarConfig() => new StarConfig
        {
            name = planetName, seed = seed, cubemapSize = cubemapSize,
            warp = stWarp,
            macroFrequency = stMacroFrequency, macroOctaves = stMacroOctaves,
            macroLacunarity = stMacroLacunarity, macroGain = stMacroGain,
            granuleFrequency = stGranuleFrequency, granuleOctaves = stGranuleOctaves,
            granuleContrast = stGranuleContrast,
            emissionFloor = stEmissionFloor, emissionScale = stEmissionScale,
            spotEmissionDarken = stSpotEmissionDarken,
            spotCount = stSpotCount, spotRadius = stSpotRadius,
            spotStrength = stSpotStrength, spotSoftness = stSpotSoftness,
            palette0 = stPalette0, palette1 = stPalette1, palette2 = stPalette2,
            palette3 = stPalette3, palette4 = stPalette4,
            baseTint = stBaseTint,
            materialEmissionFloor = stMaterialEmissionFloor,
            materialEmissionBoost = stMaterialEmissionBoost,
        };

        void ImportStarConfig(StarConfig c)
        {
            planetName = c.name; seed = c.seed; cubemapSize = c.cubemapSize;
            stWarp = c.warp;
            stMacroFrequency = c.macroFrequency; stMacroOctaves = c.macroOctaves;
            stMacroLacunarity = c.macroLacunarity; stMacroGain = c.macroGain;
            stGranuleFrequency = c.granuleFrequency; stGranuleOctaves = c.granuleOctaves;
            stGranuleContrast = c.granuleContrast;
            stEmissionFloor = c.emissionFloor; stEmissionScale = c.emissionScale;
            stSpotEmissionDarken = c.spotEmissionDarken;
            stSpotCount = c.spotCount; stSpotRadius = c.spotRadius;
            stSpotStrength = c.spotStrength; stSpotSoftness = c.spotSoftness;
            stPalette0 = c.palette0; stPalette1 = c.palette1; stPalette2 = c.palette2;
            stPalette3 = c.palette3; stPalette4 = c.palette4;
            stBaseTint = c.baseTint;
            stMaterialEmissionFloor = c.materialEmissionFloor;
            stMaterialEmissionBoost = c.materialEmissionBoost;
        }

        void SavePlanet()
        {
            if (mode == PlanetGenMode.Terrestrial) { SaveTerrestrialPlanet(); return; }
            if (mode == PlanetGenMode.Rocky)       { SaveRockyPlanet();       return; }
            if (mode == PlanetGenMode.GasGiant)    { SaveGasGiantPlanet();    return; }
            if (mode == PlanetGenMode.Star)        { SaveStarPlanet();        return; }

            EditorUtility.DisplayDialog("Save Planet",
                "Save is not supported in this mode.", "OK");
        }

        void SaveTerrestrialPlanet()
        {
            EnsureComputesLoaded();
            if (terrestrialCompute == null || normalCompute == null ||
                (cloudsEnabled && cloudsCompute == null))
            {
                Debug.LogError("[PlanetGenerator] One or more compute shaders failed to load.");
                return;
            }

            string safeName = SanitizeFileName(planetName);
            string relRoot  = $"{LibraryRoot}/{safeName}";
            string absRoot  = Path.Combine(Directory.GetCurrentDirectory(), relRoot);
            string relTex   = $"{relRoot}/Textures";
            string relMat   = $"{relRoot}/Materials";

            Directory.CreateDirectory(Path.Combine(absRoot, "Textures"));
            Directory.CreateDirectory(Path.Combine(absRoot, "Materials"));

            int size = exportResolution;
            int groups = Mathf.CeilToInt(size / 8f);

            RenderTexture colorArr  = AllocExportRT(size, TextureDimension.Tex2DArray, true,  RenderTextureFormat.ARGBHalf);
            RenderTexture colorCube = AllocExportRT(size, TextureDimension.Cube,       false, RenderTextureFormat.ARGBHalf);
            RenderTexture normalArr = AllocExportRT(size, TextureDimension.Tex2DArray, true);
            RenderTexture cloudArr  = cloudsEnabled ? AllocExportRT(size, TextureDimension.Tex2DArray, true) : null;

            try
            {
                int kT = terrestrialCompute.FindKernel("CSMain");
                terrestrialCompute.SetTexture(kT, "_ColorOutput", colorArr);
                SetTerrestrialUniforms(terrestrialCompute, size);
                terrestrialCompute.Dispatch(kT, groups, groups, 6);
                for (int f = 0; f < 6; f++) Graphics.CopyTexture(colorArr, f, 0, colorCube, f, 0);

                int kN = normalCompute.FindKernel("CSMain");
                normalCompute.SetTexture(kN, "_HeightCube", colorCube);
                normalCompute.SetTexture(kN, "_NormalOutput", normalArr);
                normalCompute.SetInt("_Size", size);
                normalCompute.SetFloat("_HeightScale", heightScale);
                normalCompute.Dispatch(kN, groups, groups, 6);

                if (cloudsEnabled)
                {
                    int kC = cloudsCompute.FindKernel("CSMain");
                    cloudsCompute.SetTexture(kC, "_Output", cloudArr);
                    SetCloudsUniforms(cloudsCompute, size);
                    cloudsCompute.Dispatch(kC, groups, groups, 6);
                }

                string colorPngRel  = $"{relTex}/color.png";
                string normalPngRel = $"{relTex}/normal.png";
                string cloudPngRel  = $"{relTex}/cloud.png";
                string colorPngAbs  = Path.Combine(Directory.GetCurrentDirectory(), colorPngRel);
                string normalPngAbs = Path.Combine(Directory.GetCurrentDirectory(), normalPngRel);
                string cloudPngAbs  = Path.Combine(Directory.GetCurrentDirectory(), cloudPngRel);

                WriteCubemapStripPng(colorArr,  colorPngAbs);
                WriteCubemapStripPng(normalArr, normalPngAbs);
                if (cloudsEnabled) WriteCubemapStripPng(cloudArr, cloudPngAbs);

                AssetDatabase.Refresh();

                var colorCm  = LoadCubemapPng(colorPngRel);
                var normalCm = LoadCubemapPng(normalPngRel);
                var cloudCm  = cloudsEnabled ? LoadCubemapPng(cloudPngRel) : null;

                Mesh[] lodMeshes = EnsureSharedLodMeshes();

                string bodyMatPath  = $"{relMat}/{safeName}_Body.mat";
                string cloudMatPath = $"{relMat}/{safeName}_Clouds.mat";
                Material bodyMat  = CreateOrReplaceBodyMaterial(colorCm, normalCm, cloudCm, bodyMatPath);
                Material cloudMat = cloudsEnabled ? CreateOrReplaceCloudMaterial(cloudCm, cloudMatPath) : null;

                string prefabPath = $"{relRoot}/{safeName}.prefab";
                if (AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) != null)
                    AssetDatabase.DeleteAsset(prefabPath);

                GameObject tempRoot = BuildPlanetPrefab(safeName, lodMeshes, bodyMat, cloudMat);
                PrefabUtility.SaveAsPrefabAsset(tempRoot, prefabPath);
                DestroyImmediate(tempRoot);

                File.WriteAllText(Path.Combine(absRoot, "params.json"),
                    JsonUtility.ToJson(ExportConfig(), prettyPrint: true));

                AssetDatabase.Refresh();

                var prefabObj = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                if (prefabObj != null)
                {
                    EditorGUIUtility.PingObject(prefabObj);
                    Selection.activeObject = prefabObj;
                }

                Debug.Log($"[PlanetGenerator] Saved '{safeName}' at {size}px → {relRoot}");
            }
            finally
            {
                ReleaseRT(ref colorArr); ReleaseRT(ref colorCube);
                ReleaseRT(ref normalArr); ReleaseRT(ref cloudArr);
            }
        }

        void SaveRockyPlanet()
        {
            EnsureComputesLoaded();
            if (rockyCompute == null || normalCompute == null)
            {
                Debug.LogError("[PlanetGenerator] Rocky/Normal compute shaders failed to load.");
                return;
            }

            string safeName = SanitizeFileName(planetName);
            string relRoot  = $"{LibraryRoot}/{safeName}";
            string absRoot  = Path.Combine(Directory.GetCurrentDirectory(), relRoot);
            string relTex   = $"{relRoot}/Textures";
            string relMat   = $"{relRoot}/Materials";

            Directory.CreateDirectory(Path.Combine(absRoot, "Textures"));
            Directory.CreateDirectory(Path.Combine(absRoot, "Materials"));

            int size = exportResolution;
            int groups = Mathf.CeilToInt(size / 8f);

            RenderTexture colorArr  = AllocExportRT(size, TextureDimension.Tex2DArray, true,  RenderTextureFormat.ARGBHalf);
            RenderTexture colorCube = AllocExportRT(size, TextureDimension.Cube,       false, RenderTextureFormat.ARGBHalf);
            RenderTexture normalArr = AllocExportRT(size, TextureDimension.Tex2DArray, true);

            try
            {
                int kR = rockyCompute.FindKernel("CSMain");
                SetRockyUniforms(rockyCompute, kR, colorArr, size);
                rockyCompute.Dispatch(kR, groups, groups, 6);
                for (int f = 0; f < 6; f++) Graphics.CopyTexture(colorArr, f, 0, colorCube, f, 0);

                int kN = normalCompute.FindKernel("CSMain");
                normalCompute.SetTexture(kN, "_HeightCube", colorCube);
                normalCompute.SetTexture(kN, "_NormalOutput", normalArr);
                normalCompute.SetInt("_Size", size);
                normalCompute.SetFloat("_HeightScale", rkHeightScale);
                normalCompute.Dispatch(kN, groups, groups, 6);

                string colorPngRel  = $"{relTex}/color.png";
                string normalPngRel = $"{relTex}/normal.png";

                WriteCubemapStripPng(colorArr,  Path.Combine(Directory.GetCurrentDirectory(), colorPngRel));
                WriteCubemapStripPng(normalArr, Path.Combine(Directory.GetCurrentDirectory(), normalPngRel));

                // Legacy cleanup: rocky planets have no clouds. Remove any leftovers.
                string oldCloudPng = $"{relTex}/cloud.png";
                if (AssetDatabase.LoadAssetAtPath<Texture>(oldCloudPng) != null)
                    AssetDatabase.DeleteAsset(oldCloudPng);
                string oldCloudMat = $"{relMat}/{safeName}_Clouds.mat";
                if (AssetDatabase.LoadAssetAtPath<Material>(oldCloudMat) != null)
                    AssetDatabase.DeleteAsset(oldCloudMat);

                AssetDatabase.Refresh();

                var colorCm  = LoadCubemapPng(colorPngRel);
                var normalCm = LoadCubemapPng(normalPngRel);

                Mesh[] lodMeshes = EnsureSharedLodMeshes();

                string bodyMatPath = $"{relMat}/{safeName}_Body.mat";
                Material bodyMat = CreateOrReplaceBodyMaterial(colorCm, normalCm, null, bodyMatPath);

                // Detail normal lives on the material (sampled at runtime by
                // the shader, not baked into the planet's normal cubemap).
                // Apply after CreateAsset, then SetDirty so the binding is
                // persisted to disk on the next SaveAssets.
                ApplyDetailNormalToMaterial(bodyMat, rkDetailNormalEnabled && rkDetailNormalMap != null);
                EditorUtility.SetDirty(bodyMat);
                AssetDatabase.SaveAssets();

                string prefabPath = $"{relRoot}/{safeName}.prefab";
                if (AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) != null)
                    AssetDatabase.DeleteAsset(prefabPath);

                GameObject tempRoot = BuildPlanetPrefab(safeName, lodMeshes, bodyMat, null);
                PrefabUtility.SaveAsPrefabAsset(tempRoot, prefabPath);
                DestroyImmediate(tempRoot);

                File.WriteAllText(Path.Combine(absRoot, "params.json"),
                    JsonUtility.ToJson(ExportRockyConfig(), prettyPrint: true));

                AssetDatabase.Refresh();

                var prefabObj = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                if (prefabObj != null)
                {
                    EditorGUIUtility.PingObject(prefabObj);
                    Selection.activeObject = prefabObj;
                }

                Debug.Log($"[PlanetGenerator] Saved rocky '{safeName}' at {size}px → {relRoot}");
            }
            finally
            {
                ReleaseRT(ref colorArr); ReleaseRT(ref colorCube); ReleaseRT(ref normalArr);
            }
        }

        void SaveGasGiantPlanet()
        {
            EnsureComputesLoaded();
            if (gasGiantCompute == null || normalCompute == null)
            {
                Debug.LogError("[PlanetGenerator] GasGiant/Normal compute shaders failed to load.");
                return;
            }

            string safeName = SanitizeFileName(planetName);
            string relRoot  = $"{LibraryRoot}/{safeName}";
            string absRoot  = Path.Combine(Directory.GetCurrentDirectory(), relRoot);
            string relTex   = $"{relRoot}/Textures";
            string relMat   = $"{relRoot}/Materials";

            Directory.CreateDirectory(Path.Combine(absRoot, "Textures"));
            Directory.CreateDirectory(Path.Combine(absRoot, "Materials"));

            int size = exportResolution;
            int groups = Mathf.CeilToInt(size / 8f);

            bool wantNormal = ggHeightScale > 0f;
            RenderTexture colorArr  = AllocExportRT(size, TextureDimension.Tex2DArray, true,  RenderTextureFormat.ARGBHalf);
            RenderTexture colorCube = wantNormal ? AllocExportRT(size, TextureDimension.Cube, false, RenderTextureFormat.ARGBHalf) : null;
            RenderTexture normalArr = wantNormal ? AllocExportRT(size, TextureDimension.Tex2DArray, true) : null;

            try
            {
                int kG = gasGiantCompute.FindKernel("CSMain");
                SetGasGiantUniforms(gasGiantCompute, kG, colorArr, size);
                gasGiantCompute.Dispatch(kG, groups, groups, 6);

                if (wantNormal)
                {
                    for (int f = 0; f < 6; f++) Graphics.CopyTexture(colorArr, f, 0, colorCube, f, 0);
                    int kN = normalCompute.FindKernel("CSMain");
                    normalCompute.SetTexture(kN, "_HeightCube", colorCube);
                    normalCompute.SetTexture(kN, "_NormalOutput", normalArr);
                    normalCompute.SetInt("_Size", size);
                    normalCompute.SetFloat("_HeightScale", ggHeightScale);
                    normalCompute.Dispatch(kN, groups, groups, 6);
                }

                string colorPngRel  = $"{relTex}/color.png";
                string normalPngRel = $"{relTex}/normal.png";

                WriteCubemapStripPng(colorArr, Path.Combine(Directory.GetCurrentDirectory(), colorPngRel));
                if (wantNormal) WriteCubemapStripPng(normalArr, Path.Combine(Directory.GetCurrentDirectory(), normalPngRel));

                string oldCloudPng = $"{relTex}/cloud.png";
                if (AssetDatabase.LoadAssetAtPath<Texture>(oldCloudPng) != null)
                    AssetDatabase.DeleteAsset(oldCloudPng);
                string oldCloudMat = $"{relMat}/{safeName}_Clouds.mat";
                if (AssetDatabase.LoadAssetAtPath<Material>(oldCloudMat) != null)
                    AssetDatabase.DeleteAsset(oldCloudMat);
                if (!wantNormal)
                {
                    string oldNormal = $"{relTex}/normal.png";
                    if (AssetDatabase.LoadAssetAtPath<Texture>(oldNormal) != null)
                        AssetDatabase.DeleteAsset(oldNormal);
                }

                AssetDatabase.Refresh();

                var colorCm  = LoadCubemapPng(colorPngRel);
                var normalCm = wantNormal ? LoadCubemapPng(normalPngRel) : null;

                Mesh[] lodMeshes = EnsureSharedLodMeshes();

                string bodyMatPath = $"{relMat}/{safeName}_Body.mat";
                Material bodyMat = CreateOrReplaceBodyMaterial(colorCm, normalCm, null, bodyMatPath);

                string prefabPath = $"{relRoot}/{safeName}.prefab";
                if (AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) != null)
                    AssetDatabase.DeleteAsset(prefabPath);

                GameObject tempRoot = BuildPlanetPrefab(safeName, lodMeshes, bodyMat, null);
                PrefabUtility.SaveAsPrefabAsset(tempRoot, prefabPath);
                DestroyImmediate(tempRoot);

                File.WriteAllText(Path.Combine(absRoot, "params.json"),
                    JsonUtility.ToJson(ExportGasConfig(), prettyPrint: true));

                AssetDatabase.Refresh();

                var prefabObj = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                if (prefabObj != null)
                {
                    EditorGUIUtility.PingObject(prefabObj);
                    Selection.activeObject = prefabObj;
                }

                Debug.Log($"[PlanetGenerator] Saved gas giant '{safeName}' at {size}px → {relRoot}");
            }
            finally
            {
                ReleaseRT(ref colorArr); ReleaseRT(ref colorCube); ReleaseRT(ref normalArr);
            }
        }

        void SaveStarPlanet()
        {
            EnsureComputesLoaded();
            if (starCompute == null)
            {
                Debug.LogError("[PlanetGenerator] Star compute shader failed to load.");
                return;
            }

            string safeName = SanitizeFileName(planetName);
            string relRoot  = $"{LibraryRoot}/{safeName}";
            string absRoot  = Path.Combine(Directory.GetCurrentDirectory(), relRoot);
            string relTex   = $"{relRoot}/Textures";
            string relMat   = $"{relRoot}/Materials";

            Directory.CreateDirectory(Path.Combine(absRoot, "Textures"));
            Directory.CreateDirectory(Path.Combine(absRoot, "Materials"));

            int size = exportResolution;
            int groups = Mathf.CeilToInt(size / 8f);

            RenderTexture colorArr    = AllocExportRT(size, TextureDimension.Tex2DArray, true, RenderTextureFormat.ARGBHalf);
            RenderTexture emissionArr = AllocExportRT(size, TextureDimension.Tex2DArray, true, RenderTextureFormat.ARGB32);

            try
            {
                int kS = starCompute.FindKernel("CSMain");
                SetStarUniformsForExport(kS, colorArr, emissionArr, size);
                starCompute.Dispatch(kS, groups, groups, 6);

                string colorPngRel    = $"{relTex}/color.png";
                string emissionPngRel = $"{relTex}/emission.png";

                WriteCubemapStripPng(colorArr,    Path.Combine(Directory.GetCurrentDirectory(), colorPngRel));
                WriteCubemapStripPng(emissionArr, Path.Combine(Directory.GetCurrentDirectory(), emissionPngRel));

                string[] legacy = {
                    $"{relTex}/normal.png", $"{relTex}/cloud.png",
                    $"{relMat}/{safeName}_Clouds.mat",
                };
                foreach (var p in legacy)
                    if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(p) != null)
                        AssetDatabase.DeleteAsset(p);

                AssetDatabase.Refresh();

                var colorCm    = LoadCubemapPng(colorPngRel);
                var emissionCm = LoadCubemapPng(emissionPngRel);

                Mesh[] lodMeshes = EnsureSharedLodMeshes();

                string bodyMatPath = $"{relMat}/{safeName}_Body.mat";
                Material bodyMat = CreateOrReplaceStarMaterial(colorCm, emissionCm, bodyMatPath);

                string prefabPath = $"{relRoot}/{safeName}.prefab";
                if (AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) != null)
                    AssetDatabase.DeleteAsset(prefabPath);

                GameObject tempRoot = BuildPlanetPrefab(safeName, lodMeshes, bodyMat, null);
                PrefabUtility.SaveAsPrefabAsset(tempRoot, prefabPath);
                DestroyImmediate(tempRoot);

                File.WriteAllText(Path.Combine(absRoot, "params.json"),
                    JsonUtility.ToJson(ExportStarConfig(), prettyPrint: true));

                AssetDatabase.Refresh();

                var prefabObj = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                if (prefabObj != null)
                {
                    EditorGUIUtility.PingObject(prefabObj);
                    Selection.activeObject = prefabObj;
                }

                Debug.Log($"[PlanetGenerator] Saved star '{safeName}' at {size}px → {relRoot}");
            }
            finally
            {
                ReleaseRT(ref colorArr); ReleaseRT(ref emissionArr);
            }
        }

        void SetStarUniformsForExport(int kernel, RenderTexture colorOut, RenderTexture emissionOut, int size)
        {
            EnsureSpotBuffer(out int spotCount);

            starCompute.SetTexture(kernel, "_ColorOutput", colorOut);
            starCompute.SetTexture(kernel, "_EmissionOutput", emissionOut);
            starCompute.SetBuffer(kernel, "_Spots", spotBuffer);
            starCompute.SetInt("_Size", size);
            starCompute.SetInt("_Seed", seed);
            starCompute.SetInt("_SpotCount", spotCount);
            starCompute.SetFloat("_Warp", stWarp);
            starCompute.SetFloat("_MacroFrequency", stMacroFrequency);
            starCompute.SetInt("_MacroOctaves", stMacroOctaves);
            starCompute.SetFloat("_MacroLacunarity", stMacroLacunarity);
            starCompute.SetFloat("_MacroGain", stMacroGain);
            starCompute.SetFloat("_GranuleFrequency", stGranuleFrequency);
            starCompute.SetInt("_GranuleOctaves", stGranuleOctaves);
            starCompute.SetFloat("_GranuleContrast", stGranuleContrast);
            starCompute.SetFloat("_EmissionFloor", stEmissionFloor);
            starCompute.SetFloat("_EmissionScale", stEmissionScale);
            starCompute.SetFloat("_SpotEmissionDarken", stSpotEmissionDarken);
            var palette = new Vector4[5];
            palette[0] = stPalette0; palette[1] = stPalette1; palette[2] = stPalette2;
            palette[3] = stPalette3; palette[4] = stPalette4;
            starCompute.SetVectorArray("_Palette", palette);
        }

        Material CreateOrReplaceStarMaterial(Cubemap color, Cubemap emission, string path)
        {
            if (AssetDatabase.LoadAssetAtPath<Material>(path) != null) AssetDatabase.DeleteAsset(path);

            var shader = Shader.Find(StarShaderName);
            var mat = new Material(shader);

            if (color != null)
            {
                mat.SetTexture("_BaseCube", color);
                SetToggleKeyword(mat, "_UseStarCube", "_USE_STAR_CUBE", true);
            }
            if (emission != null)
            {
                mat.SetTexture("_EmissionCube", emission);
                SetToggleKeyword(mat, "_UseEmissionCube", "_USE_EMISSION_CUBE", true);
            }
            mat.SetColor("_BaseColor",    stBaseTint);
            mat.SetFloat("_EmissionFloor", stMaterialEmissionFloor);
            mat.SetFloat("_EmissionBoost", stMaterialEmissionBoost);

            AssetDatabase.CreateAsset(mat, path);
            return mat;
        }

        static string SanitizeFileName(string input)
        {
            string s = string.IsNullOrWhiteSpace(input) ? "Planet" : input.Trim();
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s;
        }

        void EnsureComputesLoaded()
        {
            if (terrestrialCompute == null)
                terrestrialCompute = AssetDatabase.LoadAssetAtPath<ComputeShader>(TerrestrialComputePath);
            if (rockyCompute == null)
                rockyCompute = AssetDatabase.LoadAssetAtPath<ComputeShader>(RockyComputePath);
            if (gasGiantCompute == null)
                gasGiantCompute = AssetDatabase.LoadAssetAtPath<ComputeShader>(GasGiantComputePath);
            if (starCompute == null)
                starCompute = AssetDatabase.LoadAssetAtPath<ComputeShader>(StarComputePath);
            if (normalCompute == null)
                normalCompute = AssetDatabase.LoadAssetAtPath<ComputeShader>(NormalComputePath);
            if (cloudsCompute == null)
                cloudsCompute = AssetDatabase.LoadAssetAtPath<ComputeShader>(CloudsComputePath);
        }

        static RenderTexture AllocExportRT(int size, TextureDimension dim, bool randomWrite,
            RenderTextureFormat format = RenderTextureFormat.ARGB32)
        {
            var rt = new RenderTexture(size, size, 0, format, RenderTextureReadWrite.Linear)
            {
                dimension = dim, volumeDepth = 6,
                enableRandomWrite = randomWrite,
                useMipMap = false, autoGenerateMips = false,
                hideFlags = HideFlags.DontSave,
                name = $"PlanetGen_Export_{dim}",
            };
            rt.Create();
            return rt;
        }

        static void ReleaseRT(ref RenderTexture rt)
        {
            if (rt == null) return;
            rt.Release();
            DestroyImmediate(rt);
            rt = null;
        }

        // Build the 3×3 Whittaker biome grid as Vector4 array. Index = 3*T + P
        // (T: 0=cold, 1=mild, 2=hot; P: 0=dry, 1=mid, 2=wet). See compute
        // shader for the layout / colour assignments.
        Vector4[] BuildBiomeGrid()
        {
            var g = new Vector4[9];
            g[0] = tundra;  g[1] = tundra;  g[2] = taiga;   // cold row
            g[3] = desert;  g[4] = grass;   g[5] = forest;  // mild row
            g[6] = desert;  g[7] = savanna; g[8] = jungle;  // hot row
            return g;
        }

        void SetTerrestrialUniforms(ComputeShader cs, int size)
        {
            cs.SetInt("_Size", size);
            cs.SetInt("_Seed", seed);
            cs.SetFloat("_ContinentFrequency", continentFrequency);
            cs.SetInt("_ContinentOctaves", continentOctaves);
            cs.SetFloat("_ContinentLacunarity", continentLacunarity);
            cs.SetFloat("_ContinentGain", continentGain);
            cs.SetFloat("_WarpStrength", warpStrength);
            cs.SetFloat("_SeaLevel", seaLevel);
            cs.SetFloat("_ElevationAmplitude", elevationAmplitude);
            cs.SetFloat("_MountainStrength", mountainStrength);
            cs.SetFloat("_MountainStart", mountainStart);
            cs.SetFloat("_MountainFull", mountainFull);
            cs.SetFloat("_BiomeContrast", Mathf.Max(0.1f, biomeContrast));
            cs.SetFloat("_MoistureFrequency", moistureFrequency);
            cs.SetInt("_MoistureOctaves", moistureOctaves);

            // Climate model
            cs.SetFloat("_AltitudeCooling",   altitudeCooling);
            cs.SetFloat("_TempNoiseFreq",     tempNoiseFreq);
            cs.SetFloat("_TempNoiseStrength", tempNoiseStrength);
            cs.SetFloat("_HadleyStrength",    hadleyStrength);
            cs.SetFloat("_SnowTempThreshold", snowTempThreshold);
            cs.SetFloat("_SnowTempBlend",     Mathf.Max(0.001f, snowTempBlend));

            // Polar override
            cs.SetInt  ("_PolesEnabled",      polesEnabled ? 1 : 0);
            cs.SetFloat("_PoleLatitude",      poleLatitude);
            cs.SetFloat("_PoleBlendWidth",    poleBlendWidth);
            cs.SetFloat("_PoleNoiseStrength", poleNoiseStrength);

            // Palette
            cs.SetVectorArray("_BiomeGrid", BuildBiomeGrid());
            cs.SetVector("_OceanDeepColor",    oceanDeep);
            cs.SetVector("_OceanShallowColor", oceanShallow);
            cs.SetVector("_BeachColor",        beach);
            cs.SetVector("_MountainColor",     mountain);
            cs.SetVector("_SnowColor",         snow);
            cs.SetVector("_PolarColor",        polar);
        }

        static Vector3 RandomUnitSphere(System.Random rng)
        {
            while (true)
            {
                float u = (float)(rng.NextDouble() * 2.0 - 1.0);
                float v = (float)(rng.NextDouble() * 2.0 - 1.0);
                float s = u * u + v * v;
                if (s >= 1f || s <= 1e-6f) continue;
                float f = 2f * Mathf.Sqrt(1f - s);
                return new Vector3(u * f, v * f, 1f - 2f * s);
            }
        }

        void SetRockyUniforms(ComputeShader cs, int kernel, RenderTexture output, int size)
        {
            cs.SetTexture(kernel, "_ColorOutput", output);
            cs.SetInt("_Size", size);
            cs.SetInt("_Seed", seed);

            cs.SetFloat("_BaseFrequency", rkBaseFrequency);
            cs.SetInt("_BaseOctaves", rkBaseOctaves);
            cs.SetFloat("_BaseLacunarity", rkBaseLacunarity);
            cs.SetFloat("_BaseGain", rkBaseGain);
            cs.SetFloat("_BaseAmplitude", rkBaseAmplitude);
            cs.SetFloat("_BaseWarp", rkBaseWarp);

            cs.SetInt("_MareEnabled", rkMareEnabled ? 1 : 0);
            cs.SetFloat("_MareFrequency", rkMareFrequency);
            cs.SetFloat("_MareCoverage", rkMareCoverage);
            cs.SetFloat("_MareSoftness", rkMareSoftness);
            cs.SetFloat("_MareFlatten", rkMareFlatten);
            cs.SetFloat("_MareDepth", rkMareDepth);

            cs.SetFloat("_DustFrequency", rkDustFrequency);
            cs.SetFloat("_DustStrength", rkDustStrength);

            cs.SetInt("_PolesEnabled", rkPolesEnabled ? 1 : 0);
            cs.SetFloat("_PoleLatitude", rkPoleLatitude);
            cs.SetFloat("_PoleBlendWidth", rkPoleBlendWidth);
            cs.SetFloat("_PoleNoiseStrength", rkPoleNoiseStrength);

            // Detail texture (triplanar). Texture2D ref must always be bound —
            // even when disabled — so the shader's sampler is in a valid
            // state. We feed a 1x1 white default if the user hasn't dropped
            // anything in. The _UseDetailMap int gates the sampling work.
            bool detailActive = rkDetailEnabled && rkDetailMap != null;
            cs.SetInt("_UseDetailMap", detailActive ? 1 : 0);
            cs.SetTexture(kernel, "_DetailMap", detailActive ? (Texture)rkDetailMap : Texture2D.whiteTexture);
            cs.SetFloat("_DetailTiling",   rkDetailTiling);
            cs.SetFloat("_DetailOffsetU",  rkDetailOffset.x);
            cs.SetFloat("_DetailOffsetV",  rkDetailOffset.y);
            cs.SetFloat("_DetailStrength", rkDetailStrength);
            cs.SetFloat("_DetailBlendSharpness", Mathf.Max(1f, rkDetailBlendSharpness));

            // Height range: only base noise + optional mare drop contribute now.
            float hMin = -rkBaseAmplitude - (rkMareEnabled ? rkMareDepth : 0f) - 0.05f;
            float hMax =  rkBaseAmplitude + 0.05f;
            cs.SetFloat("_HeightRangeMin", hMin);
            cs.SetFloat("_HeightRangeMax", hMax);

            cs.SetVector("_HighlandColor", rkHighland);
            cs.SetVector("_MareColor",     rkMare);
            cs.SetVector("_DustColor",     rkDust);
            cs.SetVector("_PolarColor",    rkPolar);
        }

        [StructLayout(LayoutKind.Sequential)]
        struct StormGpu
        {
            public Vector4 center;
            public Vector4 minor;
            public Vector4 tint;
        }

        void EnsureStormBuffer(out int count)
        {
            var storms = new List<StormGpu>(ggBigStormCount + ggSmallStormCount);
            var rng = new System.Random(seed ^ unchecked((int)0x5701A000));

            AppendStormTier(storms, rng, ggBigStormCount,
                ggBigStormRadius, 0.35f, ggBigStormIntensity, ggBigStormSwirl, ggBigStormTint);
            AppendStormTier(storms, rng, ggSmallStormCount,
                ggSmallStormRadius, 0.25f, ggSmallStormIntensity, ggSmallStormSwirl, ggSmallStormTint);

            count = storms.Count;
            int bufferLen = Mathf.Max(count, 1);
            if (stormBuffer == null || stormBuffer.count != bufferLen)
            {
                stormBuffer?.Release();
                stormBuffer = new ComputeBuffer(bufferLen, Marshal.SizeOf<StormGpu>());
            }
            if (count == 0) stormBuffer.SetData(new[] { default(StormGpu) });
            else            stormBuffer.SetData(storms);
        }

        static void AppendStormTier(List<StormGpu> list, System.Random rng,
            int count, float radiusMean, float radiusJitter,
            float intensity, float swirl, Color tint)
        {
            for (int i = 0; i < count; i++)
            {
                float lat = (float)(rng.NextDouble() * 1.3 - 0.65);
                float lon = (float)(rng.NextDouble() * 2.0 * Mathf.PI);
                float cosLat = Mathf.Cos(lat * Mathf.PI * 0.5f);
                Vector3 center = new Vector3(
                    cosLat * Mathf.Cos(lon),
                    Mathf.Sin(lat * Mathf.PI * 0.5f),
                    cosLat * Mathf.Sin(lon)).normalized;

                float jitter = 1f + ((float)rng.NextDouble() * 2f - 1f) * radiusJitter;
                float majorR = Mathf.Max(0.005f, radiusMean * jitter);
                float aspect = 2.5f + (float)rng.NextDouble() * 1.5f;
                float minorR = majorR / aspect;
                float swirlV = swirl * (0.7f + (float)rng.NextDouble() * 0.6f);

                list.Add(new StormGpu
                {
                    center = new Vector4(center.x, center.y, center.z, majorR),
                    minor  = new Vector4(minorR, swirlV, intensity, 0f),
                    tint   = new Vector4(tint.r, tint.g, tint.b, 1f),
                });
            }
        }

        void SetGasGiantUniforms(ComputeShader cs, int kernel, RenderTexture output, int size)
        {
            EnsureStormBuffer(out int stormCount);

            cs.SetTexture(kernel, "_ColorOutput", output);
            cs.SetBuffer(kernel, "_Storms", stormBuffer);
            cs.SetInt("_Size", size);
            cs.SetInt("_Seed", seed);
            cs.SetInt("_StormCount", stormCount);

            cs.SetFloat("_BandStretch", ggBandStretch);
            cs.SetFloat("_BandFrequency", ggBandFrequency);
            cs.SetInt("_BandOctaves", ggBandOctaves);
            cs.SetFloat("_BandLacunarity", ggBandLacunarity);
            cs.SetFloat("_BandGain", ggBandGain);
            cs.SetFloat("_BandContrast", ggBandContrast);
            cs.SetFloat("_BandRepetition", ggBandRepetition);
            cs.SetFloat("_BandLatShift", ggBandLatShift);
            cs.SetFloat("_BandWarp", ggBandWarp);

            cs.SetFloat("_FlowStrength", ggFlowStrength);
            cs.SetFloat("_FlowFrequency", ggFlowFrequency);
            cs.SetInt("_FlowOctaves", ggFlowOctaves);
            cs.SetFloat("_CurlStrength", ggCurlStrength);

            cs.SetFloat("_DetailFrequency", ggDetailFrequency);
            cs.SetInt("_DetailOctaves", ggDetailOctaves);
            cs.SetFloat("_DetailContrast", ggDetailContrast);

            cs.SetFloat("_StormNoiseFrequency", ggStormNoiseFrequency);

            cs.SetFloat("_PoleLatitude", ggPoleLatitude);
            cs.SetFloat("_PoleDarken", ggPoleDarken);
            cs.SetFloat("_HeightScale", ggHeightScale);

            var palette = new Vector4[6];
            palette[0] = ggPalette0; palette[1] = ggPalette1; palette[2] = ggPalette2;
            palette[3] = ggPalette3; palette[4] = ggPalette4; palette[5] = ggPalette5;
            cs.SetVectorArray("_Palette", palette);
        }

        [StructLayout(LayoutKind.Sequential)]
        struct SunSpotGpu
        {
            public Vector4 centerRadius;
            public Vector4 params_;
        }

        void EnsureSpotBuffer(out int count)
        {
            var spots = new List<SunSpotGpu>(stSpotCount);
            var rng = new System.Random(seed ^ unchecked((int)0x5A01E900));

            for (int i = 0; i < stSpotCount; i++)
            {
                Vector3 center = RandomUnitSphere(rng);
                float jitter = 0.6f + (float)rng.NextDouble() * 0.8f;
                float majorR = Mathf.Max(0.005f, stSpotRadius * jitter);
                float aspect = 1.0f + (float)rng.NextDouble() * 0.8f;
                float minorR = majorR / aspect;
                float softV = Mathf.Clamp01(stSpotSoftness * (0.8f + (float)rng.NextDouble() * 0.4f));

                spots.Add(new SunSpotGpu
                {
                    centerRadius = new Vector4(center.x, center.y, center.z, majorR),
                    params_      = new Vector4(minorR, stSpotStrength, softV, 0f),
                });
            }

            count = spots.Count;
            int bufferLen = Mathf.Max(count, 1);
            if (spotBuffer == null || spotBuffer.count != bufferLen)
            {
                spotBuffer?.Release();
                spotBuffer = new ComputeBuffer(bufferLen, Marshal.SizeOf<SunSpotGpu>());
            }
            if (count == 0) spotBuffer.SetData(new[] { default(SunSpotGpu) });
            else            spotBuffer.SetData(spots);
        }

        void SetStarUniforms(ComputeShader cs, int kernel, RenderTexture colorOut, int size)
        {
            EnsureSpotBuffer(out int spotCount);

            cs.SetTexture(kernel, "_ColorOutput", colorOut);
            cs.SetTexture(kernel, "_EmissionOutput", emissionArrayRT);
            cs.SetBuffer(kernel, "_Spots", spotBuffer);
            cs.SetInt("_Size", size);
            cs.SetInt("_Seed", seed);
            cs.SetInt("_SpotCount", spotCount);

            cs.SetFloat("_Warp", stWarp);
            cs.SetFloat("_MacroFrequency", stMacroFrequency);
            cs.SetInt("_MacroOctaves", stMacroOctaves);
            cs.SetFloat("_MacroLacunarity", stMacroLacunarity);
            cs.SetFloat("_MacroGain", stMacroGain);

            cs.SetFloat("_GranuleFrequency", stGranuleFrequency);
            cs.SetInt("_GranuleOctaves", stGranuleOctaves);
            cs.SetFloat("_GranuleContrast", stGranuleContrast);

            cs.SetFloat("_EmissionFloor", stEmissionFloor);
            cs.SetFloat("_EmissionScale", stEmissionScale);
            cs.SetFloat("_SpotEmissionDarken", stSpotEmissionDarken);

            var palette = new Vector4[5];
            palette[0] = stPalette0; palette[1] = stPalette1; palette[2] = stPalette2;
            palette[3] = stPalette3; palette[4] = stPalette4;
            cs.SetVectorArray("_Palette", palette);
        }

        void SetCloudsUniforms(ComputeShader cs, int size)
        {
            cs.SetInt("_Size", size);
            cs.SetInt("_Seed", (int)((uint)seed ^ 0x9E17Ca11u));
            cs.SetFloat("_CloudFrequency", cloudFrequency);
            cs.SetInt("_CloudOctaves", cloudOctaves);
            cs.SetFloat("_CloudLacunarity", cloudLacunarity);
            cs.SetFloat("_CloudGain", cloudGain);
            cs.SetFloat("_WarpStrength", cloudWarpStrength);
            cs.SetFloat("_Coverage", cloudCoverage);
            cs.SetFloat("_Softness", cloudSoftness);
            cs.SetFloat("_DetailStrength", cloudDetailStrength);
            cs.SetVector("_CloudColor", cloudColor);
        }

        // Writes the 6 slices of a Tex2DArray RT as a horizontal 4:3 cross PNG.
        //     .  +Y  .  .
        //     -X +Z +X -Z
        //     .  -Y  .  .
        // Each cell is size×size; image is (4*size) × (3*size). Unity's
        // FullCubemap auto-detect picks this layout from the 4:3 aspect.
        void WriteCubemapStripPng(RenderTexture sourceArrayRT, string absPath)
        {
            if (sourceArrayRT == null) return;

            int size = sourceArrayRT.width;
            int stripW = size * 4;
            int stripH = size * 3;

            var tempRT = RenderTexture.GetTemporary(size, size, 0,
                sourceArrayRT.format, RenderTextureReadWrite.Linear);
            var faceFmt = sourceArrayRT.format == RenderTextureFormat.ARGBHalf
                ? TextureFormat.RGBAHalf : TextureFormat.RGBA32;
            var face  = new Texture2D(size, size,   faceFmt,              false, true);
            var strip = new Texture2D(stripW, stripH, TextureFormat.RGBA32, false, true);

            var placements = new (int f, int col, int row)[]
            {
                (0, 2, 1), (1, 0, 1),
                (2, 1, 0), (3, 1, 2),
                (4, 1, 1), (5, 3, 1),
            };

            var prevActive = RenderTexture.active;
            try
            {
                var black = new Color32[size * size];
                var usedMask = new bool[4, 3];
                foreach (var p in placements) usedMask[p.col, p.row] = true;
                for (int row = 0; row < 3; row++)
                for (int col = 0; col < 4; col++)
                {
                    if (usedMask[col, row]) continue;
                    strip.SetPixels32(col * size, (2 - row) * size, size, size, black);
                }

                var flipped = new Color32[size * size];
                foreach (var (f, col, row) in placements)
                {
                    Graphics.CopyTexture(sourceArrayRT, f, 0, tempRT, 0, 0);
                    RenderTexture.active = tempRT;
                    face.ReadPixels(new Rect(0, 0, size, size), 0, 0);
                    face.Apply();

                    var src = face.GetPixels32();
                    for (int y = 0; y < size; y++)
                        System.Array.Copy(src, y * size, flipped, (size - 1 - y) * size, size);

                    int px = col * size;
                    int py = (2 - row) * size;
                    strip.SetPixels32(px, py, size, size, flipped);
                }
                strip.Apply();
                File.WriteAllBytes(absPath, strip.EncodeToPNG());
            }
            finally
            {
                RenderTexture.active = prevActive;
                RenderTexture.ReleaseTemporary(tempRT);
                DestroyImmediate(face);
                DestroyImmediate(strip);
            }
        }

        static Cubemap LoadCubemapPng(string relPath)
        {
            var cm = AssetDatabase.LoadAssetAtPath<Cubemap>(relPath);
            if (cm != null) return cm;

            var imp = AssetImporter.GetAtPath(relPath) as TextureImporter;
            if (imp != null)
            {
                imp.textureShape = TextureImporterShape.TextureCube;
                imp.generateCubemap = TextureImporterGenerateCubemap.FullCubemap;
                imp.SaveAndReimport();
                cm = AssetDatabase.LoadAssetAtPath<Cubemap>(relPath);
            }
            return cm;
        }

        Mesh[] EnsureSharedLodMeshes()
        {
            string abs = Path.Combine(Directory.GetCurrentDirectory(), SharedMeshFolder);
            Directory.CreateDirectory(abs);

            var subdivs = QuadSphereMesh.DefaultLodSubdivisions;
            var meshes  = new Mesh[subdivs.Length];
            bool created = false;
            for (int i = 0; i < subdivs.Length; i++)
            {
                string path = $"{SharedMeshFolder}/QuadSphere_LOD{i}.asset";
                var m = AssetDatabase.LoadAssetAtPath<Mesh>(path);
                if (m == null)
                {
                    m = QuadSphereMesh.Build(subdivs[i], 0.5f);
                    AssetDatabase.CreateAsset(m, path);
                    created = true;
                }
                meshes[i] = m;
            }
            if (created) AssetDatabase.SaveAssets();
            return meshes;
        }

        Material CreateOrReplaceBodyMaterial(Cubemap color, Cubemap normal, Cubemap cloud, string path)
        {
            if (AssetDatabase.LoadAssetAtPath<Material>(path) != null) AssetDatabase.DeleteAsset(path);

            var shader = Shader.Find(SurfaceShaderName);
            var mat = new Material(shader);

            if (color != null)
            {
                mat.SetTexture("_BaseCube", color);
                SetToggleKeyword(mat, "_UseBaseCube", "_USE_BASE_CUBE", true);
            }
            if (normal != null)
            {
                mat.SetTexture("_NormalCube", normal);
                SetToggleKeyword(mat, "_UseNormalCube", "_USE_NORMAL_CUBE", true);
            }
            if (cloud != null)
            {
                mat.SetTexture("_CloudCube", cloud);
                SetToggleKeyword(mat, "_UseCloudCube", "_USE_CLOUD_CUBE", true);
                mat.SetFloat("_CloudShadowStrength", cloudShadowStrength);
                mat.SetFloat("_CloudParallax",       cloudParallax);
            }

            AssetDatabase.CreateAsset(mat, path);
            return mat;
        }

        Material CreateOrReplaceCloudMaterial(Cubemap cloud, string path)
        {
            if (AssetDatabase.LoadAssetAtPath<Material>(path) != null) AssetDatabase.DeleteAsset(path);

            var shader = Shader.Find(CloudsShaderName);
            var mat = new Material(shader);
            mat.SetTexture("_CloudCube", cloud);
            mat.SetColor("_CloudTint", cloudColor);
            mat.SetFloat("_Density", cloudDensity);
            mat.SetFloat("_AmbientFloor", cloudAmbient);

            AssetDatabase.CreateAsset(mat, path);
            return mat;
        }

        static void SetToggleKeyword(Material mat, string propertyName, string keyword, bool value)
        {
            mat.SetFloat(propertyName, value ? 1f : 0f);
            if (value) mat.EnableKeyword(keyword);
            else mat.DisableKeyword(keyword);
        }

        // Push the rocky detail-normal params onto a material. Always sets the
        // shared mapping uniforms (tiling/offset/sharpness) so they're in a
        // known state, then toggles the _USE_DETAIL_NORMAL keyword based on
        // whether sampling should actually happen. Called on both the live
        // preview material and the exported body material.
        void ApplyDetailNormalToMaterial(Material mat, bool active)
        {
            mat.SetFloat("_DetailTiling",         rkDetailTiling);
            mat.SetFloat("_DetailOffsetU",        rkDetailOffset.x);
            mat.SetFloat("_DetailOffsetV",        rkDetailOffset.y);
            mat.SetFloat("_DetailBlendSharpness", Mathf.Max(1f, rkDetailBlendSharpness));
            mat.SetFloat("_DetailNormalStrength", rkDetailNormalStrength);
            mat.SetTexture("_DetailNormalMap", active ? (Texture)rkDetailNormalMap : Texture2D.normalTexture);
            SetToggleKeyword(mat, "_UseDetailNormal", "_USE_DETAIL_NORMAL", active);
        }

        // Builds the runtime prefab: MeshFilter+MeshRenderer for the body, an
        // optional child Clouds sphere scaled by cloudAltitude, optional LOD1/LOD2
        // children + a LODGroup. No custom components — drop the prefab into any
        // URP scene with a directional light and it just renders.
        GameObject BuildPlanetPrefab(string name, Mesh[] lodMeshes, Material bodyMat, Material cloudMat)
        {
            var root = new GameObject(name);

            var mf = root.AddComponent<MeshFilter>();
            mf.sharedMesh = lodMeshes[0];
            var mr = root.AddComponent<MeshRenderer>();
            mr.sharedMaterial = bodyMat;

            Renderer cloudRenderer = null;
            if (cloudMat != null)
            {
                var cloudChild = new GameObject("Clouds");
                cloudChild.transform.SetParent(root.transform, false);
                cloudChild.transform.localScale = Vector3.one * (1f + cloudAltitude);
                cloudChild.AddComponent<MeshFilter>().sharedMesh = lodMeshes[0];
                var cloudMrr = cloudChild.AddComponent<MeshRenderer>();
                cloudMrr.sharedMaterial = cloudMat;
                cloudRenderer = cloudMrr;
            }

            Renderer[] lod1Renderers = null;
            Renderer[] lod2Renderers = null;
            if (exportWithLods && lodMeshes.Length >= 3)
            {
                var lod1 = new GameObject("LOD1");
                lod1.transform.SetParent(root.transform, false);
                lod1.AddComponent<MeshFilter>().sharedMesh = lodMeshes[1];
                var lod1mr = lod1.AddComponent<MeshRenderer>();
                lod1mr.sharedMaterial = bodyMat;
                lod1mr.enabled = false;
                lod1Renderers = new Renderer[] { lod1mr };

                var lod2 = new GameObject("LOD2");
                lod2.transform.SetParent(root.transform, false);
                lod2.AddComponent<MeshFilter>().sharedMesh = lodMeshes[2];
                var lod2mr = lod2.AddComponent<MeshRenderer>();
                lod2mr.sharedMaterial = bodyMat;
                lod2mr.enabled = false;
                lod2Renderers = new Renderer[] { lod2mr };
            }

            if (exportWithLods && lod1Renderers != null)
            {
                var lodGroup = root.AddComponent<LODGroup>();
                var lods = new LOD[3];
                lods[0] = new LOD(0.30f, cloudRenderer != null ? new[] { mr, cloudRenderer } : new Renderer[] { mr });
                lods[1] = new LOD(0.08f, lod1Renderers);
                lods[2] = new LOD(0.01f, lod2Renderers);
                lodGroup.SetLODs(lods);
                lodGroup.RecalculateBounds();
            }

            return root;
        }

        void LoadPlanet()
        {
            string defaultFolder = Path.Combine(Directory.GetCurrentDirectory(), LibraryRoot);
            if (!Directory.Exists(defaultFolder)) defaultFolder = Application.dataPath;

            string path = EditorUtility.OpenFilePanel(
                "Load Planet (params.json)", defaultFolder, "json");
            if (string.IsNullOrEmpty(path)) return;

            try
            {
                string json = File.ReadAllText(path);

                var header = JsonUtility.FromJson<PlanetConfigHeader>(json);
                string type = header?.planetType ?? "";

                if (type == "rocky")
                {
                    var config = JsonUtility.FromJson<RockyPlanetConfig>(json);
                    if (config == null) { Debug.LogError("[PlanetGenerator] Invalid rocky params."); return; }
                    ImportRockyConfig(config);
                    mode = PlanetGenMode.Rocky;
                }
                else if (type == "gas")
                {
                    var config = JsonUtility.FromJson<GasPlanetConfig>(json);
                    if (config == null) { Debug.LogError("[PlanetGenerator] Invalid gas params."); return; }
                    ImportGasConfig(config);
                    mode = PlanetGenMode.GasGiant;
                }
                else if (type == "star")
                {
                    var config = JsonUtility.FromJson<StarConfig>(json);
                    if (config == null) { Debug.LogError("[PlanetGenerator] Invalid star params."); return; }
                    ImportStarConfig(config);
                    mode = PlanetGenMode.Star;
                }
                else
                {
                    var config = JsonUtility.FromJson<TerrestrialPlanetConfig>(json);
                    if (config == null) { Debug.LogError("[PlanetGenerator] Invalid terrestrial params."); return; }
                    ImportConfig(config);
                    mode = PlanetGenMode.Terrestrial;
                }

                Regenerate();
                Repaint();
                Debug.Log($"[PlanetGenerator] Loaded '{planetName}' from {path}");
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[PlanetGenerator] Failed to load planet: {e.Message}");
            }
        }

        bool DispatchNormalPass(float reliefScale)
        {
            if (normalCompute == null)
                normalCompute = AssetDatabase.LoadAssetAtPath<ComputeShader>(NormalComputePath);
            if (normalCompute == null)
            {
                Debug.LogError($"[PlanetGenerator] NormalFromHeight compute not found at '{NormalComputePath}'");
                return false;
            }

            int k = normalCompute.FindKernel("CSMain");
            normalCompute.SetTexture(k, "_HeightCube", cubeRT);
            normalCompute.SetTexture(k, "_NormalOutput", normalArrayRT);
            normalCompute.SetInt("_Size", cubemapSize);
            normalCompute.SetFloat("_HeightScale", reliefScale);

            int groups = Mathf.CeilToInt(cubemapSize / 8f);
            normalCompute.Dispatch(k, groups, groups, 6);

            for (int f = 0; f < 6; f++)
                Graphics.CopyTexture(normalArrayRT, f, 0, normalCubeRT, f, 0);

            return true;
        }
    }
}

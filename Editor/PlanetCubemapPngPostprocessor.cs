using System.IO;
using UnityEditor;

namespace Valtiel.PlanetGenerator.Editor
{
    // Forces PNGs exported by PlanetGeneratorWindow under
    // Assets/Procedural Planets Generator/Library/{Name}/Textures/ to be imported as
    // Cubemap (4:3 cross layout) on first import. Once imported, the user is
    // free to tweak mipmap/filter/size/compression on the PNG asset directly —
    // the material references the PNG itself, so any change propagates.
    //
    // Only first-import settings are enforced. After that, user edits to the
    // importer stick (importSettingsMissing == false).
    public sealed class PlanetCubemapPngPostprocessor : AssetPostprocessor
    {
        const string LibraryRoot = "Assets/Procedural Planets Generator/Library/";

        void OnPreprocessTexture()
        {
            if (!IsPlanetLibraryCubemapPng(assetPath)) return;

            var imp = (TextureImporter)assetImporter;
            if (!imp.importSettingsMissing) return;

            imp.textureShape    = TextureImporterShape.TextureCube;
            imp.generateCubemap = TextureImporterGenerateCubemap.FullCubemap;
            imp.sRGBTexture     = false;
            imp.alphaSource     = TextureImporterAlphaSource.FromInput;
            imp.alphaIsTransparency = false;
            imp.mipmapEnabled   = true;
            imp.wrapMode        = UnityEngine.TextureWrapMode.Clamp;
            imp.filterMode      = UnityEngine.FilterMode.Bilinear;
        }

        static bool IsPlanetLibraryCubemapPng(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            if (!path.StartsWith(LibraryRoot)) return false;
            if (!path.EndsWith(".png")) return false;
            string parent = Path.GetFileName(Path.GetDirectoryName(path));
            return parent == "Textures";
        }
    }
}

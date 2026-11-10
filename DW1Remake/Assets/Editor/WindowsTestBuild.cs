using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace DW1Remake.EditorTools
{
    /// <summary>
    /// Menu Build > Windows test build: a Windows 64-bit player for a friend to try (release plan, docs/RELEASE-PLAN.md).
    /// The build holds only our code, shaders and art; the player picks their own USA disc on first start.
    /// Our code finds its shaders by name (Shader.Find), and Unity leaves out of a build every shader no scene or Resources asset uses,
    /// so the build first writes one small "keep" material per shader (and per set of switches our code turns on) into
    /// Assets/Resources/BuildKeep. The company and product name are set for the build only and put back afterwards, so the
    /// editor's own saves and settings stay where they are.
    /// </summary>
    public static class WindowsTestBuild
    {
        const string KeepFolder = "Assets/Resources/BuildKeep";
        const string Company = "DW1RemakeFan", Product = "DW1 Remake Test";

        [MenuItem("Build/Windows test build")]
        public static void Build()
        {
            if (EditorApplication.isPlaying) { EditorUtility.DisplayDialog("Windows test build", "Stop Play first.", "OK"); return; }
            if (!WriteKeepMaterials()) return;

            string[] scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
            if (scenes.Length == 0) { EditorUtility.DisplayDialog("Windows test build", "No scene is ticked in Build Settings.", "OK"); return; }
            string outDir = Path.GetFullPath(Path.Combine(Application.dataPath, "../../Builds/DW1Remake-Windows"));
            if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
            Directory.CreateDirectory(outDir);

            string oldCompany = PlayerSettings.companyName, oldProduct = PlayerSettings.productName;
            var target = NamedBuildTarget.Standalone;
            var oldBackend = PlayerSettings.GetScriptingBackend(target);
            BuildReport report;
            try
            {
                PlayerSettings.companyName = Company;
                PlayerSettings.productName = Product;
                // IL2CPP when its Windows module is installed: the game code becomes machine code (no readable .dll), much harder to take apart.
                // It also needs Visual Studio's "Desktop development with C++"; without the module the Mono player is used
                bool il2cpp = Directory.Exists(Path.Combine(EditorApplication.applicationContentsPath, "PlaybackEngines/windowsstandalonesupport/Variations/win64_player_nondevelopment_il2cpp"));
                PlayerSettings.SetScriptingBackend(target, il2cpp ? ScriptingImplementation.IL2CPP : ScriptingImplementation.Mono2x);
                Debug.Log("[build] scripting backend: " + (il2cpp ? "IL2CPP" : "Mono"));
                report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = scenes,
                    locationPathName = Path.Combine(outDir, "DW1Remake.exe"),
                    target = BuildTarget.StandaloneWindows64,
                    options = BuildOptions.None,            // not a development build: test keys, debug menu and help line are left out
                });
            }
            finally
            {
                PlayerSettings.companyName = oldCompany;
                PlayerSettings.productName = oldProduct;
                PlayerSettings.SetScriptingBackend(target, oldBackend);
                // the build writes the project settings with the build's names: write them again with the editor's own
                AssetDatabase.SaveAssets();
            }

            var s = report.summary;
            if (s.result == BuildResult.Succeeded)
            {
                Debug.Log("[build] Windows test build done: " + outDir + " (" + (s.totalSize / (1024 * 1024)) + " MB, " + s.totalTime.TotalMinutes.ToString("0.0") + " min)");
                EditorUtility.RevealInFinder(Path.Combine(outDir, "DW1Remake.exe"));
            }
            else Debug.LogError("[build] Windows test build failed: " + s.result + ", " + s.totalErrors + " errors (see the Console above)");
        }

        // one material per shader our code asks for by name, with the switches (keywords) our code turns on, so their variants are built
        internal static bool WriteKeepMaterials()
        {
            if (!AssetDatabase.IsValidFolder("Assets/Resources")) AssetDatabase.CreateFolder("Assets", "Resources");
            if (!AssetDatabase.IsValidFolder(KeepFolder)) AssetDatabase.CreateFolder("Assets/Resources", "BuildKeep");

            string[] ours = { "DW1Remake/Backdrop", "DW1Remake/BlobShadow", "DW1Remake/Effect", "DW1Remake/GuiBlend", "DW1Remake/Half", "DW1Remake/Overlay", "DW1Remake/Psx", "DW1Remake/Unlit" };
            foreach (var name in ours) if (!Keep(name, name.Substring(name.IndexOf('/') + 1))) return false;
            // MmdModel.MakeMaterial (lit and unlit), the Finale's see-through boy, and the plain unlit / sprite materials
            if (!Keep("Universal Render Pipeline/Lit", "UrpLitCutout", "_ALPHATEST_ON", "_SPECULARHIGHLIGHTS_OFF", "_ENVIRONMENTREFLECTIONS_OFF")) return false;
            if (!Keep("Universal Render Pipeline/Lit", "UrpLitCutoutTransparent", "_ALPHATEST_ON", "_SPECULARHIGHLIGHTS_OFF", "_ENVIRONMENTREFLECTIONS_OFF", "_SURFACE_TYPE_TRANSPARENT")) return false;
            if (!Keep("Universal Render Pipeline/Unlit", "UrpUnlit")) return false;
            if (!Keep("Universal Render Pipeline/Unlit", "UrpUnlitCutout", "_ALPHATEST_ON")) return false;
            if (!Keep("Sprites/Default", "SpritesDefault")) return false;
            AssetDatabase.SaveAssets();
            return true;
        }

        static bool Keep(string shaderName, string file, params string[] keywords)
        {
            var sh = Shader.Find(shaderName);
            if (sh == null) { EditorUtility.DisplayDialog("Windows test build", "Shader not found: " + shaderName, "OK"); return false; }
            string path = KeepFolder + "/" + file.Replace('/', '_') + ".mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null) { m = new Material(sh); AssetDatabase.CreateAsset(m, path); }
            else m.shader = sh;
            m.shaderKeywords = keywords;
            if (keywords.Contains("_ALPHATEST_ON")) { m.SetFloat("_AlphaClip", 1f); m.SetFloat("_Cutoff", 0.5f); }
            if (keywords.Contains("_SURFACE_TYPE_TRANSPARENT")) m.SetFloat("_Surface", 1f);
            EditorUtility.SetDirty(m);
            return true;
        }
    }
}

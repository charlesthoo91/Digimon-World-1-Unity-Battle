using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace DW1Remake.EditorTools
{
    /// <summary>
    /// Menu Build > Web test build: a quick browser version to see whether the game runs well enough in a web page.
    /// The player picks their own USA disc with the browser's choose-file box (Plugins/WebGL/DW1Web.jslib); it is read into the page's
    /// memory only, never uploaded. HD-2D is off and online battle is PC only there (PlayableRoom.Start, ModSettings.OnlineWorks).
    /// Saves stay in the browser's own storage. When the build is done Unity serves it on this PC and opens the browser.
    /// The web settings changed for the build are put back afterwards, and so is the editor's build target.
    /// </summary>
    public static class WebTestBuild
    {
        const string Company = "DW1UnityBattle", Product = "DW1UnityBattle";

        [MenuItem("Build/Web test build")]
        public static void Build()
        {
            if (EditorApplication.isPlaying) { EditorUtility.DisplayDialog("Web test build", "Stop Play first.", "OK"); return; }
            if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.WebGL, BuildTarget.WebGL))
            { EditorUtility.DisplayDialog("Web test build", "The Web Build Support module is not installed (Unity Hub > Installs > Add modules).", "OK"); return; }
            if (!WindowsTestBuild.WriteKeepMaterials()) return;

            string[] scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
            if (scenes.Length == 0) { EditorUtility.DisplayDialog("Web test build", "No scene is ticked in Build Settings.", "OK"); return; }
            string outDir = Path.GetFullPath(Path.Combine(Application.dataPath, "../../Builds/DW1UnityBattle-Web"));
            if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
            Directory.CreateDirectory(outDir);

            var oldTarget = EditorUserBuildSettings.activeBuildTarget;
            var oldGroup = EditorUserBuildSettings.selectedBuildTargetGroup;
            string oldCompany = PlayerSettings.companyName, oldProduct = PlayerSettings.productName;
            var oldCompression = PlayerSettings.WebGL.compressionFormat;
            bool oldFallback = PlayerSettings.WebGL.decompressionFallback;
            var oldExceptions = PlayerSettings.WebGL.exceptionSupport;
            int oldMaxMemory = PlayerSettings.WebGL.maximumMemorySize;
            BuildReport report;
            try
            {
                PlayerSettings.companyName = Company;
                PlayerSettings.productName = Product;
                // packed files that any web server can hand out (the page unpacks them itself)
                PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Gzip;
                PlayerSettings.WebGL.decompressionFallback = true;
                // a caught error stays caught, as on Windows (the default would stop the whole page on the first one)
                PlayerSettings.WebGL.exceptionSupport = WebGLExceptionSupport.FullWithoutStacktrace;
                PlayerSettings.WebGL.maximumMemorySize = 4096;
                report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = scenes,
                    locationPathName = outDir,
                    target = BuildTarget.WebGL,
                    targetGroup = BuildTargetGroup.WebGL,
                    options = BuildOptions.AutoRunPlayer,      // Unity's own small web server on this PC, then the browser opens it
                });
            }
            finally
            {
                PlayerSettings.companyName = oldCompany;
                PlayerSettings.productName = oldProduct;
                PlayerSettings.WebGL.compressionFormat = oldCompression;
                PlayerSettings.WebGL.decompressionFallback = oldFallback;
                PlayerSettings.WebGL.exceptionSupport = oldExceptions;
                PlayerSettings.WebGL.maximumMemorySize = oldMaxMemory;
                AssetDatabase.SaveAssets();
                if (EditorUserBuildSettings.activeBuildTarget != oldTarget) EditorUserBuildSettings.SwitchActiveBuildTarget(oldGroup, oldTarget);
            }

            var s = report.summary;
            if (s.result == BuildResult.Succeeded)
                Debug.Log("[build] Web test build done: " + outDir + " (" + (s.totalSize / (1024 * 1024)) + " MB, " + s.totalTime.TotalMinutes.ToString("0.0") + " min)");
            else Debug.LogError("[build] Web test build failed: " + s.result + ", " + s.totalErrors + " errors (see the Console above)");
        }
    }
}

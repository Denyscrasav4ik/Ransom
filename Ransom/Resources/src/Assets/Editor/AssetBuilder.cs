using System.IO;
using UnityEditor;
using UnityEngine;

namespace Editor
{
    public class BuildAssetBundles : MonoBehaviour
    {
        private const string BuildMenuPath = "Assets/Build Assets";

        private const string BuildDirectory = "BuiltAssets";

        private const string AssetFolder = "Assets/Resources";

        [MenuItem(BuildMenuPath)]
        public static void BuildFromSelectedFolder()
        {
            if (!AssetDatabase.IsValidFolder(AssetFolder))
            {
                Debug.LogError("The selected item is not a folder. Please select a folder.");
                return;
            }

            Debug.Log("Starting asset bundle build...");

            BuildBundle("assets-win.bundle", BuildTarget.StandaloneWindows64);

            Debug.Log("Finished building WINDOWS asset bundle. Starting LINUX build...");

            BuildBundle("assets-linux.bundle", BuildTarget.StandaloneLinux64);

            Debug.Log("Finished building LINUX asset bundle. Starting MAC build...");

            BuildBundle("assets-mac.bundle", BuildTarget.StandaloneOSX);

            Debug.Log("Finished building MAC asset bundle.");

            Debug.Log("Asset bundles are located in the BuiltAssets folder in the project root.");
        }

        private static void BuildBundle(string name, BuildTarget buildTarget)
        {
            var assetPaths = Directory.GetFiles(AssetFolder, "*", SearchOption.AllDirectories);

            var builds = new AssetBundleBuild[1];
            builds[0].assetBundleName = name;
            builds[0].assetNames = assetPaths;

            if (!Directory.Exists(BuildDirectory))
            {
                Directory.CreateDirectory(BuildDirectory);
            }

            BuildPipeline.BuildAssetBundles(BuildDirectory, builds, BuildAssetBundleOptions.None, buildTarget);
        }
    }
}

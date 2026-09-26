using System;
using System.IO;
using System.Linq;
using TMPro;
using UnityEditor;
using UnityEngine;

public static class BuildChatBundle
{
    private const string BundleName = "nebulabundle";
    private const string AssetsRoot = "Assets/Resources/TextMeshPro";
    private const string SettingsPath = "Assets/Resources/TextMeshPro/TMP Settings.asset";

    // Batch entry point: Unity.exe -batchmode -quit -projectPath <project>
    // -executeMethod BuildChatBundle.Build
    public static void Build()
    {
        foreach (var path in AssetDatabase.GetAllAssetPaths()
                     .Where(p => p.StartsWith(AssetsRoot + "/", StringComparison.Ordinal)))
        {
            var importer = AssetImporter.GetAtPath(path);
            if (importer == null || importer.assetBundleName == BundleName) continue;
            importer.assetBundleName = BundleName;
            importer.SaveAndReimport();
        }

        if (Array.IndexOf(AssetDatabase.GetAssetPathsFromAssetBundle(BundleName), SettingsPath) < 0)
        {
            throw new InvalidOperationException("TMP Settings is not assigned to the nebulabundle.");
        }

        var outputDirectory = "Assets/StreamingAssets/AssetBundles";
        Directory.CreateDirectory(outputDirectory);
        var manifest = BuildPipeline.BuildAssetBundles(outputDirectory, BuildAssetBundleOptions.None,
            BuildTarget.StandaloneWindows64);
        if (manifest == null)
        {
            throw new InvalidOperationException("Unity failed to build the nebulabundle asset bundle.");
        }

        var bundle = AssetBundle.LoadFromFile(Path.Combine(outputDirectory, BundleName));
        if (bundle == null)
        {
            throw new InvalidOperationException("The rebuilt nebulabundle could not be loaded.");
        }

        try
        {
            var settings = bundle.LoadAsset<TMP_Settings>(SettingsPath);
            if (settings == null)
            {
                throw new InvalidOperationException("TMP Settings is missing from nebulabundle.");
            }
        }
        finally
        {
            bundle.Unload(false);
        }

        Debug.Log("Nebula chat bundle built and TMP Settings verified.");
    }
}

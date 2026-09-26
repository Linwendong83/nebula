using System;
using System.IO;
using UnityEditor;
using UnityEngine;

public static class BuildNebulaAssetBundle
{
    private const string BundleName = "nebulanametag";
    private const string NameTagShaderPath = "Assets/Resources/ui/shaders/playerNameTag.shader";

    // Batch entry point: Unity.exe -batchmode -quit -projectPath <project>
    // -executeMethod BuildNebulaAssetBundle.Build
    public static void Build()
    {
        var assets = AssetDatabase.GetAssetPathsFromAssetBundle(BundleName);
        if (Array.IndexOf(assets, NameTagShaderPath) < 0)
        {
            throw new InvalidOperationException("The player name tag shader is not assigned to nebulanametag.");
        }

        var outputDirectory = "Assets/StreamingAssets/AssetBundles";
        Directory.CreateDirectory(outputDirectory);
        var manifest = BuildPipeline.BuildAssetBundles(outputDirectory, BuildAssetBundleOptions.None,
            BuildTarget.StandaloneWindows64);
        if (manifest == null)
        {
            throw new InvalidOperationException("Unity failed to build the Nebula asset bundles.");
        }

        var bundle = AssetBundle.LoadFromFile(Path.Combine(outputDirectory, BundleName));
        if (bundle == null)
        {
            throw new InvalidOperationException("The rebuilt nebulanametag could not be loaded.");
        }

        try
        {
            var shader = bundle.LoadAsset<Shader>(NameTagShaderPath);
            if (shader == null || shader.name != "Nebula/PlayerNameTag")
            {
                throw new InvalidOperationException("The player name tag shader is missing from nebulanametag.");
            }
        }
        finally
        {
            bundle.Unload(false);
        }

        Debug.Log("Nebula asset bundle built and player name tag shader verified.");
    }
}

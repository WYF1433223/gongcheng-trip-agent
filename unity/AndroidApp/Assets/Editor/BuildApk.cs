// 一键出包：
//   APK   : Unity -batchmode -quit -projectPath . -buildTarget Android -executeMethod BuildApk.Build
//   Windows: Unity -batchmode -quit -projectPath . -buildTarget Win64  -executeMethod BuildApk.BuildWin
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class BuildApk
{
    const string SceneDir = "Assets/Scenes";
    const string ScenePath = "Assets/Scenes/Main.unity";

    [MenuItem("Build/Build APK")]
    public static void Build()
    {
        PrepareScene();
        ApplyCommon();

        PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Android, "com.wyf.travelagent");
        PlayerSettings.Android.bundleVersionCode = 1;
        PlayerSettings.defaultInterfaceOrientation = UIOrientation.Portrait;
        PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel24;
        PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevelAuto;
        PlayerSettings.Android.forceInternetPermission = true;
        PlayerSettings.SetManagedStrippingLevel(BuildTargetGroup.Android, ManagedStrippingLevel.Low);
        PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
        PlayerSettings.SetScriptingBackend(BuildTargetGroup.Android, ScriptingImplementation.IL2CPP);

        string apk = Path.Combine(OutDir(), "TravelAgent.apk");
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { ScenePath },
            locationPathName = apk,
            target = BuildTarget.Android,
            options = BuildOptions.None
        });
        Debug.Log("[BuildApk] result=" + report.summary.result + " size=" + report.summary.totalSize +
                  " apk=" + apk + " errors=" + report.summary.totalErrors);
        if (report.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
            EditorApplication.Exit(1);
    }

    [MenuItem("Build/Build Windows Player")]
    public static void BuildWin()
    {
        PrepareScene();
        ApplyCommon();
        PlayerSettings.SetScriptingBackend(BuildTargetGroup.Standalone, ScriptingImplementation.Mono2x);
        string exe = Path.Combine(OutDir(), "win64", "TravelAgent.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe));
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { ScenePath },
            locationPathName = exe,
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.None
        });
        Debug.Log("[BuildApk] win result=" + report.summary.result + " exe=" + exe);
        if (report.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
            EditorApplication.Exit(1);
    }

    static string OutDir()
    {
        string dir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "build"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    static void ApplyCommon()
    {
        PlayerSettings.companyName = "wyf";
        PlayerSettings.productName = "拱程行程规划";
        PlayerSettings.bundleVersion = "1.0.0";
        ApplyIcon();
        PlayerSettings.colorSpace = ColorSpace.Gamma;
        PlayerSettings.resizableWindow = true;
        PlayerSettings.defaultScreenWidth = 540;
        PlayerSettings.defaultScreenHeight = 960;
        PlayerSettings.runInBackground = true;
    }

    static void ApplyIcon()
    {
        const string iconPath = "Assets/AppIcon.png";
        var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(iconPath);
        if (tex == null) { Debug.LogWarning("[BuildApk] icon missing: " + iconPath); return; }
        PlayerSettings.SetIconsForTargetGroup(BuildTargetGroup.Android, new[] { tex });
        PlayerSettings.SetIconsForTargetGroup(BuildTargetGroup.Standalone, new[] { tex });
        Debug.Log("[BuildApk] icon applied " + tex.width + "x" + tex.height);
    }

    static void PrepareScene()
    {
        Directory.CreateDirectory(SceneDir);
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var go = new GameObject("App");
        go.AddComponent<App>();
        EditorSceneManager.SaveScene(scene, ScenePath);
        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
    }
}
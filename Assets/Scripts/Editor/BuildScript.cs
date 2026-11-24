// Assets/Scripts/Editor/BuildScript.cs
// 命令行构建入口（batchmode 专用）：
//   PC:   Unity.exe -batchmode -quit -projectPath <工程> -executeMethod BuildScript.BuildAndroid -logFile <log>
//   真机: ... -executeMethod BuildScript.PrepareDevice   （只配置+建场景，便宜的自检步骤）
//         ... -executeMethod BuildScript.BuildDevice     （配置+建场景+打 MR APK）
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

public static class BuildScript
{
    static readonly string[] DevScenes = { "Assets/Scenes/DevEditor.unity" };
    static readonly string[] DeviceScenes = { "Assets/Scenes/Device.unity" };
    static readonly string[] ProductionScenes = { "Assets/Scenes/DeviceProduction.unity" };

    /// <summary>调试版包名（工程原始包名，已装在头显的调试版就是它）。显式写死，
    /// 防止工程设置被上线版构建临时切换后污染调试版——2026-11-24 实际踩到：
    /// 调试 APK 被打成了上线包名，安装会覆盖正式版。</summary>
    const string DebugApplicationId = "com.UnityTechnologies.com.unity.template.urpblank";

    /// <summary>上线版独立包名：安装上线版不会覆盖/卸载调试版，两个版本可在头显中共存。</summary>
    const string ProductionApplicationId = "com.nju.arattention.prod";

    /// <summary>设备应用库里的显示名：调试/上线分开，方便一眼区分。</summary>
    const string DebugProductName = "ARAttention 调试";
    const string ProductionProductName = "ARAttention 正式";

    public static void BuildAndroid()
    {
        ConfigureAndroidTools();

        // 构建前自动重建开发场景，保证场景接线与最新组件一致（防止"忘了重跑菜单"）
        try
        {
            DevSceneCreator.CreateDevScene();
            Debug.Log("[Build] DevEditor 场景已重建");
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[Build] 场景重建失败（沿用现有场景）：{e.Message}");
        }

        EditorApplication.Exit(Build(DevScenes, "Builds/ARAttention-dev.apk") == BuildResult.Succeeded ? 0 : 1);
    }

    /// <summary>真机准备（不打包）：项目配置 + Device 场景生成。先跑它自检，再跑 BuildDevice。</summary>
    public static void PrepareDevice()
    {
        try
        {
            ConfigureAndroidTools();
            MRProjectSetup.Apply();
            XRRuntimeSetup.Apply();
            DeviceSceneCreator.CreateDeviceScene();
            AssetDatabase.SaveAssets();
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[Build] 真机准备失败：{e}");
            EditorApplication.Exit(1);
            return;
        }

        Debug.Log("[Build] 真机配置与场景准备完毕（未打包）");
        EditorApplication.Exit(0);
    }

    /// <summary>打真机 MR APK：项目配置 + Device 场景 + 构建。</summary>
    public static void BuildDevice()
    {
        ConfigureAndroidTools();
        try
        {
            MRProjectSetup.Apply();
            XRRuntimeSetup.Apply();
            DeviceSceneCreator.CreateDeviceScene();
            Debug.Log("[Build] Device 场景已重建");
        }
        catch (System.Exception e)
        {
            // 场景/配置失败就别浪费一次 20 分钟的打包了
            Debug.LogError($"[Build] Device 准备失败：{e}");
            EditorApplication.Exit(1);
            return;
        }

        // 调试版沿用原包名（覆盖已装调试版保留数据），显示名带"调试"
        var result = BuildWithIdentity(DeviceScenes, "Builds/ARAttention-device.apk",
                                       development: true, DebugApplicationId, DebugProductName);
        EditorApplication.Exit(result == BuildResult.Succeeded ? 0 : 1);
    }

    /// <summary>上线版 MR APK：无识别框/数据面板/显式锚点/AX 调试输入，非 Development 构建。</summary>
    public static void BuildDeviceProduction()
    {
        ConfigureAndroidTools();
        try
        {
            MRProjectSetup.Apply();
            XRRuntimeSetup.Apply();
            DeviceSceneCreator.CreateDeviceScene(true);
            Debug.Log("[Build] Production 场景已重建（无调试 UI）");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[Build] Production 准备失败：{e}");
            EditorApplication.Exit(1);
            return;
        }

        // 上线版使用独立包名与独立显示名：安装时不覆盖调试版
        var result = BuildWithIdentity(ProductionScenes, "Builds/ARAttention-release.apk",
                                       development: false, ProductionApplicationId, ProductionProductName);
        EditorApplication.Exit(result == BuildResult.Succeeded ? 0 : 1);
    }

    /// <summary>一次打两个真机 APK（调试版 + 上线版）：
    /// 显示名区分（ARAttention 调试 / ARAttention 正式）、包名不同，设备上可共存互不覆盖。
    /// 调试版：保留 3D 检测框，去掉信息面板；上线版：无调试 UI。</summary>
    public static void BuildAllDevices()
    {
        ConfigureAndroidTools();
        try
        {
            MRProjectSetup.Apply();
            XRRuntimeSetup.Apply();
            DeviceSceneCreator.CreateDeviceScene(false);
            DeviceSceneCreator.CreateDeviceScene(true);
            Debug.Log("[Build] 调试场景 + 上线场景均已重建");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[Build] 双版本准备失败：{e}");
            EditorApplication.Exit(1);
            return;
        }

        BuildResult debugResult, prodResult;
        try
        {
            debugResult = BuildWithIdentity(DeviceScenes, "Builds/ARAttention-device.apk",
                                            development: true, DebugApplicationId, DebugProductName);
            prodResult = BuildWithIdentity(ProductionScenes, "Builds/ARAttention-release.apk",
                                           development: false, ProductionApplicationId, ProductionProductName);
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[Build] 打包失败：{e}");
            EditorApplication.Exit(1);
            return;
        }

        EditorApplication.Exit(debugResult == BuildResult.Succeeded && prodResult == BuildResult.Succeeded ? 0 : 1);
    }

    /// <summary>临时切换包名/显示名后构建，结束后恢复工程设置（不会污染调试/后续构建）。</summary>
    static BuildResult BuildWithIdentity(string[] scenes, string output, bool development,
                                         string applicationId, string productName)
    {
        var group = BuildTargetGroup.Android;
        var originalId = PlayerSettings.GetApplicationIdentifier(group);
        var originalName = PlayerSettings.productName;
        try
        {
            PlayerSettings.SetApplicationIdentifier(group, applicationId);
            PlayerSettings.productName = productName;
            Debug.Log($"[Build] {output} 包名={applicationId} 显示名={productName}");
            return Build(scenes, output, development);
        }
        finally
        {
            PlayerSettings.SetApplicationIdentifier(group, originalId);
            PlayerSettings.productName = originalName;
        }
    }

    static BuildResult Build(string[] scenes, string output, bool development = true)
    {
        var opts = new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = output,
            target = BuildTarget.Android,
            options = development ? BuildOptions.Development : BuildOptions.None,
        };

        var report = BuildPipeline.BuildPlayer(opts);
        Debug.Log($"[Build] 结果={report.summary.result} " +
                  $"大小={report.summary.totalSize} 字节 耗时={report.summary.totalTime}");
        return report.summary.result;
    }

    /// <summary>把 Android 工具链路径显式写进编辑器设置（batchmode 下自带 JDK 检测会失灵）。
    /// ★2026-10-11 实测：Unity 6 的 Android 后处理只认 AndroidExternalToolsSettings.*RootPath，
    /// 不读旧的 EditorPrefs("JdkPath")——只写后者时打包会以 "UnityException: JDK not found" 秒败
    ///（日志特征：Reporting tool paths: JDK: ''）。两套都写，双保险。</summary>
    static void ConfigureAndroidTools()
    {
        const string root = @"D:\Development\Unity\Editor\Data\PlaybackEngines\AndroidPlayer";
        EditorPrefs.SetString("JdkPath", $@"{root}\OpenJDK");
        EditorPrefs.SetString("AndroidSdkRoot", $@"{root}\SDK");
        EditorPrefs.SetString("AndroidNdkRoot", $@"{root}\NDK");
        try
        {
            UnityEditor.Android.AndroidExternalToolsSettings.jdkRootPath = $@"{root}\OpenJDK";
            UnityEditor.Android.AndroidExternalToolsSettings.sdkRootPath = $@"{root}\SDK";
            UnityEditor.Android.AndroidExternalToolsSettings.ndkRootPath = $@"{root}\NDK";
            UnityEditor.Android.AndroidExternalToolsSettings.gradlePath = $@"{root}\Tools\gradle";
            Debug.Log($"[Build] 工具路径已设置（AndroidExternalToolsSettings + EditorPrefs）：JDK={root}\\OpenJDK");
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[Build] AndroidExternalToolsSettings 写入失败（沿用 EditorPrefs）：{e.Message}");
        }
    }
}

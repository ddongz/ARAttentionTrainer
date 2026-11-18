// Assets/Scripts/Editor/AndroidManifestPatcher.cs
// 构建后补丁：向最终 AndroidManifest 直接写入 Meta SDK 没有自动生成、但运行时硬性检查的权限。
// 背景（2026-10-10 实测）：场景扫描（空间设置流程）被系统禁用，日志为——
//   "skipping XR_FB_scene_capture due to: missing uses-permission 'horizonos.permission.USE_ANCHOR_API'"
//   "xrRequestSceneCaptureFB not supported"
// Meta SDK 的 OVRManifestPreprocessor 只会写旧命名空间 com.oculus.permission.USE_ANCHOR_API，
// 而当前 Horizon OS 的 OpenXR 扩展门控检查的是 horizonos.* 新命名空间 → 这里补齐。
using System.IO;
using UnityEditor.Android;
using UnityEngine;

public class AndroidManifestPatcher : IPostGenerateGradleAndroidProject
{
    /// <summary>运行时硬性要求、SDK 未生成的权限（新命名空间）。</summary>
    static readonly string[] ExtraPermissions =
    {
        "horizonos.permission.USE_ANCHOR_API", // 场景扫描/空间设置（XR_FB_scene_capture 门控）
    };

    /// <summary>2026-11-18：无边界 MR。系统 guardian 数据为 0 时会自动生成 Stationary 小圈，
    /// 走两步弹“建立游戏边界”；BOUNDARYLESS_APP 让应用不依赖 Guardian 房间边界。</summary>
    static readonly string[] ExtraFeatures =
    {
        "com.oculus.feature.BOUNDARYLESS_APP",
    };

    public int callbackOrder => 999; // 在所有其他 manifest 处理（含 Meta 预处理器）之后

    public void OnPostGenerateGradleAndroidProject(string path)
    {
        var manifestPath = Path.Combine(path, "src", "main", "AndroidManifest.xml");
        if (!File.Exists(manifestPath))
        {
            Debug.LogWarning($"[ManifestPatch] 未找到 {manifestPath}，跳过");
            return;
        }

        var text = File.ReadAllText(manifestPath);
        bool changed = false;
        foreach (var perm in ExtraPermissions)
        {
            if (text.Contains(perm)) continue;
            var line = $"    <uses-permission android:name=\"{perm}\" />\n";
            text = text.Replace("</manifest>", line + "</manifest>");
            changed = true;
            Debug.Log($"[ManifestPatch] 已写入权限：{perm}");
        }

        foreach (var feature in ExtraFeatures)
        {
            if (text.Contains(feature)) continue;
            var line = $"    <uses-feature android:name=\"{feature}\" android:required=\"true\" />\n";
            text = text.Replace("</manifest>", line + "</manifest>");
            changed = true;
            Debug.Log($"[ManifestPatch] 已写入 feature：{feature}");
        }

        if (changed)
        {
            File.WriteAllText(manifestPath, text);
            Debug.Log($"[ManifestPatch] 已更新 {manifestPath}");
        }
        else
        {
            Debug.Log("[ManifestPatch] 权限已存在，无需修改");
        }
    }
}

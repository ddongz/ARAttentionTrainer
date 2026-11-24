// Assets/Scripts/Core/MeteorLogic.cs
// 流星引导 · 几何纯逻辑（2026-11-24：星星进度已并入 AttentionFlowLogic）：
//   ① 贝塞尔轨迹（流星弧线、星尘路径绕行）
//   ② 星环槽位（围绕老师的 8 个落星位置）
using UnityEngine;

public static class MeteorLogic
{
    /// <summary>二次贝塞尔采样。</summary>
    public static Vector3 Bezier(Vector3 p0, Vector3 ctrl, Vector3 p2, float t)
    {
        float u = 1f - t;
        return u * u * p0 + 2f * u * t * ctrl + t * t * p2;
    }

    /// <summary>星环槽位：老师脸周围一圈（默认 8 个），环面朝向观察者。
    /// slot: 0..count-1（角度均布）；返回世界坐标。</summary>
    public static Vector3 RingSlot(Vector3 teacherPos, Vector3 viewerPos, int slot, int count, float radius,
                                   float verticalSquash = 0.8f)
    {
        Vector3 fwd = teacherPos - viewerPos;
        fwd.y = 0f;
        if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
        fwd.Normalize();
        Vector3 right = Vector3.Cross(Vector3.up, fwd).normalized;
        float ang = Mathf.PI * 2f * slot / Mathf.Max(1, count);
        return teacherPos + right * (Mathf.Cos(ang) * radius)
                          + Vector3.up * (Mathf.Sin(ang) * radius * verticalSquash);
    }

    /// <summary>星尘路径控制点：从 start（孩子前方地面）到 end（老师身旁地面），
    /// 向"远离孩子"的一侧弯，避免路径穿过孩子身体。detour=最小绕行距离。</summary>
    public static Vector3 PathControl(Vector3 childPos, Vector3 start, Vector3 end, float detour = 0.9f)
    {
        Vector3 mid = (start + end) * 0.5f;
        Vector3 dir = end - start;
        dir.y = 0f;
        if (dir.sqrMagnitude < 1e-4f) return mid + Vector3.up * 0.02f;
        Vector3 side = Vector3.Cross(Vector3.up, dir.normalized);
        Vector3 toChild = childPos - start;
        toChild.y = 0f;
        float sideDot = Vector3.Dot(side, toChild);
        // 孩子在线段的哪一侧，就往另一侧绕
        float sign = sideDot > 0f ? -1f : 1f;
        float extra = Mathf.Max(detour, 0.9f - Mathf.Abs(sideDot)); // 孩子越接近路径，绕行越大
        return mid + side * (sign * extra);
    }
}

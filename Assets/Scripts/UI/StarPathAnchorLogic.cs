// Assets/Scripts/UI/StarPathAnchorLogic.cs
// 星路**视野锚点**（纯逻辑可测，2026-11-24 重构）：
//   起点 = 当前视野前下方（孩子脚前/画面下缘附近），随头动；
//   只有明显转头/走动（距离 > viewUpdateDist 或转角 > viewUpdateAngle）并持续 hold 秒才更新，
//   避免星路每帧抖动。
//
// 老师锚点不在这里：它由 AttentionFlowLogic 在"第一次找到老师"时放下，
// 看到老师时平滑跟随、丢失时冻结，再通过 StardustPath.SetTeacherAnchor 传进来。
// 星路 = 这两个锚点之间的贝塞尔路径。
using UnityEngine;

public class StarPathAnchorLogic
{
    /// <summary>本轮星路的视野起点（世界坐标）。</summary>
    public Vector3 ViewAnchor { get; private set; }
    public bool HasViewAnchor { get; private set; }
    /// <summary>本帧锚点是否更新（调用方据此重算路径）。</summary>
    public bool Dirty { get; private set; }

    readonly float _viewUpdateDist;
    readonly float _viewUpdateAngle;
    readonly float _hold;
    float _viewT;

    public StarPathAnchorLogic(float viewUpdateDist = 0.25f, float viewUpdateAngleDeg = 12f,
                               float hold = 0.15f)
    {
        _viewUpdateDist = Mathf.Max(0.02f, viewUpdateDist);
        _viewUpdateAngle = Mathf.Max(1f, viewUpdateAngleDeg);
        _hold = Mathf.Max(0f, hold);
    }

    public void Reset()
    {
        HasViewAnchor = false;
        Dirty = false;
        _viewT = 0f;
    }

    /// <summary>每帧推进。eyePos/eyeForward = 孩子眼睛位姿；
    /// startForward/startDrop = 视野锚点相对眼睛前方/下方的距离（米）。</summary>
    public void Tick(float dt, Vector3 eyePos, Vector3 eyeForward, float startForward, float startDrop)
    {
        Dirty = false;
        if (dt < 0f) dt = 0f;

        Vector3 fwd = new Vector3(eyeForward.x, 0f, eyeForward.z);
        if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
        fwd.Normalize();
        Vector3 candidate = eyePos + fwd * startForward + Vector3.down * startDrop;

        if (!HasViewAnchor)
        {
            ViewAnchor = candidate;
            HasViewAnchor = true;
            Dirty = true;
            return;
        }

        Vector3 a = ViewAnchor - eyePos; a.y = 0f;
        Vector3 b = candidate - eyePos; b.y = 0f;
        float dist = Vector3.Distance(candidate, ViewAnchor);
        float ang = (a.sqrMagnitude > 1e-4f && b.sqrMagnitude > 1e-4f)
            ? Vector3.Angle(a, b) : 0f;

        if (dist > _viewUpdateDist || ang > _viewUpdateAngle)
        {
            _viewT += dt;
            if (_viewT >= _hold)
            {
                ViewAnchor = candidate;
                _viewT = 0f;
                Dirty = true;
            }
        }
        else _viewT = 0f;
    }
}

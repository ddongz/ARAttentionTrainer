// Assets/Scripts/UI/StarPathAnchorLogic.cs
// 星路双锚点（2026-10-25 用户定调，纯逻辑可测）：
//   ① 老师锚点 = **上一次看到老师的位置**（3D 空间锚点）。默认老师不动，所以锚点固定；
//      只有新一轮星路开始时才重新取一次当前/最后已知老师位置。
//   ② 视野锚点 = 当前视野前下方（画面底边之外），随孩子转头/走动。
//   ③ 视野锚点移动"显著"并持续 hold 秒后才更新 —— 星路不会每帧抖/滑；
//      更新时用同一个老师锚点重算星路（远端不动，近端换到新的视野前方）。
// 星路几何由这两个锚点生成（见 StardustPath.ComputeAnchoredPath）。
using UnityEngine;

public class StarPathAnchorLogic
{
    /// <summary>上一次看到老师的位置（固定；丢失时也保持）。</summary>
    public Vector3 TeacherAnchor { get; private set; }
    /// <summary>视野前方的路径起点（随头动，带滞后）。</summary>
    public Vector3 ViewAnchor { get; private set; }
    public bool HasTeacherAnchor { get; private set; }
    public bool HasViewAnchor { get; private set; }
    /// <summary>本帧是否有锚点更新（调用方据此重算星路）。</summary>
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

    /// <summary>新会话：两个锚点都清空。</summary>
    public void Reset()
    {
        HasTeacherAnchor = false;
        HasViewAnchor = false;
        Dirty = false;
        _viewT = 0f;
    }

    /// <summary>新一轮星路：只重置视野锚点，**老师锚点保持不动**（T0 修复）。</summary>
    public void ResetView()
    {
        HasViewAnchor = false;
        _viewT = 0f;
        // Dirty 由下一帧建立新视野锚点置位
    }

    /// <summary>每帧推进。
    /// teacherValid=老师当前是否被检测到；teacherPos=当前老师世界位置（头部点）。
    /// 第一次 teacherValid 时锁定老师锚点，之后**不再移动**（老师默认不动）。
    /// eyePos/eyeForward=孩子眼睛位姿；startForward/startDrop=视野锚点相对眼前下方。</summary>
    public void Tick(float dt, bool teacherValid, Vector3 teacherPos,
                     Vector3 eyePos, Vector3 eyeForward, float startForward, float startDrop)
    {
        Dirty = false;
        if (dt < 0f) dt = 0f;

        // ── 老师锚点：只建立一次，之后固定 ──
        if (teacherValid && !HasTeacherAnchor)
        {
            TeacherAnchor = teacherPos;
            HasTeacherAnchor = true;
            Dirty = true;
        }

        // ── 视野锚点：显著移动才更新 ──
        Vector3 fwd = new Vector3(eyeForward.x, 0f, eyeForward.z);
        if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
        fwd.Normalize();
        Vector3 viewCand = eyePos + fwd * startForward + Vector3.down * startDrop;

        if (!HasViewAnchor)
        {
            ViewAnchor = viewCand;
            HasViewAnchor = true;
            Dirty = true;
            return;
        }

        Vector3 a = ViewAnchor - eyePos; a.y = 0f;
        Vector3 b = viewCand - eyePos; b.y = 0f;
        float dist = Vector3.Distance(viewCand, ViewAnchor);
        float ang = (a.sqrMagnitude > 1e-4f && b.sqrMagnitude > 1e-4f)
            ? Vector3.Angle(a, b) : 0f;
        if (dist > _viewUpdateDist || ang > _viewUpdateAngle)
        {
            _viewT += dt;
            if (_viewT >= _hold)
            {
                ViewAnchor = viewCand;
                _viewT = 0f;
                Dirty = true;
            }
        }
        else _viewT = 0f;
    }
}

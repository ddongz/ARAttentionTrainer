// Assets/Scripts/Core/FirstSearchLogic.cs
// 首寻漫游 · 纯逻辑（2026-11-24 重构）：
//   第一次找到老师前，精灵先在**视野中央**出现，然后沿一条连续、平滑的路径在
//   孩子正前方左右游走（不是逐点停顿/瞬移），吸引孩子抬头/转头，进而看到老师。
//   路径由两条不同频率的正弦叠加：左右摆幅约 ±65°、距离约 1m、眼平上下 ±0.1m；
//   起点在正前方中央；房间边界仍会夹紧。
using UnityEngine;

public struct SearchTickResult
{
    public bool active;
    public Vector3 target;
    public bool voiceDue;
    public int voiceSlot;
}

public class FirstSearchLogic
{
    public bool Active { get; private set; }
    public Vector3 CurrentTarget { get; private set; }
    public int VoiceCount { get; private set; }
    public float Elapsed { get; private set; }

    readonly float _baseRadius, _voiceInterval;
    Vector3 _eyePos, _fwd, _right, _origin;
    float _floorY;
    Bounds _room;
    float _voiceT;
    bool _firstVoicePending;

    public FirstSearchLogic(float moveSpeed = 0.5f, float radius = 1.0f,
                            float holdSeconds = 0.6f, float voiceInterval = 4f)
    {
        // moveSpeed/holdSeconds 保留参数以兼容旧场景资产；连续路径不需要“停顿”。
        _baseRadius = Mathf.Clamp(radius, 0.45f, 1.6f);
        _voiceInterval = Mathf.Max(0.5f, voiceInterval);
    }

    /// <summary>开始搜索。childPos/childForward 是开始瞬间的头位/朝向；floorY=地板高度；room=房间边界。</summary>
    public void Begin(Vector3 childPos, Vector3 childForward, float floorY, Bounds room)
    {
        Vector3 fwd = new Vector3(childForward.x, 0f, childForward.z);
        if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
        fwd.Normalize();
        _fwd = fwd;
        _right = Vector3.Cross(Vector3.up, fwd).normalized;
        _eyePos = childPos;
        _origin = new Vector3(childPos.x, floorY, childPos.z);
        _floorY = floorY;
        _room = room;

        Active = true;
        Elapsed = 0f;
        VoiceCount = 0;
        _voiceT = 0f;
        _firstVoicePending = true;
        CurrentTarget = PathAt(0f);
    }

    public SearchTickResult Tick(float dt)
    {
        var res = new SearchTickResult();
        if (!Active) return res;
        if (dt < 0f) dt = 0f;

        Elapsed += dt;
        CurrentTarget = PathAt(Elapsed);

        // 语音节拍：开场句 0.2s 后，之后每 voiceInterval 一句；槽位轮换
        bool due = false;
        _voiceT += dt;
        if (_firstVoicePending)
        {
            if (_voiceT >= 0.2f) { due = true; _firstVoicePending = false; _voiceT = 0f; }
        }
        else if (_voiceT >= _voiceInterval)
        {
            due = true;
            _voiceT = Mathf.Repeat(_voiceT, _voiceInterval);
        }
        if (due)
        {
            res.voiceDue = true;
            res.voiceSlot = VoiceCount % 5;
            VoiceCount++;
        }

        res.active = true;
        res.target = CurrentTarget;
        return res;
    }

    /// <summary>连续游走路径：t=0 时在正前方中央；左右两条正弦叠加，距离/高度缓慢起伏。</summary>
    Vector3 PathAt(float t)
    {
        float yaw = (55f * Mathf.Sin(0.32f * t) + 10f * Mathf.Sin(0.71f * t)) * Mathf.Deg2Rad;
        float radius = _baseRadius * (1f + 0.08f * Mathf.Sin(0.27f * t));
        float y = _eyePos.y - 0.05f + 0.08f * Mathf.Sin(0.41f * t) + 0.03f * Mathf.Sin(0.90f * t);
        Vector3 dir = _fwd * Mathf.Cos(yaw) + _right * Mathf.Sin(yaw);
        Vector3 p = new Vector3(_eyePos.x, y, _eyePos.z) + dir * radius;
        return ClampToRoom(p, _room, _floorY);
    }

    public void Stop() => Active = false;

    /// <summary>测试/诊断：连续路径的采样点（只读）。</summary>
    public Vector3[] PreviewWaypoints(float duration = 24f, int samples = 16)
    {
        int n = Mathf.Max(2, samples);
        var arr = new Vector3[n];
        for (int i = 0; i < n; i++)
            arr[i] = PathAt(duration * i / (n - 1f));
        return arr;
    }

    static Vector3 ClampToRoom(Vector3 p, Bounds room, float floorY)
    {
        const float margin = 0.35f;
        if (room.size.x > 2f * margin + 0.2f)
            p.x = Mathf.Clamp(p.x, room.min.x + margin, room.max.x - margin);
        if (room.size.z > 2f * margin + 0.2f)
            p.z = Mathf.Clamp(p.z, room.min.z + margin, room.max.z - margin);
        float yMin = floorY + 0.75f;
        float yMax = room.max.y - 0.25f;
        if (yMax > yMin) p.y = Mathf.Clamp(p.y, yMin, yMax);
        return p;
    }
}

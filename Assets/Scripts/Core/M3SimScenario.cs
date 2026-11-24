// Assets/Scripts/Core/M3SimScenario.cs
// 模拟测试机 · 剧本生成器（2026-11-24 重构版）：
// 完整的注意力循环：首寻 → 第一次找到老师 → 看老师累计星星 → 移开视线指路 →
// 回看收路 → 集满奖励 → 下一轮；并覆盖老师离场、讲话门控、海报假脸、快速视线抖动、掉帧。
using System.Collections.Generic;
using UnityEngine;

/// <summary>模拟输入接口：驱动层 Tick 每帧拉取。</summary>
public interface IM3SimInputs
{
    void Tick(float dt, out List<DetectionResult> detections, out List<DetectionResult> faces,
              out bool looking, out bool speaking, out bool speechEnded, out bool reset);
}

public class M3SimScenario : IM3SimInputs
{
    public float TotalDuration { get; private set; }
    public string CurrentPhase => _segIdx < _segs.Count ? _segs[_segIdx].name : "END";

    // 关键时间点（harness 断言用）
    public float FirstTeacherSec = -1f;   // 第一次检测到老师
    public float FirstLookSec = -1f;      // 第一次看老师（stars 开始累计）
    public float FirstLookAwaySec = -1f;  // 第一次移开视线（应进入指路）
    public float TeacherGoneSec = -1f;    // 老师在 Watching 中离场（应指路但不清锚点）
    public float TeacherReturnSec = -1f;  // 老师回到画面
    public float FakeFaceStartSec = -1f;  // 海报假脸窗口起点
    public float FakeFaceEndSec = -1f;

    struct Seg
    {
        public float dur;
        public bool person;
        public bool look;
        public bool speak;
        public float noise;
        public string name;
        public bool faceBox;
        public float fcx, fcy;   // 人脸中心（<0 = 跟随人框顶部）
        public float pbx, pby;   // 人框中心
    }

    readonly List<Seg> _segs = new List<Seg>();
    readonly List<DetectionResult> _dets = new List<DetectionResult>();
    readonly List<DetectionResult> _faces = new List<DetectionResult>();
    readonly List<DetectionResult> _empty = new List<DetectionResult>();
    List<DetectionResult> _lastDetCache;
    readonly System.Random _rng;
    int _segIdx;
    float _segT;
    bool _speakingPrev;
    int _dropFrames;

    const float FW = 0.22f, FH = 0.32f;   // 人脸框
    const float PW = 0.24f, PH = 0.42f;   // 人框
    const float LOOK_X = 0.58f, LOOK_Y = 0.45f; // 老师正前方（人框中心）
    const float AWAY_X = 0.30f, AWAY_Y = 0.40f; // 孩子转头 → 人框偏到画面左侧

    public M3SimScenario(int seed = 7)
    {
        _rng = new System.Random(seed);
        float S = 0f;

        void Add(float d, bool person, bool look, bool speak, float noise, string name,
                 bool faceBox = false, float fcx = -1f, float fcy = -1f,
                 float pbx = LOOK_X, float pby = LOOK_Y)
        {
            _segs.Add(new Seg
            {
                dur = d, person = person, look = look, speak = speak, noise = noise, name = name,
                faceBox = faceBox, fcx = fcx, fcy = fcy, pbx = pbx, pby = pby
            });
            S += d;
        }

        // ── 开场：没有老师，精灵首寻 ──
        Add(6f, false, false, false, 0f, "开场·没有老师（首寻游走 + 语音）");

        // ── 第一次找到老师：孩子正在看，确认 0.5s 后放锚点、开始累计星 ──
        FirstTeacherSec = S;
        FirstLookSec = S;
        Add(4f, true, true, false, 0.01f, "老师出现·孩子正在看（第一次找到）", true);

        // ── 看过老师后移开视线：应进入指路（星路 + 精灵进视野 + 找老师语音）──
        FirstLookAwaySec = S;
        Add(6f, true, false, false, 0.02f, "移开视线（指路）", true, -1, -1, AWAY_X, AWAY_Y);

        // ── 回看：应立刻收路；继续累计到集满（约 35s 处奖励）──
        Add(26f, true, true, false, 0.01f, "回看·持续注视（集满奖励）", true);

        // ── 奖励后的下一轮 + 再次指路 ──
        Add(4f, true, false, false, 0.02f, "奖励后移开（指路）", true, -1, -1, AWAY_X, AWAY_Y);
        Add(16f, true, true, false, 0.012f, "下一轮继续数星星", true);

        // ── 老师在 Watching 中离场：锚点必须保留，仍能指向上次位置 ──
        TeacherGoneSec = S;
        Add(5f, false, true, false, 0f, "老师离场·孩子仍看原方向（指路·锚点保留）");
        TeacherReturnSec = S;
        Add(12f, true, true, false, 0.012f, "老师回来·孩子看老师（返回 Watching）", true);

        // ── 讲话门控：指路期间的精灵语音必须被延后 ──
        Add(4f, true, false, true, 0.02f, "讲话中移开（指路语音应被门控）", true, -1, -1, AWAY_X, AWAY_Y);
        Add(8f, true, true, true, 0.012f, "讲话中看老师（倾听态，不插话）", true);
        Add(4f, true, false, false, 0.02f, "讲话结束·指路", true, -1, -1, AWAY_X, AWAY_Y);

        // ── 海报假脸：中央有高分脸但不属于人体，不能累计星、不能当作老师 ──
        FakeFaceStartSec = S;
        Add(8f, true, true, false, 0.01f, "海报假脸居中（不属于人体→不算老师）",
            true, 0.50f, 0.49f, 0.82f, 0.76f);
        FakeFaceEndSec = S;
        Add(6f, true, true, false, 0.012f, "假脸后恢复", true);

        // ── 长循环：看/移开/奖励/离场/快速抖动，覆盖 20 分钟 ──
        int cycle = 0;
        while (S < 1150f)
        {
            Add(9f, true, true, false, 0.012f, $"C{cycle}·看老师", true);
            Add(3.5f, true, false, false, 0.025f, $"C{cycle}·移开（指路）", true, -1, -1, AWAY_X, AWAY_Y);
            Add(12f, true, true, false, 0.012f, $"C{cycle}·回看（累计星）", true);
            Add(4f, true, false, cycle % 3 == 0, 0.025f, $"C{cycle}·移开（可能讲话）", true, -1, -1, AWAY_X, AWAY_Y);
            Add(11f, true, true, false, 0.012f, $"C{cycle}·继续看", true);

            if (cycle % 5 == 2)
            {
                Add(6f, false, false, false, 0f, $"C{cycle}·老师离场");
                Add(8f, true, true, false, 0.012f, $"C{cycle}·老师回来+看", true);
            }
            if (cycle % 7 == 4)
            {
                // 快速来回（0.4s）：小于指路宽限，不应触发星路闪烁
                for (int k = 0; k < 6; k++)
                {
                    Add(0.4f, true, true, false, 0.05f, $"C{cycle}·快速看", true);
                    Add(0.4f, true, false, false, 0.05f, $"C{cycle}·快速移开", true, -1, -1, AWAY_X, AWAY_Y);
                }
            }
            cycle++;
        }

        TotalDuration = S;
        Debug.Log($"[SIM] 剧本就绪：{_segs.Count} 段 / {TotalDuration / 60f:F1} 分钟 / " +
                  $"首老师 {FirstTeacherSec:F1}s / 首次移开 {FirstLookAwaySec:F1}s / " +
                  $"假脸 {FakeFaceStartSec:F1}s / 离场 {TeacherGoneSec:F1}s");
    }

    public void Tick(float dt, out List<DetectionResult> detections, out List<DetectionResult> faces,
                     out bool looking, out bool speaking, out bool speechEnded, out bool reset)
    {
        reset = false;
        _segT += dt;
        while (_segIdx < _segs.Count && _segT >= _segs[_segIdx].dur)
        {
            _segT -= _segs[_segIdx].dur;
            _segIdx++;
        }
        var seg = _segIdx < _segs.Count ? _segs[_segIdx] : default;

        // 掉帧：保留旧检测批（与真实 Latest 语义一致）
        if (_dropFrames > 0) _dropFrames--;
        else if (seg.person && _rng.NextDouble() < 0.004) _dropFrames = _rng.Next(6, 15);

        if (seg.person)
        {
            if (_dropFrames > 0 && _lastDetCache != null) detections = _lastDetCache;
            else
            {
                float n = seg.noise;
                var box = new Rect(
                    Mathf.Clamp01(seg.pbx - PW * 0.5f + J(n)),
                    Mathf.Clamp01(seg.pby - PH * 0.5f + J(n)),
                    Mathf.Max(0.06f, PW + J(n * 0.5f)),
                    Mathf.Max(0.12f, PH + J(n)));
                _dets.Clear();
                _dets.Add(new DetectionResult { normalizedBox = box, confidence = 0.85f });
                _lastDetCache = _dets;
                detections = _dets;
            }
        }
        else
        {
            _lastDetCache = null;
            detections = _empty;
        }

        if (seg.faceBox)
        {
            float j = seg.noise * 0.25f;
            float cx = seg.fcx >= 0f ? seg.fcx + J(j) : seg.pbx - 0.08f + J(j);
            float cy = seg.fcy >= 0f ? seg.fcy + J(j) : seg.pby - 0.12f + J(j);
            cx = Mathf.Clamp01(cx); cy = Mathf.Clamp01(cy);
            _faces.Clear();
            _faces.Add(new DetectionResult
            {
                normalizedBox = new Rect(cx - FW * 0.5f, cy - FH * 0.5f, FW, FH),
                confidence = 0.92f,
            });
            faces = _faces;
        }
        else faces = _empty;

        looking = seg.look;
        speaking = seg.speak;
        speechEnded = _speakingPrev && !seg.speak;
        _speakingPrev = seg.speak;
    }

    float J(float n) => n <= 0f ? 0f : (float)((_rng.NextDouble() * 2.0 - 1.0) * n);
}

// Assets/Scripts/Core/AttentionFlowLogic.cs
// 注意力训练主流程（2026-11-24 重构，纯逻辑可测）：
//
//   ① FirstSearch  开局：老师还没出现。精灵在视野中央出现后缓慢游走 + 语音吸引，
//                  直到老师被**稳定确认**（连续 teacherFoundConfirmSeconds、位置稳定）。
//   ② Watching     第一次确认老师：在老师位置放下世界锚点，进入"看老师"状态，
//                  **此时绝不生成星路**。孩子看着老师时星星累计；
//                  集满一圈 → Rewarding → 奖励结束自动开始下一轮（星星归零继续累计）。
//   ③ GuidingBack  孩子已经看过老师、随后移开视线 ≥ lookAwayGraceSeconds：
//                  精灵飘进视野、指老师，星路用"老师锚点 + 当前视野锚点"生成，
//                  孩子重新看老师并稳定 gazeReturnConfirmSeconds → 回 Watching（星路立即关闭）。
//   ④ Rewarding    星星集满：奖励播放 rewardSeconds；孩子移开视线则提前收尾并进入 GuidingBack。
//
// 与旧版的关键区别：
//   · 不依赖"老师是否讲话"驱动任何状态；
//   · 星路只在 GuidingBack 打开，第一次找到老师/正在看老师时都不会出现；
//   · 星星只增不减，不因走神/老师暂时出画面而清空；只有集满奖励后归零开始下一轮；
//   · 老师锚点 = 最后一次看到老师的位置（看到时平滑更新，丢失时冻结），不再超时清除。
using UnityEngine;

public enum AttentionState
{
    FirstSearch = 0,
    Watching = 1,
    GuidingBack = 2,
    Rewarding = 3,
}

public enum AttentionEvent
{
    None = 0,
    TeacherFound = 1,     // 第一次稳定确认老师：锚点已建立，进入 Watching
    GuideBackStarted = 2, // 进入 GuidingBack（星路应打开、精灵应飞进视野）
    GazeReturned = 3,     // 孩子在 GuidingBack 中重新看老师：星路应立即关闭
    StarDropped = 4,      // 落下一颗星（未集满）
    RewardStarted = 5,    // 集满：星数已归零，开始奖励（下一轮从 0 开始）
    RewardEnded = 6,      // 奖励结束，回到 Watching
}

public class AttentionFlowLogic
{
    public AttentionState State { get; private set; } = AttentionState.FirstSearch;
    public float StateTime { get; private set; }

    /// <summary>老师世界锚点（第一次稳定确认时放下；之后看到老师时平滑更新，丢失时冻结）。</summary>
    public Vector3 TeacherAnchor { get; private set; }
    public bool HasTeacherAnchor { get; private set; }

    /// <summary>本轮已经点亮的星数（0..StarsPerRing-1）。集满时归零再发 RewardStarted。</summary>
    public int LitStars { get; private set; }
    public int StarsPerRing { get; }
    public float StarProgress01 => Mathf.Clamp01(_starT / _starSeconds);
    public int CompletedRounds { get; private set; }

    /// <summary>孩子是否已经真正看过老师（星路的前置门：没看过就不指路）。</summary>
    public bool HasEverLookedAtTeacher { get; private set; }

    /// <summary>当前是否应该显示星路（= GuidingBack）。</summary>
    public bool PathWanted => State == AttentionState.GuidingBack;

    readonly float _teacherFoundConfirmSec;
    readonly float _anchorStabilityRadius;
    readonly float _anchorSmoothSec;
    readonly float _lookAwayGraceSec;
    readonly float _returnConfirmSec;
    readonly float _starSeconds;
    readonly float _rewardSeconds;
    readonly float _rewardLookAwayGraceSec;

    float _teacherSeenT;
    Vector3 _teacherCandidate;
    bool _teacherCandidateValid;

    float _notLookingT;
    float _returnLookingT;
    float _starT;
    float _rewardT;
    float _rewardNotLookingT;

    public AttentionFlowLogic(float teacherFoundConfirmSeconds = 0.5f,
                              float teacherAnchorStabilityRadius = 0.35f,
                              float lookAwayGraceSeconds = 0.6f,
                              float gazeReturnConfirmSeconds = 0.3f,
                              float starSeconds = 2.5f,
                              int starsPerRing = 8,
                              float rewardSeconds = 6f,
                              float rewardLookAwayGraceSeconds = 0.6f,
                              float anchorSmoothSeconds = 0.5f)
    {
        _teacherFoundConfirmSec = Mathf.Max(0.05f, teacherFoundConfirmSeconds);
        _anchorStabilityRadius = Mathf.Max(0.05f, teacherAnchorStabilityRadius);
        _anchorSmoothSec = Mathf.Max(0.05f, anchorSmoothSeconds);
        _lookAwayGraceSec = Mathf.Max(0.05f, lookAwayGraceSeconds);
        _returnConfirmSec = Mathf.Max(0.05f, gazeReturnConfirmSeconds);
        _starSeconds = Mathf.Max(0.2f, starSeconds);
        StarsPerRing = Mathf.Max(1, starsPerRing);
        _rewardSeconds = Mathf.Max(0.2f, rewardSeconds);
        _rewardLookAwayGraceSec = Mathf.Max(0.1f, rewardLookAwayGraceSeconds);
    }

    public void Reset()
    {
        State = AttentionState.FirstSearch;
        StateTime = 0f;
        TeacherAnchor = default;
        HasTeacherAnchor = false;
        LitStars = 0;
        CompletedRounds = 0;
        HasEverLookedAtTeacher = false;
        _teacherSeenT = 0f;
        _teacherCandidate = default;
        _teacherCandidateValid = false;
        _notLookingT = 0f;
        _returnLookingT = 0f;
        _starT = 0f;
        _rewardT = 0f;
        _rewardNotLookingT = 0f;
    }

    /// <summary>单帧推进。
    /// teacherSeen = 老师当前被可靠检测到（新鲜检测 + 属于人体的脸）；
    /// teacherPos = 老师当前世界位置（头部/脸中心）；
    /// lookingAtTeacher = 孩子当前是否在看着老师（头姿/几何代理，已含防抖）。</summary>
    public AttentionEvent Tick(float dt, bool teacherSeen, Vector3 teacherPos, bool lookingAtTeacher)
    {
        if (dt <= 0f) dt = 1f / 30f;
        StateTime += dt;

        UpdateTeacherAnchor(dt, teacherSeen, teacherPos);

        switch (State)
        {
            case AttentionState.FirstSearch:
                // 首寻要等“孩子真的看向老师”的那一刻才算找到：
                // 老师被检测到但孩子没看 → 不计确认时间（精灵继续吸引）。
                if (!teacherSeen || !lookingAtTeacher) _teacherSeenT = 0f;
                // 没有稳定确认老师之前一直搜索；锚点建立的那一帧就是"第一次找到老师"。
                if (!HasTeacherAnchor)
                {
                    if (teacherSeen && lookingAtTeacher && _teacherSeenT >= _teacherFoundConfirmSec)
                    {
                        TeacherAnchor = teacherPos;
                        HasTeacherAnchor = true;
                        HasEverLookedAtTeacher = true; // 找到老师=孩子正看着老师
                        Enter(AttentionState.Watching);
                        return AttentionEvent.TeacherFound;
                    }
                    return AttentionEvent.None;
                }
                // 理论上不会走到（锚点建立当帧已进入 Watching），保底推进。
                Enter(AttentionState.Watching);
                return AttentionEvent.None;

            case AttentionState.Watching:
            {
                if (lookingAtTeacher)
                {
                    HasEverLookedAtTeacher = true;
                    _notLookingT = 0f;
                    return CountStars(dt);
                }

                _notLookingT += dt;
                // 关键：只有"看过老师之后又移开"才进入指路；第一次找到老师时绝不立刻生成星路。
                if (HasEverLookedAtTeacher && _notLookingT >= _lookAwayGraceSec)
                {
                    _returnLookingT = 0f;
                    Enter(AttentionState.GuidingBack);
                    return AttentionEvent.GuideBackStarted;
                }
                return AttentionEvent.None;
            }

            case AttentionState.GuidingBack:
                if (lookingAtTeacher)
                {
                    _returnLookingT += dt;
                    if (_returnLookingT >= _returnConfirmSec)
                    {
                        _notLookingT = 0f;
                        Enter(AttentionState.Watching);
                        return AttentionEvent.GazeReturned;
                    }
                }
                else _returnLookingT = 0f;
                return AttentionEvent.None;

            case AttentionState.Rewarding:
            {
                _rewardT += dt;
                if (lookingAtTeacher) _rewardNotLookingT = 0f;
                else _rewardNotLookingT += dt;

                if (_rewardT >= _rewardSeconds)
                {
                    Enter(AttentionState.Watching);
                    _notLookingT = 0f;
                    return AttentionEvent.RewardEnded;
                }
                // 奖励跟随注视：孩子持续移开视线 → 提前收尾，立刻回到指路状态。
                if (_rewardNotLookingT >= _rewardLookAwayGraceSec)
                {
                    _returnLookingT = 0f;
                    Enter(AttentionState.GuidingBack);
                    return AttentionEvent.GuideBackStarted;
                }
                return AttentionEvent.None;
            }
        }
        return AttentionEvent.None;
    }

    AttentionEvent CountStars(float dt)
    {
        _starT += dt;
        if (_starT < _starSeconds) return AttentionEvent.None;

        _starT -= _starSeconds;
        LitStars++;
        if (LitStars < StarsPerRing) return AttentionEvent.StarDropped;

        // 集满：先归零，奖励结束后的下一轮从 0 开始累计。
        LitStars = 0;
        _starT = 0f;
        CompletedRounds++;
        _rewardT = 0f;
        _rewardNotLookingT = 0f;
        Enter(AttentionState.Rewarding);
        return AttentionEvent.RewardStarted;
    }

    /// <summary>老师锚点维护：确认期要求位置稳定；确认后看到老师时平滑跟随，丢失时冻结。</summary>
    void UpdateTeacherAnchor(float dt, bool teacherSeen, Vector3 teacherPos)
    {
        if (teacherSeen)
        {
            if (!HasTeacherAnchor)
            {
                if (!_teacherCandidateValid)
                {
                    _teacherCandidate = teacherPos;
                    _teacherCandidateValid = true;
                    _teacherSeenT = 0f;
                }
                else if (Vector3.Distance(teacherPos, _teacherCandidate) > _anchorStabilityRadius)
                {
                    // 检测位置还在跳（换目标/噪声）→ 重新计时
                    _teacherCandidate = teacherPos;
                    _teacherSeenT = 0f;
                }
                _teacherSeenT += dt;
            }
            else
            {
                // 最后一次看到老师的位置：平滑吸收检测抖动，老师走动时也能跟上。
                float k = 1f - Mathf.Exp(-dt / _anchorSmoothSec);
                TeacherAnchor = Vector3.Lerp(TeacherAnchor, teacherPos, k);
            }
        }
        else
        {
            _teacherSeenT = 0f;
            if (!HasTeacherAnchor) _teacherCandidateValid = false;
            // 已建立的老师锚点**不清除**：星路仍然指向"上一次看到老师的位置"。
        }
    }

    void Enter(AttentionState s)
    {
        State = s;
        StateTime = 0f;
    }
}

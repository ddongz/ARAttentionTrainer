// Assets/Tests/EditMode/AttentionFlowLogicTests.cs
// 注意力主流程单测（2026-11-24 重构）：
//   首寻→第一次找到老师→看到老师累计星→移开视线才指路→回看立刻收路→集满奖励→下一轮。
using NUnit.Framework;
using UnityEngine;

public class AttentionFlowLogicTests
{
    const float Dt = 1f / 30f;
    static readonly Vector3 Teacher = new Vector3(0f, 1.4f, 2f);

    static AttentionFlowLogic Make(float starSeconds = 0.5f, int stars = 8,
                                   float rewardSeconds = 1f, float lookAwayGrace = 0.2f,
                                   float returnConfirm = 0.1f, float foundConfirm = 0.2f)
        => new AttentionFlowLogic(foundConfirm, 0.35f, lookAwayGrace, returnConfirm,
                                  starSeconds, stars, rewardSeconds, 0.2f, 0.2f);

    static void Step(AttentionFlowLogic f, float seconds, bool seen, bool looking,
                     Vector3? pos = null)
    {
        int n = Mathf.CeilToInt(seconds / Dt);
        for (int i = 0; i < n; i++)
            f.Tick(Dt, seen, pos ?? Teacher, looking);
    }

    static AttentionEvent StepEvent(AttentionFlowLogic f, float seconds, bool seen, bool looking,
                                    Vector3? pos = null)
    {
        AttentionEvent last = AttentionEvent.None;
        int n = Mathf.CeilToInt(seconds / Dt);
        for (int i = 0; i < n; i++)
        {
            var e = f.Tick(Dt, seen, pos ?? Teacher, looking);
            if (e != AttentionEvent.None) last = e;
        }
        return last;
    }

    /// <summary>推进到第一次找到老师（Watching）。</summary>
    static AttentionFlowLogic Found()
    {
        var f = Make();
        Assert.AreEqual(AttentionEvent.TeacherFound, StepEvent(f, 0.3f, true, true));
        Assert.AreEqual(AttentionState.Watching, f.State);
        return f;
    }

    [Test]
    public void FirstSearch_NeedsStableTeacherBeforeFound()
    {
        var f = Make(foundConfirm: 0.5f);
        Assert.AreEqual(AttentionState.FirstSearch, f.State);
        Assert.AreEqual(AttentionEvent.None, StepEvent(f, 0.4f, true, true));
        Assert.IsFalse(f.HasTeacherAnchor, "稳定确认前不能放锚点");
        Assert.AreEqual(AttentionEvent.TeacherFound, StepEvent(f, 0.15f, true, true));
        Assert.IsTrue(f.HasTeacherAnchor);
        Assert.AreEqual(Teacher, f.TeacherAnchor);
    }

    [Test]
    public void FirstFound_RequiresChildToLookAtTeacher()
    {
        var f = Make(foundConfirm: 0.3f);
        // 老师被检测到、但孩子没看 → 一直停留在首寻，不放锚点
        Step(f, 2f, true, false);
        Assert.AreEqual(AttentionState.FirstSearch, f.State);
        Assert.IsFalse(f.HasTeacherAnchor);
        // 孩子看向老师 → 稳定确认后才算找到
        Assert.AreEqual(AttentionEvent.TeacherFound, StepEvent(f, 0.4f, true, true));
        Assert.AreEqual(AttentionState.Watching, f.State);
        Assert.IsTrue(f.HasTeacherAnchor);
    }

    [Test]
    public void FirstFound_DoesNotOpenStarPathImmediately()
    {
        var f = Found();
        Assert.IsTrue(f.HasEverLookedAtTeacher, "找到老师时孩子正看着老师");
        Assert.IsFalse(f.PathWanted, "第一次找到老师时绝不立刻生成星路");

        // 继续看一会儿，仍然没有星路
        Step(f, 0.5f, true, true);
        Assert.AreEqual(AttentionState.Watching, f.State);
        Assert.IsFalse(f.PathWanted);

        // 刚刚移开（未到指路宽限）也不应该有星路
        Step(f, 0.1f, true, false);
        Assert.IsFalse(f.PathWanted);
    }

    [Test]
    public void Watching_StarsOnlyWhileLooking_AndProgressPersists()
    {
        var f = Make(starSeconds: 0.5f, lookAwayGrace: 0.4f);
        Step(f, 0.3f, true, true); // found
        Assert.AreEqual(AttentionEvent.StarDropped, StepEvent(f, 0.55f, true, true));
        Assert.AreEqual(1, f.LitStars);

        Step(f, 0.3f, true, false); // 短时移开（未到指路宽限）
        Assert.AreEqual(AttentionState.Watching, f.State);
        Assert.AreEqual(1, f.LitStars, "走神不清零");

        Step(f, 0.3f, true, true);  // 回来继续
        Assert.AreEqual(1, f.LitStars, "累计进度保留");
        Assert.AreEqual(AttentionEvent.StarDropped, StepEvent(f, 0.4f, true, true));
        Assert.AreEqual(2, f.LitStars);
    }

    [Test]
    public void LookAway_AfterWatching_EntersGuideBack_WithPath()
    {
        var f = Found();
        Step(f, 0.2f, true, true); // 看过老师
        Assert.AreEqual(AttentionEvent.GuideBackStarted,
                        StepEvent(f, 0.35f, true, false));
        Assert.AreEqual(AttentionState.GuidingBack, f.State);
        Assert.IsTrue(f.PathWanted, "指路状态必须打开星路");
    }

    [Test]
    public void GuideBack_LookingBack_ClosesPathImmediately()
    {
        var f = Found();
        Step(f, 0.2f, true, true);
        Step(f, 0.35f, true, false);
        Assert.AreEqual(AttentionState.GuidingBack, f.State);

        Assert.AreEqual(AttentionEvent.GazeReturned,
                        StepEvent(f, 0.2f, true, true));
        Assert.AreEqual(AttentionState.Watching, f.State);
        Assert.IsFalse(f.PathWanted, "重新看老师必须立即关闭星路");
    }

    [Test]
    public void StarsSurviveGuideEpisode()
    {
        var f = Make(starSeconds: 0.5f, lookAwayGrace: 0.2f, returnConfirm: 0.1f);
        Step(f, 0.3f, true, true);
        StepEvent(f, 0.55f, true, true); // 1 颗
        Step(f, 0.3f, true, false);      // 指路
        Assert.AreEqual(AttentionState.GuidingBack, f.State);
        Step(f, 0.2f, true, true);       // 回看
        Assert.AreEqual(AttentionState.Watching, f.State);
        Assert.AreEqual(1, f.LitStars, "指路期间星星保留");
    }

    [Test]
    public void FullRing_RewardsThenStartsNextRoundFromZero()
    {
        var f = Make(starSeconds: 0.5f, stars: 2, rewardSeconds: 0.5f,
                     lookAwayGrace: 0.2f, returnConfirm: 0.1f);
        Step(f, 0.3f, true, true); // found

        Assert.AreEqual(AttentionEvent.StarDropped, StepEvent(f, 0.55f, true, true));
        Assert.AreEqual(1, f.LitStars);
        Assert.AreEqual(AttentionEvent.RewardStarted, StepEvent(f, 0.55f, true, true));
        Assert.AreEqual(AttentionState.Rewarding, f.State);
        Assert.AreEqual(0, f.LitStars, "集满即归零，下一轮从 0 开始");
        Assert.AreEqual(1, f.CompletedRounds);

        Assert.AreEqual(AttentionEvent.RewardEnded, StepEvent(f, 0.6f, true, true));
        Assert.AreEqual(AttentionState.Watching, f.State);
        Assert.AreEqual(0, f.LitStars);

        Assert.AreEqual(AttentionEvent.StarDropped, StepEvent(f, 0.55f, true, true));
        Assert.AreEqual(1, f.LitStars, "奖励结束后开始下一轮数星星");
    }

    [Test]
    public void Reward_InterruptedByLookAway_GoesToGuideBack()
    {
        var f = Make(starSeconds: 0.5f, stars: 1, rewardSeconds: 2f,
                     lookAwayGrace: 0.2f, returnConfirm: 0.1f);
        Step(f, 0.3f, true, true);
        Assert.AreEqual(AttentionEvent.RewardStarted, StepEvent(f, 0.55f, true, true));
        Assert.AreEqual(AttentionState.Rewarding, f.State);
        Assert.AreEqual(AttentionEvent.GuideBackStarted, StepEvent(f, 0.35f, true, false));
        Assert.AreEqual(AttentionState.GuidingBack, f.State);
        Assert.IsTrue(f.PathWanted);
    }

    [Test]
    public void TeacherLost_KeepsAnchor_AndGuidesAfterGrace()
    {
        var f = Found();
        Step(f, 0.2f, true, true);
        Vector3 anchor = f.TeacherAnchor;

        Step(f, 1f, false, false);
        Assert.IsTrue(f.HasTeacherAnchor, "老师暂时丢失不能清除锚点");
        Assert.Less(Vector3.Distance(anchor, f.TeacherAnchor), 1e-4f, "丢失时锚点冻结");
        Assert.AreEqual(AttentionState.GuidingBack, f.State, "看过老师后丢失 → 指路");
        Assert.IsTrue(f.PathWanted, "仍然指向最后一次看到老师的位置");
    }

    [Test]
    public void TeacherAnchor_FollowsTeacherWhenSeen_ThenFreezes()
    {
        var f = new AttentionFlowLogic(0.2f, 0.35f, 0.2f, 0.1f, 0.5f, 8, 1f, 0.2f, 0.2f);
        Step(f, 0.3f, true, true);
        Assert.AreEqual(AttentionState.Watching, f.State);

        var moved = Teacher + new Vector3(0.6f, 0f, 0f);
        Step(f, 1f, true, true, moved);
        Assert.Less(Vector3.Distance(f.TeacherAnchor, moved), 0.1f, "看到老师时锚点应跟上");

        Vector3 before = f.TeacherAnchor;
        Step(f, 0.5f, false, true);
        Assert.Less(Vector3.Distance(before, f.TeacherAnchor), 1e-4f, "丢失后锚点必须冻结");
    }
}

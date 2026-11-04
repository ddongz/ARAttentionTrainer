// Assets/Tests/EditMode/StarPathAnchorLogicTests.cs
// 星路双锚点逻辑测试（2026-10-25）：
//   老师锚点 = 上一次看到的位置，建立后固定；
//   视野锚点 = 随头动，但显著移动 + hold 才更新。
using NUnit.Framework;
using UnityEngine;

public class StarPathAnchorLogicTests
{
    static bool Near(Vector3 a, Vector3 b) => Vector3.Distance(a, b) < 1e-4f;

    [Test]
    public void FirstValidFrame_EstablishesBothAnchors()
    {
        var a = new StarPathAnchorLogic(0.25f, 12f, 0.1f, 0f);
        var eye = new Vector3(0f, 1.2f, 0f);
        a.Tick(0.1f, true, new Vector3(0f, 1.4f, 3f), eye, Vector3.forward, 0.55f, 0.95f);
        Assert.IsTrue(a.HasTeacherAnchor);
        Assert.IsTrue(a.HasViewAnchor);
        Assert.IsTrue(a.Dirty);
        Assert.Less(Vector3.Distance(a.ViewAnchor, new Vector3(0f, 0.25f, 0.55f)), 1e-4f);
    }

    [Test]
    public void TeacherAnchor_RequiresStableConfirmation()
    {
        var a = new StarPathAnchorLogic(0.25f, 12f, 0.1f, 0.3f);
        var eye = new Vector3(0f, 1.2f, 0f);
        var t = new Vector3(0f, 1.4f, 3f);
        a.Tick(0.1f, true, t, eye, Vector3.forward, 0.55f, 0.95f);
        Assert.IsFalse(a.HasTeacherAnchor, "单帧不锁定");
        a.Tick(0.1f, true, t, eye, Vector3.forward, 0.55f, 0.95f);
        Assert.IsFalse(a.HasTeacherAnchor, "未满 0.3s 不锁定");
        a.Tick(0.11f, true, t, eye, Vector3.forward, 0.55f, 0.95f);
        Assert.IsTrue(a.HasTeacherAnchor, "连续确认 0.3s 后锁定");

        // 中途丢失 → 重新计时
        var b = new StarPathAnchorLogic(0.25f, 12f, 0.1f, 0.3f);
        b.Tick(0.2f, true, t, eye, Vector3.forward, 0.55f, 0.95f);
        b.Tick(0.1f, false, t, eye, Vector3.forward, 0.55f, 0.95f);
        b.Tick(0.2f, true, t, eye, Vector3.forward, 0.55f, 0.95f);
        Assert.IsFalse(b.HasTeacherAnchor, "中途丢失应重新计时");
        b.Tick(0.11f, true, t, eye, Vector3.forward, 0.55f, 0.95f);
        Assert.IsTrue(b.HasTeacherAnchor);
    }

    [Test]
    public void TeacherAnchor_MovingPositionResetsStability()
    {
        var a = new StarPathAnchorLogic(0.25f, 12f, 0.1f, 0.5f, 0.2f);
        var eye = new Vector3(0f, 1.2f, 0f);
        var t0 = new Vector3(0f, 1.4f, 3f);
        a.Tick(0.2f, true, t0, eye, Vector3.forward, 0.55f, 0.95f);
        Assert.IsFalse(a.HasTeacherAnchor);
        // 位置跳 0.5m > 稳定性半径 0.2m → 重新计时
        var t1 = t0 + new Vector3(0.5f, 0f, 0f);
        a.Tick(0.2f, true, t1, eye, Vector3.forward, 0.55f, 0.95f);
        Assert.IsFalse(a.HasTeacherAnchor);
        a.Tick(0.2f, true, t1, eye, Vector3.forward, 0.55f, 0.95f);
        Assert.IsFalse(a.HasTeacherAnchor, "跳变后未满 0.5s 不稳定");
        a.Tick(0.4f, true, t1, eye, Vector3.forward, 0.55f, 0.95f);
        Assert.IsTrue(a.HasTeacherAnchor, "位置稳定满 0.5s 后锁定");
    }

    [Test]
    public void TeacherAnchor_IsFixedAfterFirstSighting()
    {
        var a = new StarPathAnchorLogic(0.25f, 12f, 0.1f, 0f);
        var eye = new Vector3(0f, 1.2f, 0f);
        var t0 = new Vector3(0f, 1.4f, 3f);
        a.Tick(0.1f, true, t0, eye, Vector3.forward, 0.55f, 0.95f);

        // 后续检测抖动 / 老师走动：锚点都不动（老师默认不动）
        a.Tick(0.1f, true, t0 + new Vector3(0.12f, 0f, 0f), eye, Vector3.forward, 0.55f, 0.95f);
        a.Tick(0.1f, true, t0 + new Vector3(1.5f, 0f, 0f), eye, Vector3.forward, 0.55f, 0.95f);
        Assert.IsTrue(Near(a.TeacherAnchor, t0), "老师锚点建立后应固定");

        // 目标丢失：冻结
        a.Tick(0.1f, false, t0 + new Vector3(2f, 0f, 0f), eye, Vector3.forward, 0.55f, 0.95f);
        Assert.IsTrue(Near(a.TeacherAnchor, t0));
    }

    [Test]
    public void ResetView_KeepsTeacherAnchorFixed()
    {
        var a = new StarPathAnchorLogic(0.25f, 12f, 0.1f, 0f);
        var eye = new Vector3(0f, 1.2f, 0f);
        var t0 = new Vector3(0f, 1.4f, 3f);
        a.Tick(0.1f, true, t0, eye, Vector3.forward, 0.55f, 0.95f);

        // 新一轮星路只重置视野锚点：老师锚点必须保持不动（T0 修复）
        a.ResetView();
        a.Tick(0.1f, true, t0 + new Vector3(1.5f, 0f, 0f), eye, Vector3.forward, 0.55f, 0.95f);
        Assert.IsTrue(Near(a.TeacherAnchor, t0), "ResetView 不应移动老师锚点");
        Assert.IsTrue(a.HasViewAnchor, "ResetView 后应重新建立视野锚点");
    }

    [Test]
    public void ClearTeacherAnchor_AllowsRelockWhenTeacherReturns()
    {
        var a = new StarPathAnchorLogic(0.25f, 12f, 0.1f, 0f);
        var eye = new Vector3(0f, 1.2f, 0f);
        var t0 = new Vector3(0f, 1.4f, 3f);
        a.Tick(0.1f, true, t0, eye, Vector3.forward, 0.55f, 0.95f);
        a.ClearTeacherAnchor();
        Assert.IsFalse(a.HasTeacherAnchor, "超时后应丢掉旧锚点");
        var t1 = new Vector3(1f, 1.4f, 2f);
        a.Tick(0.1f, true, t1, eye, Vector3.forward, 0.55f, 0.95f);
        Assert.IsTrue(Near(a.TeacherAnchor, t1), "再次看到老师应重新锁定新位置");
    }

    [Test]
    public void Reset_AllowsNewTeacherAnchorForNextEpisode()
    {
        var a = new StarPathAnchorLogic(0.25f, 12f, 0.1f, 0f);
        var eye = new Vector3(0f, 1.2f, 0f);
        var t0 = new Vector3(0f, 1.4f, 3f);
        a.Tick(0.1f, true, t0, eye, Vector3.forward, 0.55f, 0.95f);
        a.Reset();
        var t1 = new Vector3(1f, 1.4f, 2f);
        a.Tick(0.1f, true, t1, eye, Vector3.forward, 0.55f, 0.95f);
        Assert.IsTrue(Near(a.TeacherAnchor, t1), "新一轮星路应取当前/最后已知老师位置");
    }

    [Test]
    public void ViewAnchor_UpdatesOnlyAfterTurnIsSignificant()
    {
        var a = new StarPathAnchorLogic(0.25f, 12f, 0.1f, 0f);
        var eye = new Vector3(0f, 1.2f, 0f);
        var t = new Vector3(0f, 1.4f, 3f);
        a.Tick(0.1f, true, t, eye, Vector3.forward, 0.55f, 0.95f);
        var v0 = a.ViewAnchor;

        // 小幅转头 5°：不更新
        Quaternion q5 = Quaternion.Euler(0f, 5f, 0f);
        a.Tick(0.1f, true, t, eye, q5 * Vector3.forward, 0.55f, 0.95f);
        Assert.IsTrue(Near(a.ViewAnchor, v0), "小幅转头不更新视野锚点");

        // 转头 40°：持续 hold 后更新
        Quaternion q40 = Quaternion.Euler(0f, 40f, 0f);
        a.Tick(0.05f, true, t, eye, q40 * Vector3.forward, 0.55f, 0.95f);
        a.Tick(0.06f, true, t, eye, q40 * Vector3.forward, 0.55f, 0.95f);
        Assert.Greater(Vector3.Distance(a.ViewAnchor, v0), 0.1f, "显著转头后更新");
        Assert.IsTrue(Near(a.TeacherAnchor, t), "视野更新不应移动老师锚点");
    }
}

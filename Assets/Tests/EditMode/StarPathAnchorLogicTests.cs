// Assets/Tests/EditMode/StarPathAnchorLogicTests.cs
// 星路视野锚点（2026-11-24 重构）：
//   起点 = 当前视野前下方；显著转头/移动并持续 hold 才更新。
//   老师锚点已移到 AttentionFlowLogic（见 AttentionFlowLogicTests）。
using NUnit.Framework;
using UnityEngine;

public class StarPathAnchorLogicTests
{
    static bool Near(Vector3 a, Vector3 b) => Vector3.Distance(a, b) < 1e-4f;

    [Test]
    public void FirstFrame_EstablishesViewAnchor()
    {
        var a = new StarPathAnchorLogic(0.25f, 12f, 0.1f);
        var eye = new Vector3(0f, 1.2f, 0f);
        a.Tick(0.1f, eye, Vector3.forward, 0.55f, 0.95f);
        Assert.IsTrue(a.HasViewAnchor);
        Assert.IsTrue(a.Dirty);
        Assert.IsTrue(Near(a.ViewAnchor, new Vector3(0f, 0.25f, 0.55f)));
    }

    [Test]
    public void SmallHeadTurn_DoesNotMoveAnchor()
    {
        var a = new StarPathAnchorLogic(0.25f, 12f, 0.1f);
        var eye = new Vector3(0f, 1.2f, 0f);
        a.Tick(0.1f, eye, Vector3.forward, 0.55f, 0.95f);
        var v0 = a.ViewAnchor;

        Quaternion q5 = Quaternion.Euler(0f, 5f, 0f);
        a.Tick(0.1f, eye, q5 * Vector3.forward, 0.55f, 0.95f);
        Assert.IsTrue(Near(a.ViewAnchor, v0), "小幅转头不更新视野锚点");
        Assert.IsFalse(a.Dirty);
    }

    [Test]
    public void SignificantTurn_UpdatesAfterHold()
    {
        var a = new StarPathAnchorLogic(0.25f, 12f, 0.1f);
        var eye = new Vector3(0f, 1.2f, 0f);
        a.Tick(0.1f, eye, Vector3.forward, 0.55f, 0.95f);
        var v0 = a.ViewAnchor;

        Quaternion q40 = Quaternion.Euler(0f, 40f, 0f);
        a.Tick(0.05f, eye, q40 * Vector3.forward, 0.55f, 0.95f);
        Assert.IsFalse(a.Dirty, "未满 hold 不更新");
        a.Tick(0.06f, eye, q40 * Vector3.forward, 0.55f, 0.95f);
        Assert.IsTrue(a.Dirty, "持续显著转头后更新");
        Assert.Greater(Vector3.Distance(a.ViewAnchor, v0), 0.1f);
    }

    [Test]
    public void Reset_ForgetsViewAnchor()
    {
        var a = new StarPathAnchorLogic(0.25f, 12f, 0.1f);
        var eye = new Vector3(0f, 1.2f, 0f);
        a.Tick(0.1f, eye, Vector3.forward, 0.55f, 0.95f);
        a.Reset();
        Assert.IsFalse(a.HasViewAnchor);
        a.Tick(0.1f, eye, Vector3.forward, 0.55f, 0.95f);
        Assert.IsTrue(a.HasViewAnchor);
        Assert.IsTrue(a.Dirty, "新一轮开启第一帧应重建");
    }
}

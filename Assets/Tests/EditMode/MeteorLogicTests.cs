// Assets/Tests/EditMode/MeteorLogicTests.cs
// 流星几何单测（2026-11-24）：贝塞尔轨迹 / 星环槽位 / 星尘路径绕行。
// 星星进度与三段式引导导演已删除，由 AttentionFlowLogicTests 覆盖新流程。
using NUnit.Framework;
using UnityEngine;

public class MeteorLogicTests
{
    [Test]
    public void Bezier_EndpointsMatch()
    {
        var p0 = new Vector3(0f, 2f, 0f);
        var c = new Vector3(0.5f, 1f, 0f);
        var p2 = new Vector3(1f, 0f, 0f);
        Assert.AreEqual(p0, MeteorLogic.Bezier(p0, c, p2, 0f));
        Assert.AreEqual(p2, MeteorLogic.Bezier(p0, c, p2, 1f));
        var mid = MeteorLogic.Bezier(p0, c, p2, 0.5f);
        Assert.AreEqual(0.5f, mid.x, 1e-4f);
        Assert.AreEqual(1.0f, mid.y, 1e-4f);
    }

    [Test]
    public void RingSlot_SurroundsTeacher()
    {
        var teacher = new Vector3(0f, 1.5f, 2f);
        var viewer = Vector3.zero;
        for (int i = 0; i < 8; i++)
        {
            var p = MeteorLogic.RingSlot(teacher, viewer, i, 8, 0.35f);
            var d = p - teacher;
            float horiz = new Vector2(d.x, d.z).magnitude;
            Assert.LessOrEqual(horiz, 0.36f, $"槽位 {i} 水平超界");
            Assert.GreaterOrEqual(d.magnitude, 0.25f, $"槽位 {i} 过近");
            Assert.LessOrEqual(d.magnitude, 0.45f, $"槽位 {i} 过远");
        }
        var s0 = MeteorLogic.RingSlot(teacher, viewer, 0, 8, 0.35f);
        var s4 = MeteorLogic.RingSlot(teacher, viewer, 4, 8, 0.35f);
        Assert.Greater(Vector3.Distance(s0, s4), 0.3f, "对角槽位应分布在不同侧");
    }

    [Test]
    public void PathControl_BendsAwayFromChild()
    {
        var child = new Vector3(0f, 1.5f, 0f);
        var start = new Vector3(0f, 0f, 1.2f);
        var end = new Vector3(0f, 0f, 4f);
        var ctrl = MeteorLogic.PathControl(child, start, end, 0.9f);
        var lineDir = (end - start).normalized;
        var offset = ctrl - Vector3.Lerp(start, end, 0.5f);
        offset.y = 0f;
        Assert.Greater(offset.magnitude, 0.5f, "控制点应偏离直线路径（绕开孩子）");
    }
}

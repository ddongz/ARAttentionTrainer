// Assets/Tests/EditMode/FirstSearchLogicTests.cs
// 首寻漫游纯逻辑单测（2026-11-24 连续游走版）：
//   起点在视野中央 / 连续平滑左右游走 / 不转到身后、不走远 / 房间夹紧 / 语音节拍 / 停止。
using NUnit.Framework;
using UnityEngine;

public class FirstSearchLogicTests
{
    static Bounds BigRoom() => new Bounds(new Vector3(0f, 1.5f, 0f), new Vector3(10f, 3f, 10f));

    static FirstSearchLogic Started(Vector3 forward = default, Bounds? room = null)
    {
        var l = new FirstSearchLogic(moveSpeed: 0.5f, radius: 1.0f, holdSeconds: 0.6f, voiceInterval: 4f);
        Vector3 f = forward.sqrMagnitude < 1e-4f ? Vector3.forward : forward.normalized;
        l.Begin(new Vector3(0f, 1.6f, 0f), f, 0f, room ?? BigRoom());
        return l;
    }

    [Test]
    public void Begin_StartsInViewCenter_AndSpeaksWithinQuarterSecond()
    {
        var l = Started();
        Assert.IsTrue(l.Active);
        // t=0：正前方约 1m、略低于眼平 → 视野中央
        var p = l.CurrentTarget;
        Assert.AreEqual(0f, p.x, 0.02f);
        Assert.AreEqual(1.0f, p.z, 0.15f);
        Assert.AreEqual(1.55f, p.y, 0.06f);

        var r = l.Tick(0.2f);
        Assert.IsTrue(r.active);
        Assert.IsTrue(r.voiceDue, "搜索开始后 0.2s 内应说第一句");
        Assert.AreEqual(0, r.voiceSlot);
        Assert.AreEqual(1, l.VoiceCount);
    }

    [Test]
    public void Wander_IsContinuousAndStaysInFront()
    {
        var l = Started();
        var wps = l.PreviewWaypoints(24f, 48);
        Assert.GreaterOrEqual(wps.Length, 8);

        float maxMove = 0f;
        for (int i = 0; i < wps.Length; i++)
        {
            var wp = wps[i];
            float dist = new Vector2(wp.x, wp.z).magnitude;
            Assert.GreaterOrEqual(dist, 0.5f, "不能贴脸");
            Assert.LessOrEqual(dist, 1.3f, "不能走远（否则画面里太小）");
            float az = Mathf.Atan2(wp.x, wp.z) * Mathf.Rad2Deg;
            Assert.LessOrEqual(Mathf.Abs(az), 70f, "留在正前方视野附近，不转到身后");
            Assert.LessOrEqual(Mathf.Abs(wp.y - 1.6f), 0.25f, "应在眼平附近");
            if (i > 0) maxMove = Mathf.Max(maxMove, Vector3.Distance(wps[i - 1], wps[i]));
        }
        Assert.Greater(Vector3.Distance(wps[0], wps[wps.Length / 2]), 0.25f,
                       "路径应真的左右游走，而不是停在原地");
        Assert.Less(maxMove, 0.55f, "连续路径相邻采样不应瞬移");
    }

    [Test]
    public void Waypoints_StayInsideRoomBounds()
    {
        var room = new Bounds(new Vector3(0f, 1.5f, 0f), new Vector3(6f, 3f, 6f));
        var l = Started(room: room);
        foreach (var wp in l.PreviewWaypoints(24f, 24))
        {
            Assert.GreaterOrEqual(wp.x, room.min.x + 0.3f);
            Assert.LessOrEqual(wp.x, room.max.x - 0.3f);
            Assert.GreaterOrEqual(wp.z, room.min.z + 0.3f);
            Assert.LessOrEqual(wp.z, room.max.z - 0.3f);
            Assert.GreaterOrEqual(wp.y, 0.75f);
            Assert.LessOrEqual(wp.y, room.max.y - 0.25f);
        }
    }

    [Test]
    public void SmallRoom_ClampsWaypointsInside()
    {
        var room = new Bounds(new Vector3(0f, 1.5f, 0f), new Vector3(1.4f, 2.6f, 1.4f));
        var l = Started(room: room);
        foreach (var wp in l.PreviewWaypoints(24f, 24))
        {
            Assert.GreaterOrEqual(wp.x, room.min.x + 0.3f - 1e-3f);
            Assert.LessOrEqual(wp.x, room.max.x - 0.3f + 1e-3f);
            Assert.GreaterOrEqual(wp.z, room.min.z + 0.3f - 1e-3f);
            Assert.LessOrEqual(wp.z, room.max.z - 0.3f + 1e-3f);
        }
    }

    [Test]
    public void Tick_MovesContinuously_NotStuck()
    {
        var l = Started();
        var seen = new System.Collections.Generic.HashSet<Vector3>();
        float moved = 0f;
        Vector3 prev = l.CurrentTarget;
        for (int i = 0; i < 30 * 20; i++)
        {
            var r = l.Tick(1f / 30f);
            seen.Add(r.target);
            moved += Vector3.Distance(prev, r.target);
            prev = r.target;
        }
        Assert.Greater(moved, 1.5f, "20 秒内应平滑游走一段距离");
        Assert.Greater(seen.Count, 30, "目标应持续变化（连续路径）");
    }

    [Test]
    public void Voice_RepeatsAtInterval_WithoutImmediateRepeat()
    {
        var l = Started();
        int lastSlot = -1, due = 0;
        for (int i = 0; i < 30 * 13; i++)
        {
            var r = l.Tick(1f / 30f);
            if (!r.voiceDue) continue;
            due++;
            Assert.AreNotEqual(lastSlot, r.voiceSlot, "连续两句不能是同一句");
            lastSlot = r.voiceSlot;
        }
        Assert.GreaterOrEqual(due, 3, "13 秒至少 3 句（4s 间隔）");
        Assert.LessOrEqual(due, 4, "13 秒不应超过 4 句（避免话痨）");
    }

    [Test]
    public void Stop_EndsMovementAndVoice()
    {
        var l = Started();
        Assert.IsTrue(l.Active);
        Assert.IsTrue(l.Tick(0.2f).active);
        l.Stop();
        Assert.IsFalse(l.Active);
        var r = l.Tick(5f);
        Assert.IsFalse(r.active);
        Assert.IsFalse(r.voiceDue);
    }

    [Test]
    public void Forward_OrientationRotatesPattern()
    {
        var l = Started(new Vector3(1f, 0f, 0f));
        float maxX = float.MinValue;
        foreach (var wp in l.PreviewWaypoints(24f, 24)) maxX = Mathf.Max(maxX, wp.x);
        Assert.Greater(maxX, 0.8f, "路径应随孩子初始朝向旋转（前方 +X 有路径点）");
    }
}

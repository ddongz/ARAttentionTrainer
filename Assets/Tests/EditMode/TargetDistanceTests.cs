// Assets/Tests/EditMode/TargetDistanceTests.cs
// 老师锚点距离合理性（2026-10-21）：环境射线可能打到课桌/墙，把老师拉到 0.5m，
// 必须与身高估计交叉校验；否则星路端点过近 → 一团星星。
using NUnit.Framework;

public class TargetDistanceTests
{
    [Test]
    public void SmallBox_DeskHitAtHalfMeter_IsRejected()
    {
        // height01=0.118 → 身高估计 7.5m；射线 0.55m 明显是课桌/墙
        float d = IPWorldView.ResolveTargetDistance(7.5f, 0.55f, true, 0.118f);
        Assert.AreEqual(7.5f, d, 0.01f);
    }

    [Test]
    public void PlausibleRayHit_IsPreferred()
    {
        // 身高估计 2.1m，射线 2.0m：一致，采用射线（深度更准）
        float d = IPWorldView.ResolveTargetDistance(2.1f, 2.0f, true, 0.7f);
        Assert.AreEqual(2.0f, d, 0.01f);
    }

    [Test]
    public void NoRayHit_UsesHeightEstimate()
    {
        float d = IPWorldView.ResolveTargetDistance(3.0f, 0f, false, 0.5f);
        Assert.AreEqual(3.0f, d, 0.01f);
    }

    [Test]
    public void SeatedClosePerson_PlausibleRayHitIsAccepted()
    {
        // 坐姿近距离：身高估计 1.5m，射线 1.1m（0.73 倍）在合理带内
        float d = IPWorldView.ResolveTargetDistance(1.5f, 1.1f, true, 0.9f);
        Assert.AreEqual(1.1f, d, 0.01f);
    }

    [Test]
    public void DeskRayHit_AtOneMeter_IsRejected()
    {
        // 2026-11-03 真机：distH=2.8m、ray=1.1m（桌面），旧逻辑接受 0.35~1.8 倍 → 锚点被拉到桌面
        float d = IPWorldView.ResolveTargetDistance(2.8f, 1.1f, true, 0.55f);
        Assert.AreEqual(2.8f, d, 0.01f);
    }

    [Test]
    public void HorizontalSurfaceHit_IsRejected()
    {
        // 射线法线朝上 = 桌面/地面，不是人
        float d = IPWorldView.ResolveTargetDistance(2.8f, 2.6f, true, 0.55f, hitHorizontal: true);
        Assert.AreEqual(2.8f, d, 0.01f);
    }

    [Test]
    public void FarWallHit_IsRejected()
    {
        // 身高估计 1.2m，射线 6m（打到后面的墙）：不采用
        float d = IPWorldView.ResolveTargetDistance(1.2f, 6f, true, 0.8f);
        Assert.AreEqual(1.2f, d, 0.01f);
    }
}

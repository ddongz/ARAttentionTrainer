// Assets/Tests/EditMode/SelfieSegEdgeTests.cs
// 轮廓边缘提取（SelfieSegRunner.TryGetEdgeStatic）单元测试：合成掩码验证左右边缘定位。
using NUnit.Framework;
using UnityEngine;

public class SelfieSegEdgeTests
{
    const int W = 8, H = 8;

    /// <summary>合成掩码：列 3..5、行 2..6 处是"人体"（值 1），其余 0。</summary>
    static float[] MakeMask()
    {
        var m = new float[W * H];
        for (int y = 2; y <= 6; y++)
        for (int x = 3; x <= 5; x++)
            m[y * W + x] = 1f;
        return m;
    }

    [Test]
    public void LeftEdge_FindsLeftmostColumnOfBlob()
    {
        var m = MakeMask();
        Assert.IsTrue(SelfieSegRunner.TryGetEdgeStatic(m, W, H, 0.5f, 0f, 1f, true, 0.5f, out var e));
        Assert.AreEqual((3 + 0.5f) / W, e.x, 1e-4f, "左缘应命中最外侧人体列(3)");
    }

    [Test]
    public void RightEdge_FindsRightmostColumnOfBlob()
    {
        var m = MakeMask();
        Assert.IsTrue(SelfieSegRunner.TryGetEdgeStatic(m, W, H, 0.5f, 0f, 1f, false, 0.5f, out var e));
        Assert.AreEqual((5 + 0.5f) / W, e.x, 1e-4f, "右缘应命中最外侧人体列(5)");
    }

    [Test]
    public void EmptyMask_ReturnsFalse()
    {
        var m = new float[W * H]; // 全 0
        Assert.IsFalse(SelfieSegRunner.TryGetEdgeStatic(m, W, H, 0.5f, 0f, 1f, true, 0.5f, out _));
    }

    [Test]
    public void SearchRange_Respected()
    {
        var m = MakeMask(); // 人体列 3..5（跨 0.5 线：列 4-5 在右半区）
        // 右半区间找"左缘" → 命中最外侧落在该区间的列 4
        Assert.IsTrue(SelfieSegRunner.TryGetEdgeStatic(m, W, H, 0.5f, 0.5f, 1f, true, 0.5f, out var el));
        Assert.AreEqual((4 + 0.5f) / W, el.x, 1e-4f);
        // 右半区间找"右缘" → 列 5
        Assert.IsTrue(SelfieSegRunner.TryGetEdgeStatic(m, W, H, 0.5f, 0.5f, 1f, false, 0.5f, out var e));
        Assert.AreEqual((5 + 0.5f) / W, e.x, 1e-4f);

        // 人体严格在左半（列 1..2）→ 右半区间找左缘应找不到
        var mLeft = new float[W * H];
        for (int y = 2; y <= 6; y++)
            for (int x = 1; x <= 2; x++)
                mLeft[y * W + x] = 1f;
        Assert.IsFalse(SelfieSegRunner.TryGetEdgeStatic(mLeft, W, H, 0.5f, 0.5f, 1f, true, 0.5f, out _));
    }

    [Test]
    public void Crop_ContainsPersonBoxAndStaysInside()
    {
        var box = new Rect(0.40f, 0.30f, 0.20f, 0.40f);
        Rect c = SelfieSegRunner.ComputeCrop(box);
        Assert.LessOrEqual(c.xMin, box.xMin + 1e-4f);
        Assert.GreaterOrEqual(c.xMax, box.xMax - 1e-4f);
        Assert.LessOrEqual(c.yMin, box.yMin + 1e-4f);
        Assert.GreaterOrEqual(c.yMax, box.yMax - 1e-4f);
        Assert.GreaterOrEqual(c.xMin, 0f); Assert.LessOrEqual(c.xMax, 1f);
        Assert.GreaterOrEqual(c.yMin, 0f); Assert.LessOrEqual(c.yMax, 1f);
        // 不额外扩成 16:9：高瘦的人框左右只加 24% 边距，不补大片背景
        Assert.Less(c.width, box.width * 1.30f);
    }

    [Test]
    public void Crop_NearEdge_StaysInside()
    {
        Rect c = SelfieSegRunner.ComputeCrop(new Rect(0.90f, 0.10f, 0.08f, 0.20f));
        Assert.GreaterOrEqual(c.xMin, 0f); Assert.LessOrEqual(c.xMax, 1f);
        Assert.GreaterOrEqual(c.yMin, 0f); Assert.LessOrEqual(c.yMax, 1f);
        Assert.Less(c.width, 0.08f * 1.30f);
    }

    [Test]
    public void Crop_InvalidBox_FallsBackToFullFrame()
    {
        Rect c = SelfieSegRunner.ComputeCrop(new Rect(0f, 0f, 0f, 0f));
        Assert.AreEqual(new Rect(0f, 0f, 1f, 1f), c);
    }
}

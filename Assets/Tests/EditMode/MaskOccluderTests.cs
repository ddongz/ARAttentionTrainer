// Assets/Tests/EditMode/MaskOccluderTests.cs
// 单应矩阵（MaskOccluder.ComputeRows）单元测试：用解析可验证的对齐相机配置锁定数学推导。
// 配置：眼相机与 PCA 相机基完全对齐（fwd=+Z, right=+X, up=+Y），
//       焦距 f=100，掩码 200×100 → tanX=1.0、tanY=0.5 时四角应精确映射到四角。
using NUnit.Framework;
using UnityEngine;

public class MaskOccluderTests
{
    static readonly Vector3[] Rows = new Vector3[3];

    static void BuildAligned(float tanX, float tanY, float f = 100f, float w = 200f, float h = 100f)
    {
        var fwd = new Vector3(0, 0, 1);
        var right = new Vector3(1, 0, 0);
        var up = new Vector3(0, 1, 0);
        MaskOccluder.ComputeRows(fwd, right, up, f, w, h, fwd, right, up, tanX, tanY, Rows);
    }

    static Vector2 Map(float u, float v)
    {
        var sw = new Vector3(u, v, 1f); // 左上原点输入
        var hsh = new Vector3(Vector3.Dot(Rows[0], sw), Vector3.Dot(Rows[1], sw), Vector3.Dot(Rows[2], sw));
        return new Vector2(hsh.x / hsh.z, hsh.y / hsh.z);
    }

    [Test]
    public void Center_MapsToCenter()
    {
        BuildAligned(1f, 0.5f);
        var m = Map(0.5f, 0.5f);
        Assert.AreEqual(0.5f, m.x, 1e-3f);
        Assert.AreEqual(0.5f, m.y, 1e-3f);
    }

    [Test]
    public void Corners_MapToCorners_WhenFovMatches()
    {
        BuildAligned(1f, 0.5f); // tanX·sx = 1·(100/200)=0.5 → 屏角 ↔ 掩码角
        var c = Map(0f, 0f); Assert.AreEqual(0f, c.x, 1e-3f); Assert.AreEqual(0f, c.y, 1e-3f);   // 左上
        c = Map(1f, 0f);     Assert.AreEqual(1f, c.x, 1e-3f); Assert.AreEqual(0f, c.y, 1e-3f);   // 右上
        c = Map(0f, 1f);     Assert.AreEqual(0f, c.x, 1e-3f); Assert.AreEqual(1f, c.y, 1e-3f);   // 左下
        c = Map(1f, 1f);     Assert.AreEqual(1f, c.x, 1e-3f); Assert.AreEqual(1f, c.y, 1e-3f);   // 右下
    }

    [Test]
    public void CenterColumn_MapsStraightDown()
    {
        BuildAligned(1f, 0.5f);
        var m0 = Map(0.5f, 0f);
        var m1 = Map(0.5f, 1f);
        Assert.AreEqual(0.5f, m0.x, 1e-3f);
        Assert.AreEqual(0.5f, m1.x, 1e-3f, "中列应保持 u=0.5（无扭转）");
        Assert.Less(m0.y, m1.y, "顶部 y 应小于底部 y（v 向下）");
    }

    [Test]
    public void TextureFocal_IsScaledToMaskPixels()
    {
        // 真机：PCA 纹理 960×544，focal=800（纹理像素），掩码 256×144。
        // 直接把 800 传进 mask 像素会放大约 3.75 倍；先缩放到掩码像素再映射。
        const float texW = 960f, texH = 544f, focal = 800f;
        const int maskW = 256, maskH = 144;
        float tanX = (texW * 0.5f) / focal;
        float tanY = (texH * 0.5f) / focal;
        float fx = focal * maskW / texW;
        float fy = focal * maskH / texH;

        var fwd = new Vector3(0, 0, 1);
        var right = new Vector3(1, 0, 0);
        var up = new Vector3(0, 1, 0);
        MaskOccluder.ComputeRows(fwd, right, up, fx, fy, maskW, maskH,
                                 fwd, right, up, tanX, tanY, Rows);

        var c = Map(0f, 0f); Assert.AreEqual(0f, c.x, 1e-3f); Assert.AreEqual(0f, c.y, 1e-3f);
        c = Map(1f, 0f);     Assert.AreEqual(1f, c.x, 1e-3f); Assert.AreEqual(0f, c.y, 1e-3f);
        c = Map(0f, 1f);     Assert.AreEqual(0f, c.x, 1e-3f); Assert.AreEqual(1f, c.y, 1e-3f);
        c = Map(1f, 1f);     Assert.AreEqual(1f, c.x, 1e-3f); Assert.AreEqual(1f, c.y, 1e-3f);
        c = Map(0.5f, 0.5f); Assert.AreEqual(0.5f, c.x, 1e-3f); Assert.AreEqual(0.5f, c.y, 1e-3f);
    }
}

// Assets/Scripts/Editor/SegHighlightRenderProbe.cs
// 2026-11-03：不走真机，先在编辑器里把 SegMaskHighlight 着色器/四边形渲染一遍。
// 目的：把“着色器没编译/四边形没画出来”和“真机门控/映射问题”分开。
// 用法：Unity.exe -batchmode -projectPath ... -executeMethod SegHighlightRenderProbe.Run -quit
// 产物：Builds/seg-highlight-probe.png + 控制台断言日志。
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public static class SegHighlightRenderProbe
{
    const int W = 640, H = 360, MW = 256, MH = 144;

    public static void Run()
    {
        var camGo = new GameObject("ProbeCamera");
        var cam = camGo.AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
        cam.nearClipPlane = 0.1f;
        cam.farClipPlane = 10f;
        cam.fieldOfView = 60f;
        cam.aspect = 16f / 9f;
        cam.transform.position = Vector3.zero;
        cam.transform.rotation = Quaternion.identity;

        // 掩码：顶部一条横带（top-left 0.4..0.6, 0.1..0.3）——不对称，能检出上下翻转
        var mask = new Texture2D(MW, MH, TextureFormat.R8, false);
        var bytes = new byte[MW * MH];
        for (int y = 0; y < MH; y++)
        for (int x = 0; x < MW; x++)
        {
            float u = (x + 0.5f) / MW, v = (y + 0.5f) / MH;
            bytes[y * MW + x] = (u > 0.4f && u < 0.6f && v > 0.1f && v < 0.3f) ? (byte)255 : (byte)0;
        }
        mask.SetPixelData(bytes, 0);
        mask.Apply(false);

        var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
        Object.DestroyImmediate(quad.GetComponent<Collider>());
        quad.name = "ProbeQuad";
        quad.transform.position = new Vector3(0f, 0f, 0.5f);
        quad.transform.localScale = new Vector3(2f, 2f, 1f);

        var mat = Resources.Load<Material>("SegMaskHighlight");
        if (mat != null) mat = new Material(mat);
        else
        {
            var sh = Shader.Find("ARAttention/SegMaskHighlight");
            if (sh == null) { Fail("找不到 SegMaskHighlight 着色器"); return; }
            mat = new Material(sh);
        }
        mat.SetVector("_Row0", new Vector4(1f, 0f, 0f, 0f));
        mat.SetVector("_Row1", new Vector4(0f, 1f, 0f, 0f));
        mat.SetVector("_Row2", new Vector4(0f, 0f, 1f, 0f));
        mat.SetVector("_TargetRect", new Vector4(0f, 0f, 1f, 1f));
        mat.SetColor("_Color", new Color(0.1f, 0.9f, 1f, 1f));
        mat.SetFloat("_EdgeLow", 0.4f);
        mat.SetFloat("_EdgeHigh", 0.7f);
        mat.SetFloat("_Fill", 1f);
        mat.SetFloat("_Rim", 1f);
        mat.SetTexture("_MaskTex", mask);
        quad.GetComponent<MeshRenderer>().sharedMaterial = mat;

        var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32);
        rt.Create();
        var req = new UniversalRenderPipeline.SingleCameraRequest { destination = rt };
        RenderPipeline.SubmitRenderRequest(cam, req);

        RenderTexture.active = rt;
        var read = new Texture2D(W, H, TextureFormat.RGBA32, false);
        read.ReadPixels(new Rect(0, 0, W, H), 0, 0);
        read.Apply();

        // ReadPixels 的 y=0 在底部：掩码横带在“图像顶部”，应出现在 rt 的高 y 处
        Color top = read.GetPixel(W / 2, Mathf.RoundToInt(H * 0.8f));
        Color bottom = read.GetPixel(W / 2, Mathf.RoundToInt(H * 0.2f));
        Color outside = read.GetPixel(W / 10, H / 2);
        bool centerOk = top.g > 0.15f && top.b > 0.15f && top.a > 0.1f;
        bool outsideOk = bottom.a < 0.05f && outside.a < 0.05f;

        string path = Path.Combine(Application.dataPath, "../Builds/seg-highlight-probe.png");
        File.WriteAllBytes(path, read.EncodeToPNG());
        Debug.Log($"[Probe] top={top} bottom={bottom} outside={outside} path={Path.GetFullPath(path)}");
        Debug.Log(centerOk && outsideOk
            ? "[Probe] ✅ 着色器/四边形渲染正常：掩码横带在画面顶部（无上下翻转），外部透明"
            : $"[Probe] ❌ 渲染异常：centerOk={centerOk} outsideOk={outsideOk}");

        cam.targetTexture = null;
        RenderTexture.active = null;
        rt.Release();
        Object.DestroyImmediate(rt);
        Object.DestroyImmediate(read);
        Object.DestroyImmediate(mask);
        Object.DestroyImmediate(quad);
        Object.DestroyImmediate(camGo);
        EditorApplication.Exit(centerOk && outsideOk ? 0 : 2);
    }

    static void Fail(string msg)
    {
        Debug.LogError("[Probe] " + msg);
        EditorApplication.Exit(2);
    }
}

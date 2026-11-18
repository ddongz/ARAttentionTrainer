// Assets/Scripts/Detection/SelfieSegRunner.cs
// MediaPipe Selfie Segmentation（ONNX: 输入 1×3×144×256，输出 1×1×144×256 人体 alpha 掩码）。
// 用途（用户定调 2026-10-10）：让精灵与真实人体/人脸轮廓"贴合"互动——精灵沿真实轮廓探出/躲藏。
// 策略：轮廓比人脸位置变化慢 → 节流 0.5s 一帧（约 2Hz），省 CPU 且够用。
// 本组件只产数据（掩码统计 + 轮廓边缘查询）；视觉消费在 IPWorldView。
using System.Collections;
using Unity.InferenceEngine;
using UnityEngine;

public class SelfieSegRunner : MonoBehaviour
{
    [Header("引用")]
    public FrameSourceBehaviour frameSource;
    public ModelAsset modelAsset;   // Assets/Models/selfie_seg_landscape.onnx

    [Header("参数")]
    [Tooltip("推理间隔（秒）")]
    public float intervalSeconds = 0.5f;
    [Tooltip("边缘判定阈值（掩码值）")]
    public float edgeThreshold = 0.5f;

    /// <summary>最近一次推理+回读耗时（毫秒）。</summary>
    public float LastMs { get; private set; }
    /// <summary>掩码覆盖率 [0,1]（人体占画面比例）。</summary>
    public float Coverage { get; private set; }
    /// <summary>掩码峰值（诊断用：正常有人时应接近 1）。</summary>
    public float MaxValue { get; private set; }
    /// <summary>掩码版本号：每完成一帧自增（消费方用于"仅在新掩码时更新"）。</summary>
    public int Version { get; private set; }
    /// <summary>人体掩码纹理（RFloat，W×H；供 MaskOccluder 的擦除着色器采样）。</summary>
    public Texture2D MaskTexture { get; private set; }
    /// <summary>是否已产出过至少一帧有效掩码。</summary>
    public bool HasMask { get; private set; }
    /// <summary>是否允许推理（2026-11-03：等人+脸都检测到再由驱动开启）。</summary>
    public bool Active { get; private set; }
    public int MaskWidth => W;
    public int MaskHeight => H;

    /// <summary>2026-11-18：当前要求 seg 看的 YOLO 目标人框（归一化，左上原点）。全 0 表示全画面。</summary>
    public Rect Crop01 { get; private set; } = new Rect(0f, 0f, 1f, 1f);
    /// <summary>产出当前掩码时实际使用的裁剪框——高亮映射必须用它，不能用正在变化的新框。</summary>
    public Rect MaskCrop01 { get; private set; } = new Rect(0f, 0f, 1f, 1f);
    RenderTexture _cropRT;

    /// <summary>驱动层控制：人体+人脸都确认后才开 seg；丢失后关掉，省 CPU。</summary>
    public void SetActive(bool on)
    {
        if (Active == on) return;
        Active = on;
        if (!on) HasMask = false; // 隐藏高亮；保留最后一帧纹理内容不影响
        Debug.Log(on ? "[Seg] 人+脸已确认 → 开启分割" : "[Seg] 人/脸丢失 → 暂停分割");
    }

    /// <summary>2026-11-18 用户方案：只把 YOLO 目标人框（含少量边距）送 seg，
    /// 避免窗户/白板/反光占满模型输入导致“两团蓝雾”。box01 无效时回退全画面。</summary>
    public void SetTargetBox(Rect box01)
    {
        Crop01 = ComputeCrop(box01);
    }

    /// <summary>纯函数（可单测）：只取 YOLO 人框 + 少量边距，**不再扩成 16:9**。
    /// 16:9 扩宽会把远处瘦高的人框左右补进大量背景，反而让模型继续误检窗户/白板；
    /// 直接把人框拉伸到 256×144 送模型，背景最少。</summary>
    public static Rect ComputeCrop(Rect box, float margin = 0.12f)
    {
        if (box.width <= 0.01f || box.height <= 0.01f || float.IsNaN(box.x) || float.IsNaN(box.y))
            return new Rect(0f, 0f, 1f, 1f);

        float x0 = Mathf.Clamp01(box.x - box.width * margin);
        float y0 = Mathf.Clamp01(box.y - box.height * margin);
        float x1 = Mathf.Clamp01(box.xMax + box.width * margin);
        float y1 = Mathf.Clamp01(box.yMax + box.height * margin);
        float w = x1 - x0, h = y1 - y0;
        if (w <= 0.01f || h <= 0.01f) return new Rect(0f, 0f, 1f, 1f);
        return new Rect(x0, y0, w, h);
    }

    static bool IsFullCrop(Rect r)
        => r.x <= 0.001f && r.y <= 0.001f && r.width >= 0.999f && r.height >= 0.999f;

    const int W = 256, H = 144;
    Worker _worker;
    Tensor<float> _input;
    readonly float[] _mask = new float[H * W];
    readonly byte[] _maskBytes = new byte[H * W]; // R8 纹理上传（移动 GPU 上 R8 必然支持线性滤波；RFloat 常不支持）
    int _runs;

    void Awake()
    {
        if (modelAsset == null || frameSource == null)
        {
            Debug.LogWarning("[Seg] 缺少模型或帧源，已禁用（不影响其他功能）");
            enabled = false;
            return;
        }
        var model = ModelLoader.Load(modelAsset);
        _worker = new Worker(model, BackendType.CPU);
        _input = new Tensor<float>(new TensorShape(1, 3, H, W));

        MaskTexture = new Texture2D(W, H, TextureFormat.R8, false)
        {
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
        };
        // sRGB：保持与源相机纹理相同的颜色编码，避免 Blit 到线性 RT 后 seg 输入偏色
        _cropRT = new RenderTexture(W, H, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
        {
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
        };
        _cropRT.Create();

        Debug.Log($"[Seg] SelfieSeg 模型就绪（输入 {W}x{H}，CPU）");
        StartCoroutine(Loop());
    }

    IEnumerator Loop()
    {
        while (true)
        {
            // 2026-11-03：人+脸都确认后驱动才 Active；未激活时不推理（模型仍已加载）
            if (!Active) { yield return null; continue; }
            yield return new WaitForSeconds(intervalSeconds);
            if (!Active) continue;
            if (frameSource == null || !frameSource.IsReady) continue;
            if (!frameSource.TryGetFrame(out var frame) || !frame.valid || frame.texture == null) continue;
            yield return RunOnce(frame);
        }
    }

    IEnumerator RunOnce(CameraFrame frame)
    {
        float t0 = Time.realtimeSinceStartup;

        // 2026-11-18：只把 YOLO 人框裁剪区域送进模型；全画面时直接复用源纹理。
        Rect crop = Crop01;
        Texture src = frame.texture;
        if (!IsFullCrop(crop))
        {
            // 检测框是左上原点；Graphics.Blit 的 offset 是左下原点，需要翻 y
            var scale = new Vector2(crop.width, crop.height);
            var offset = new Vector2(crop.x, 1f - crop.y - crop.height);
            Graphics.Blit(frame.texture, _cropRT, scale, offset);
            src = _cropRT;
        }
        var textureTransform = new TextureTransform();
        TextureConverter.ToTensor(src, _input, textureTransform);
        _worker.Schedule(_input);

        var await = (_worker.PeekOutput(0) as Tensor<float>).ReadbackAndCloneAsync().GetAwaiter();
        while (!await.IsCompleted) yield return null;

        using var outTensor = await.GetResult();
        var arr = outTensor.DownloadToArray();
        float sum = 0f, maxV = 0f;
        int n = Mathf.Min(arr.Length, _mask.Length);
        for (int i = 0; i < n; i++)
        {
            float v = arr[i];
            _mask[i] = v;
            _maskBytes[i] = (byte)Mathf.Clamp(Mathf.RoundToInt(v * 255f), 0, 255);
            sum += v;
            if (v > maxV) maxV = v;
        }
        Coverage = n > 0 ? sum / n : 0f;
        MaxValue = maxV;
        LastMs = (Time.realtimeSinceStartup - t0) * 1000f;
        Version++;

        MaskTexture.SetPixelData(_maskBytes, 0);
        MaskTexture.Apply(false);
        MaskCrop01 = crop; // 高亮用“产出这帧掩码时的裁剪框”，避免目标移动导致错位
        HasMask = true;

        // 每 4 帧（约 2 秒）打一条诊断
        if (++_runs % 4 == 0)
        {
            bool hasL = TryGetEdge(0.4f, 0f, 0.5f, true, out var l);
            bool hasR = TryGetEdge(0.4f, 0.5f, 1f, false, out var r);
            Debug.Log($"[Seg] {LastMs:F0}ms 覆盖 {Coverage:P0} 峰值 {MaxValue:F2} | 左缘 {(hasL ? l.x.ToString("F2") : "--")} 右缘 {(hasR ? r.x.ToString("F2") : "--")}");
        }
    }

    /// <summary>
    /// 在掩码上找"某高度带内、指定侧的轮廓边缘点"（归一化坐标，左上原点，与检测框一致）。
    /// 例：找脸部高度附近人体左侧轮廓 → 精灵从真实身体左缘探出。
    /// </summary>
    public bool TryGetEdge(float y01, float xMin01, float xMax01, bool leftSide, out Vector2 edge01)
    {
        // 2026-11-18：掩码现在是裁剪框局部坐标；调用方仍传全画面坐标，这里转换并转回。
        Rect c = MaskCrop01;
        float yl = (y01 - c.y) / Mathf.Max(1e-5f, c.height);
        float x0 = (xMin01 - c.x) / Mathf.Max(1e-5f, c.width);
        float x1 = (xMax01 - c.x) / Mathf.Max(1e-5f, c.width);
        if (yl < -0.01f || yl > 1.01f || x1 < 0f || x0 > 1f)
        {
            edge01 = default;
            return false;
        }
        if (!TryGetEdgeStatic(_mask, W, H, Mathf.Clamp01(yl),
                              Mathf.Clamp01(x0), Mathf.Clamp01(x1),
                              leftSide, edgeThreshold, out var local))
        {
            edge01 = default;
            return false;
        }
        edge01 = new Vector2(c.x + local.x * c.width, c.y + local.y * c.height);
        return true;
    }

    /// <summary>纯函数版（单元测试覆盖）：在 宽w×高h 的掩码数组上找边缘。</summary>
    public static bool TryGetEdgeStatic(float[] mask, int w, int h,
        float y01, float xMin01, float xMax01, bool leftSide, float threshold, out Vector2 edge01)
    {
        edge01 = default;

        int yc = Mathf.Clamp(Mathf.RoundToInt(y01 * h), 0, h - 1);
        int band = Mathf.Max(1, Mathf.RoundToInt(0.035f * h)); // 上下各扫若干行，取最外侧
        int x0 = Mathf.Clamp(Mathf.FloorToInt(xMin01 * w), 0, w - 1);
        int x1 = Mathf.Clamp(Mathf.CeilToInt(xMax01 * w), 0, w - 1);
        if (x1 < x0) return false;

        int bestX = -1, bestY = -1;
        for (int y = Mathf.Max(0, yc - band); y <= Mathf.Min(h - 1, yc + band); y++)
        {
            int rowBase = y * w;
            if (leftSide)
            {
                for (int x = x0; x <= x1; x++)
                {
                    if (mask[rowBase + x] >= threshold)
                    {
                        if (bestX < 0 || x < bestX) { bestX = x; bestY = y; }
                        break;
                    }
                }
            }
            else
            {
                for (int x = x1; x >= x0; x--)
                {
                    if (mask[rowBase + x] >= threshold)
                    {
                        if (bestX < 0 || x > bestX) { bestX = x; bestY = y; }
                        break;
                    }
                }
            }
        }
        if (bestX < 0) return false;

        edge01 = new Vector2((bestX + 0.5f) / w, (bestY + 0.5f) / h);
        return true;
    }

    void OnDestroy()
    {
        if (MaskTexture != null) Destroy(MaskTexture);
        if (_cropRT != null) { _cropRT.Release(); Destroy(_cropRT); }
        if (_worker == null) return;
        _worker.PeekOutput(0)?.CompleteAllPendingOperations();
        _worker.Dispose();
        _input?.Dispose();
    }
}

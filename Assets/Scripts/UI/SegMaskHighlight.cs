// Assets/Scripts/UI/SegMaskHighlight.cs
// 2026-11-03：SelfieSeg 调试验证层。
//   人+脸都检测到 → 驱动把 SelfieSegRunner 置 Active → 这里把人体掩码用单应矩阵
//   映射回眼相机屏幕，叠一层青色高亮（轮廓更亮），用于人工检查分割是否贴合老师。
//   另外在视野右上角放一个原始掩码小窗：主高亮若因映射/门控不可见，小窗仍能确认分割结果。
//   本层只做“看得见的验证”；确认分割正确后，再推进精灵与真实人体的遮挡交互。
using Meta.XR;
using UnityEngine;
using UnityEngine.Rendering;

[DisallowMultipleComponent]
public class SegMaskHighlight : MonoBehaviour
{
    [Header("引用")]
    public SelfieSegRunner seg;
    public PcaFrameSource frameSource;
    public PassthroughCameraAccess pca;
    public Camera eyeCamera;

    [Header("外观")]
    [Tooltip("全屏四边形距眼睛的距离（米）。必须大于眼相机 near clip；真机单眼相机 near=0.3")]
    public float quadDistance = 0.5f;
    public float sizeMargin = 1.8f;
    public Color color = new Color(0.10f, 0.90f, 1.00f, 0.60f);
    [Tooltip("只强化这个目标人框内的掩码（归一化，左上原点）；全 0 则全画面")]
    public Rect targetBox01;

    [Header("原始掩码小窗（右上角）")]
    public bool showMaskPreview = true;
    // 2026-11-18：用户反馈右上角小窗“根本没看到”——挪到左上、往画面内侧，并放大；
    //   右上角会和数据面板/视野边缘重叠，左上更显眼。
    public float previewOffsetX = -0.26f;
    public float previewOffsetY = 0.18f;
    public float previewSize = 0.18f;

    Material _mat;
    MeshRenderer _rend;
    Material _previewMat;
    MeshRenderer _previewRend;
    readonly Vector3[] _rows = new Vector3[3];
    float _diagT;

    // 0.5m 处的全屏四边形（与 MaskOccluder 相同策略：XR 相机 FOV 不可信，直接放大）。
    // 2026-11-03：从 0.2m 改到 0.5m——真机单眼相机 near clip=0.3，0.2m 的四边形会被整个裁掉。
    void LateUpdate()
    {
        EnsureInit();
        if (_rend == null || _mat == null) return;

        bool on = seg != null && seg.Active && seg.HasMask && seg.MaskTexture != null
                  && frameSource != null && pca != null && eyeCamera != null;
        if (!on)
        {
            _rend.enabled = false;
            if (_previewRend != null) _previewRend.enabled = false;
            _diagT += Time.deltaTime;
            if (_diagT >= 2f)
            {
                _diagT = 0f;
                Debug.Log($"[SegHL] 未显示：active={(seg != null && seg.Active)} " +
                          $"mask={(seg != null && seg.HasMask)} tex={(seg != null && seg.MaskTexture != null)} " +
                          $"frame={frameSource != null} pca={pca != null} eye={eyeCamera != null} mat={_mat != null}");
            }
            return;
        }

        CameraFrame frame = default;
        if (!frameSource.TryGetFrame(out frame) || !frame.valid || frame.texture == null)
        {
            _rend.enabled = false;
            if (_previewRend != null) _previewRend.enabled = false;
            return;
        }

        // PCA 相机基（与检测框/精灵同一坐标链）
        Vector3 pF = pca.ViewportPointToRay(new Vector2(0.5f, 0.5f), frame.cameraPose).direction;
        Vector3 pR = Vector3.ProjectOnPlane(pca.ViewportPointToRay(new Vector2(1f, 0.5f), frame.cameraPose).direction, pF).normalized;
        Vector3 pU = Vector3.ProjectOnPlane(pca.ViewportPointToRay(new Vector2(0.5f, 0f), frame.cameraPose).direction, pF).normalized; // v=0 顶部

        // 眼相机基（兼容非对称投影）
        Vector3 eF = eyeCamera.ViewportPointToRay(new Vector3(0.5f, 0.5f)).direction;
        Vector3 eR = eyeCamera.ViewportPointToRay(new Vector3(1f, 0.5f)).direction;
        Vector3 eT = eyeCamera.ViewportPointToRay(new Vector3(0.5f, 1f)).direction;
        Vector3 eRv = Vector3.ProjectOnPlane(eR, eF).normalized;
        Vector3 eUv = Vector3.ProjectOnPlane(eT, eF).normalized;
        float tanX = Vector3.Project(eR, eRv).magnitude / Mathf.Max(1e-5f, Vector3.Project(eR, eF).magnitude);
        float tanY = Vector3.Project(eT, eUv).magnitude / Mathf.Max(1e-5f, Vector3.Project(eT, eF).magnitude);

        // 2026-11-03 关键修正：frame.focalLengthPx 是摄像头纹理像素焦距；掩码 256×144。
        //   直接用纹理焦距会让掩码映射放大约 3.75 倍（960/256），人物轮廓被推到屏幕外。
        float fx = frame.focalLengthPx * seg.MaskWidth / Mathf.Max(1, frame.texture.width);
        float fy = frame.focalLengthPx * seg.MaskHeight / Mathf.Max(1, frame.texture.height);
        MaskOccluder.ComputeRows(pF, pR, pU, fx, fy, seg.MaskWidth, seg.MaskHeight,
                                 eF, eRv, eUv, tanX, tanY, _rows);

        // 2026-11-18：掩码是 YOLO 人框裁剪的局部坐标；目标框也转到局部坐标
        Rect crop = seg.MaskCrop01;
        _mat.SetVector("_CropRect", new Vector4(crop.x, crop.y, crop.width, crop.height));
        Rect r = targetBox01;
        if (r.width > 0.01f && r.height > 0.01f && crop.width > 0.01f && crop.height > 0.01f)
        {
            r = new Rect((r.x - crop.x) / crop.width,
                         (r.y - crop.y) / crop.height,
                         r.width / crop.width,
                         r.height / crop.height);
            // 边距 15%，目标框略偏时不要把真实人体裁掉
            float mx = r.width * 0.15f, my = r.height * 0.15f;
            _mat.SetVector("_TargetRect",
                new Vector4(r.x - mx, r.y - my, r.width + 2f * mx, r.height + 2f * my));
        }
        else _mat.SetVector("_TargetRect", new Vector4(0f, 0f, 1f, 1f));

        _mat.SetVector("_Row0", _rows[0]);
        _mat.SetVector("_Row1", _rows[1]);
        _mat.SetVector("_Row2", _rows[2]);
        _mat.SetTexture("_MaskTex", seg.MaskTexture);
        _mat.SetColor("_Color", color);
        _rend.enabled = true;

        if (_previewRend != null && _previewMat != null)
        {
            _previewMat.SetTexture("_MaskTex", seg.MaskTexture);
            _previewRend.enabled = showMaskPreview;
        }

        _diagT += Time.deltaTime;
        if (_diagT >= 2f)
        {
            _diagT = 0f;
            Vector3 sw = new Vector3(0.5f, 0.5f, 1f);
            float hx = Vector3.Dot(_rows[0], sw);
            float hy = Vector3.Dot(_rows[1], sw);
            float hz = Vector3.Dot(_rows[2], sw);
            Vector2 muv = new Vector2(hx, hy) / Mathf.Max(1e-5f, Mathf.Abs(hz));
            int boxHits = SampleMaskBox(new Rect(0f, 0f, 1f, 1f), out int boxCount);
            Debug.Log($"[SegHL] 显示中：muvCenter={muv.ToString("F2")} crop={seg.MaskCrop01.ToString("F2")} " +
                      $"boxMask={boxHits}/{boxCount} coverage={seg.Coverage:P0} " +
                      $"cam={eyeCamera.name} near={eyeCamera.nearClipPlane:F2} " +
                      $"stereo={eyeCamera.stereoTargetEye} targetTex={(eyeCamera.targetTexture != null)} " +
                      $"rend={_rend.enabled} mat={_mat.shader.name}");
        }
    }

    /// <summary>在目标框内做 5×5 采样，确认掩码在框里确实有人（排查“框错/掩码翻”）。</summary>
    int SampleMaskBox(Rect r, out int count)
    {
        count = 0;
        if (seg == null || seg.MaskTexture == null) return 0;
        if (r.width <= 0.01f || r.height <= 0.01f) r = new Rect(0f, 0f, 1f, 1f);
        int w = seg.MaskWidth, h = seg.MaskHeight;
        int hits = 0;
        for (int y = 0; y < 5; y++)
        {
            for (int x = 0; x < 5; x++)
            {
                float u = Mathf.Clamp01(r.x + (x + 0.5f) / 5f * r.width);
                float v = Mathf.Clamp01(r.y + (y + 0.5f) / 5f * r.height); // 左上原点
                int px = Mathf.Clamp(Mathf.FloorToInt(u * w), 0, w - 1);
                int pyTop = Mathf.Clamp(Mathf.FloorToInt(v * h), 0, h - 1);
                // 掩码数据第 0 行 = 图像顶部，SetPixelData 后它位于 GetPixel 的 y=0；
                // 因此 GetPixel 的 y 直接就是“从上往下”的行号，不需要再翻。
                float m = seg.MaskTexture.GetPixel(px, pyTop).r;
                if (m >= 0.5f) hits++;
                count++;
            }
        }
        return hits;
    }

    void EnsureInit()
    {
        if (_rend != null) return;
        eyeCamera = FindEyeCamera(eyeCamera);
        if (eyeCamera == null)
        {
            Debug.LogError("[SegHL] 找不到眼相机，高亮不可用");
            return;
        }

        var go = new GameObject("SegMaskQuad");
        go.transform.SetParent(eyeCamera.transform, false);
        go.layer = eyeCamera.gameObject.layer;
        // 某些 XR 相机 cullingMask 不含 Default 层 → 四边形会被整层剔除
        if ((eyeCamera.cullingMask & (1 << go.layer)) == 0)
            eyeCamera.cullingMask |= 1 << go.layer;
        go.transform.localPosition = new Vector3(0f, 0f, quadDistance);
        go.transform.localRotation = Quaternion.identity;
        float size = Mathf.Max(2f, 2f * quadDistance * sizeMargin);
        go.transform.localScale = new Vector3(size, size, 1f);

        var mf = go.AddComponent<MeshFilter>();
        mf.sharedMesh = BuildQuad();

        _rend = go.AddComponent<MeshRenderer>();
        var baseMat = Resources.Load<Material>("SegMaskHighlight");
        if (baseMat != null)
        {
            _mat = new Material(baseMat);
        }
        else
        {
            var sh = Shader.Find("ARAttention/SegMaskHighlight");
            if (sh == null)
            {
                Debug.LogError("[SegHL] 找不到 SegMaskHighlight 着色器/材质，高亮不可用");
                return;
            }
            _mat = new Material(sh);
        }
        _mat.SetColor("_Color", color);
        _rend.sharedMaterial = _mat;
        _rend.shadowCastingMode = ShadowCastingMode.Off;
        _rend.receiveShadows = false;
        _rend.enabled = false;

        // ── 右上角原始掩码小窗 ──
        if (showMaskPreview)
        {
            var pv = new GameObject("SegMaskPreviewQuad");
            pv.transform.SetParent(eyeCamera.transform, false);
            pv.layer = eyeCamera.gameObject.layer;
            pv.transform.localPosition = new Vector3(previewOffsetX, previewOffsetY, quadDistance);
            pv.transform.localRotation = Quaternion.identity;
            pv.transform.localScale = new Vector3(previewSize * (16f / 9f), previewSize, 1f);
            var pvMf = pv.AddComponent<MeshFilter>();
            pvMf.sharedMesh = BuildQuad();
            _previewRend = pv.AddComponent<MeshRenderer>();
            var pvBase = Resources.Load<Material>("SegMaskPreview");
            var pvShader = Shader.Find("ARAttention/SegMaskPreview");
            if (pvBase != null) _previewMat = new Material(pvBase);
            else if (pvShader != null) _previewMat = new Material(pvShader);
            if (_previewMat != null)
            {
                _previewRend.sharedMaterial = _previewMat;
                _previewRend.shadowCastingMode = ShadowCastingMode.Off;
                _previewRend.receiveShadows = false;
                _previewRend.enabled = false;
            }
            else Debug.LogError("[SegHL] 找不到 SegMaskPreview 着色器/材质，小窗不可用");
        }

        Debug.Log($"[SegHL] 四边形就绪：cam={eyeCamera.name} near={eyeCamera.nearClipPlane:F2} " +
                  $"stereo={eyeCamera.stereoTargetEye} targetTex={(eyeCamera.targetTexture != null)} " +
                  $"layer={go.layer} cull={eyeCamera.cullingMask != 0} " +
                  $"dist={quadDistance:F2} mat={baseMat != null} " +
                  $"shader={(_mat.shader != null ? _mat.shader.name : "null")} preview={(_previewRend != null)}");
    }

    /// <summary>优先选真正渲染头显的相机：启用、无 targetTexture、stereo 相机；否则退回普通相机。</summary>
    static Camera FindEyeCamera(Camera current)
    {
        if (IsRenderCamera(current, false)) return current;

        if (IsRenderCamera(Camera.main, true)) return Camera.main;

        var cams = Object.FindObjectsByType<Camera>(FindObjectsSortMode.None);
        Camera fallback = null;
        foreach (var c in cams)
        {
            if (c == null || !c.isActiveAndEnabled) continue;
            if (c.targetTexture != null) continue; // 采集相机不用于显示
            if (c.stereoTargetEye != StereoTargetEyeMask.None) return c;
            if (fallback == null) fallback = c;
        }
        return fallback != null ? fallback : current;
    }

    static bool IsRenderCamera(Camera c, bool allowNonStereo)
    {
        if (c == null || !c.isActiveAndEnabled || c.targetTexture != null) return false;
        return allowNonStereo || c.stereoTargetEye != StereoTargetEyeMask.None;
    }

    static Mesh BuildQuad()
    {
        var m = new Mesh { name = "SegMaskQuad" };
        m.vertices = new[]
        {
            new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
            new Vector3(-0.5f, 0.5f, 0f), new Vector3(0.5f, 0.5f, 0f),
        };
        m.uv = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f) };
        m.triangles = new[] { 0, 2, 1, 1, 2, 3 };
        m.RecalculateBounds();
        return m;
    }

    void OnDestroy()
    {
        if (_mat != null) Destroy(_mat);
        if (_previewMat != null) Destroy(_previewMat);
    }
}

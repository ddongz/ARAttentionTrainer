// Assets/Scripts/UI/StardustPath.cs
// 星尘路径 v6（2026-10-20 用户实测"效果一模一样"后重做）：
//   根因①：占位贴图 star_soft.png 的内容只在左上 128×128 象限 → 每颗星偏在面片左上角、只有 1/4 大小。
//          现改用 star_dot.png（居中裁切）。
//   根因②：旧路径是"空中细弧线"，没有地面透视，读不出"一条路"。
//          现改为：视野前下方（可见）起步 → 落到地面 → 贴地绕过孩子 → 铺到老师脚下；
//          120 颗物理尺寸一致的星点（近大远小）形成真实 3D 透视的"星路"，两端淡出。
//   方向：终点 = 老师独立空间锚点（最后一次已知世界位置）的地面投影 + 身侧偏移；
//         老师位于背后时，控制点绕到孩子侧方（保证不穿过身体）。
// 调试：开启时每 2s 打一条 [Stardust] 日志（起点/终点/老师锚点/方位角/星数）。
using Meta.XR.MRUtilityKit;
using UnityEngine;
using UnityEngine.Rendering;

public class StardustPath : MonoBehaviour
{
    [Header("素材/引用")]
    public Material starMat;          // 星点材质（居中星图，GuideArrow shader）
    public Transform eye;             // 观察者（中心眼）
    public MrukBootstrap mruk;        // 可选：房间地板高度（没有则用 eyeHeight 估算）

    [Header("双锚点（2026-10-25 重做）")]
    [Tooltip("视野锚点：眼前下方多少米（水平）——起点在画面下缘附近")]
    public float startForward = 0.60f;
    [Tooltip("视野锚点：比眼睛低多少米——2026-11-18 从 0.95 抬高到 0.60，更多角度可见")]
    public float startDrop = 0.60f;
    [Tooltip("视野锚点显著移动距离（米）才重算星路")]
    public float viewAnchorUpdateDistance = 0.25f;
    [Tooltip("视野锚点显著转角（度）才重算星路")]
    public float viewAnchorUpdateAngle = 12f;
    [Tooltip("锚点变化持续这么久才更新（秒）——防抖")]
    public float anchorUpdateHold = 0.15f;

    [Header("几何")]
    [Tooltip("绕开孩子所需的最小侧向距离（米）")]
    public float detour = 0.95f;
    [Tooltip("终点相对老师锚点的侧向偏移（米）")]
    public float endSideOffset = 0.10f;
    [Tooltip("路尾离地高度（米）——2026-11-18 从贴地 0.05 抬高到 0.45，更多角度可见")]
    public float endHeight = 0.45f;
    [Tooltip("无房间数据时用于估算地板高度的眼高（米）")]
    public float eyeHeight = 1.5f;
    [Tooltip("铺路星点数量（上限；实际按路长定间距）")]
    public int starDots = 150;
    [Tooltip("星点基准尺寸（米）")]
    public float starDotSize = 0.10f;
    [Tooltip("路面最大半宽（米）——实际半宽还会随路长收缩")]
    public float roadHalfWidth = 0.28f;
    [Tooltip("中心线星点占比（0~1）：越高方向感越强")]
    [Range(0f, 0.8f)] public float centerlineFraction = 0.60f;
    [Tooltip("终点宽度相对起点的比例（锥形收窄）")]
    [Range(0.2f, 1f)] public float taper = 0.62f;

    [Header("指路彗星")]
    [Tooltip("彗星沿路飞行的速度（归一化/秒）")]
    public float cometSpeed = 0.55f;
    [Tooltip("彗星尺寸（米）")]
    public float cometSize = 0.26f;

    [Header("节奏/调试")]
    [Tooltip("中心线星点沿路向老师方向流动的速度（归一化/秒）")]
    public float flowSpeed = 0.08f;
    [Tooltip("显示两个空间锚点的实物标记（青=老师锚点，洋红=视野锚点）")]
    public bool showAnchors = true;
    [Tooltip("锚点标记尺寸（米）")]
    public float anchorMarkerSize = 0.10f;

    const int MaxDots = 192;      // 路径星 + 箭头 + chevron，留余量
    const int ArrowDots = 7;      // 终点贴地箭头（1 个箭尖 + 3 对翼）
    const int ChevronCount = 2;   // 沿途 V 形 chevron 数量
    const int ChevronDots = 5;    // 每个 chevron：1 个尖 + 2 对翼
    const int ChevronTotal = ChevronCount * ChevronDots;

    public bool IsOn { get; private set; }

    // ── 路径控制点（三次贝塞尔）──
    struct Path
    {
        public Vector3 p0, c1, c2, p3; // 起点/控制1/控制2/终点
    }

    Path _path;
    bool _hasPath, _built;
    StarPathAnchorLogic _anchors;
    float _flow, _diagT;

    Transform[] _dots;
    MeshRenderer[] _dotR;
    MaterialPropertyBlock _mpb;
    Transform _comet;
    MeshRenderer _cometR;
    Material _cometMat;
    float _cometT;

    // 中心线 + 锚点实物标记（2026-10-25）
    LineRenderer _centerLine;
    Transform _teacherMarker, _viewMarker;
    LineRenderer _teacherStem, _teacherRing;
    Material _markerMatCyan, _markerMatMagenta;

    /// <summary>新会话：清空两个锚点，重新锁定老师位置。</summary>
    public void ResetAnchors()
    {
        EnsureAnchors();
        _anchors.Reset();
        _hasPath = false;
        HidePathVisuals();
    }

    public Vector3 PathStart => _path.p0;
    public Vector3 PathEnd => _path.p3;
    public bool HasTeacherAnchor => _anchors != null && _anchors.HasTeacherAnchor;
    public Vector3 TeacherAnchor => HasTeacherAnchor ? _anchors.TeacherAnchor : Vector3.zero;
    public Vector3 ViewAnchor => _anchors != null && _anchors.HasViewAnchor ? _anchors.ViewAnchor : Vector3.zero;

    // 每颗星的确定性伪随机参数（构建时生成，运行时不变）
    float[] _jSide, _jUp, _sMul, _phase, _pick;

    // 最近一次的几何（诊断用）
    Vector3 _diagStart, _diagEnd, _diagTeacher;
    float _diagFloorY, _lastHalfWidth;
    int _diagDots;

    void Awake()
    {
        EnsureBuilt();
    }

    /// <summary>构建星点（预览工具也会显式调用；幂等）。</summary>
    public void EnsureBuilt()
    {
        if (_built) return;
        _built = true;
        _dots = new Transform[MaxDots];
        _dotR = new MeshRenderer[MaxDots];
        _mpb = new MaterialPropertyBlock();
        _jSide = new float[MaxDots];
        _jUp = new float[MaxDots];
        _sMul = new float[MaxDots];
        _phase = new float[MaxDots];
        _pick = new float[MaxDots];

        var shared = starMat != null ? starMat
                   : new Material(Shader.Find("ARAttention/GuideArrow"));
        for (int i = 0; i < MaxDots; i++)
        {
            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = "Star" + i;
            quad.transform.SetParent(transform, false);
            Destroy(quad.GetComponent<Collider>());
            var qr = quad.GetComponent<MeshRenderer>();
            qr.sharedMaterial = shared;                 // 共享材质 + MPB 逐星染色
            qr.shadowCastingMode = ShadowCastingMode.Off;
            qr.receiveShadows = false;
            qr.enabled = false;
            _dots[i] = quad.transform;
            _dotR[i] = qr;
            // 确定性伪随机（不依赖 UnityEngine.Random，测试/构建稳定）
            _jSide[i] = Hash01(i * 12.9898f + 3.7f) * 2f - 1f;
            _jUp[i] = Hash01(i * 78.233f + 9.1f);
            _sMul[i] = 0.62f + 0.75f * Hash01(i * 39.425f + 1.3f);
            _phase[i] = Hash01(i * 93.989f + 7.7f) * Mathf.PI * 2f;
            _pick[i] = Hash01(i * 53.717f + 5.5f);
        }

        // 指路彗星：一颗更亮的大星沿中心线飞向老师，给星路一个明确方向
        var comet = GameObject.CreatePrimitive(PrimitiveType.Quad);
        comet.name = "GuideComet";
        comet.transform.SetParent(transform, false);
        Destroy(comet.GetComponent<Collider>());
        _comet = comet.transform;
        _cometR = comet.GetComponent<MeshRenderer>();
        _cometMat = new Material(shared);
        _cometMat.SetColor("_Color", new Color(1f, 0.96f, 0.78f, 1f));
        _cometR.sharedMaterial = _cometMat;
        _cometR.shadowCastingMode = ShadowCastingMode.Off;
        _cometR.receiveShadows = false;
        _cometR.enabled = false;

        EnsureCenterLine(shared);
        EnsureDebugMarkers();
    }

    void EnsureCenterLine(Material shared)
    {
        if (_centerLine != null) return;
        var go = new GameObject("CenterLine");
        go.transform.SetParent(transform, false);
        _centerLine = go.AddComponent<LineRenderer>();
        _centerLine.useWorldSpace = true;
        _centerLine.positionCount = 0;
        _centerLine.startWidth = 0.035f;
        _centerLine.endWidth = 0.035f;
        _centerLine.numCapVertices = 4;
        _centerLine.alignment = LineAlignment.View;
        var mat = new Material(shared != null ? shared.shader : Shader.Find("ARAttention/GuideArrow"));
        mat.SetTexture("_MainTex", Texture2D.whiteTexture);
        mat.SetColor("_Color", new Color(1f, 0.93f, 0.68f, 0.55f));
        _centerLine.sharedMaterial = mat;
        _centerLine.startColor = new Color(1f, 0.93f, 0.68f, 0.55f);
        _centerLine.endColor = new Color(1f, 0.97f, 0.82f, 0.85f);
        _centerLine.shadowCastingMode = ShadowCastingMode.Off;
        _centerLine.receiveShadows = false;
        _centerLine.enabled = false;
    }

    void EnsureDebugMarkers()
    {
        if (_teacherMarker != null) return;
        _markerMatCyan = MakeUnlit(new Color(0.10f, 0.90f, 1.00f, 1f));
        _markerMatMagenta = MakeUnlit(new Color(1.00f, 0.25f, 0.90f, 1f));
        _teacherMarker = MakeSphere("AnchorTeacher", _markerMatCyan);
        _viewMarker = MakeSphere("AnchorView", _markerMatMagenta);
        _teacherStem = MakeLine("AnchorTeacherStem", _markerMatCyan, 0.012f);
        _teacherRing = MakeLine("AnchorTeacherRing", _markerMatCyan, 0.012f);
        _teacherMarker.gameObject.SetActive(false);
        _viewMarker.gameObject.SetActive(false);
        _teacherStem.enabled = false;
        _teacherRing.enabled = false;
    }

    static Material MakeUnlit(Color c)
    {
        // 用项目已打包的 GuideArrow 着色器（starMat 已引用，避免 URP Unlit 被剥离后标记不可见）
        var sh = Shader.Find("ARAttention/GuideArrow");
        if (sh == null) sh = Shader.Find("Universal Render Pipeline/Unlit");
        var m = new Material(sh);
        m.SetTexture("_MainTex", Texture2D.whiteTexture);
        m.SetColor("_BaseColor", c);
        m.SetColor("_Color", c);
        return m;
    }

    Transform MakeSphere(string name, Material mat)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.name = name;
        go.transform.SetParent(transform, false);
        Destroy(go.GetComponent<Collider>());
        var r = go.GetComponent<MeshRenderer>();
        r.sharedMaterial = mat;
        r.shadowCastingMode = ShadowCastingMode.Off;
        r.receiveShadows = false;
        go.transform.localScale = Vector3.one * anchorMarkerSize;
        return go.transform;
    }

    LineRenderer MakeLine(string name, Material mat, float width)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false);
        var lr = go.AddComponent<LineRenderer>();
        lr.useWorldSpace = true;
        lr.positionCount = 0;
        lr.startWidth = width;
        lr.endWidth = width;
        lr.numCapVertices = 2;
        lr.alignment = LineAlignment.View;
        lr.sharedMaterial = mat;
        lr.startColor = Color.white;
        lr.endColor = Color.white;
        lr.shadowCastingMode = ShadowCastingMode.Off;
        lr.receiveShadows = false;
        lr.enabled = false;
        return lr;
    }

    /// <summary>开/关星路（驱动层在"引导激活且孩子没看老师"时打开）。
    /// 注意：只隐藏星路本身；两个空间锚点的实物标记仍保持显示（调试用）。</summary>
    public void SetOn(bool on)
    {
        if (IsOn == on) return;
        IsOn = on;
        EnsureAnchors();
        if (on)
        {
            // 新一轮星路：只换视野锚点（路径起点），**老师锚点保持上次锁定的位置不动**
            _anchors.ResetView();
            _hasPath = false;
            _cometT = 0f;
            _flow = 0f;
            HidePathVisuals(); // 等锚点建立后再显示，避免旧路残留一帧
        }
        else HidePathVisuals();
    }

    void HidePathVisuals()
    {
        for (int i = 0; i < MaxDots; i++)
            if (_dotR[i] != null) _dotR[i].enabled = false;
        if (_cometR != null) _cometR.enabled = false;
        if (_centerLine != null) _centerLine.enabled = false;
    }

    void EnsureAnchors()
    {
        if (_anchors == null)
            _anchors = new StarPathAnchorLogic(viewAnchorUpdateDistance,
                                               viewAnchorUpdateAngle, anchorUpdateHold);
    }

    /// <summary>驱动层每帧调用。teacherPos=当前/最后已知老师世界位置；
    /// teacherValid=当前是否检测到老师（首次有效时锁定老师锚点，之后固定）。
    /// 即使星路关闭也会更新锚点标记（调试用）。</summary>
    public void Tick(float now, float dt, Vector3 teacherPos, bool teacherValid = true,
                     bool teacherVisible = true)
    {
        if (eye == null) return;
        EnsureBuilt();
        EnsureAnchors();
        Vector3 eyePos = eye.position;
        Vector3 fwd = eye.forward;
        fwd.y = 0f;
        if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
        fwd.Normalize();

        // ── 地板高度：优先 MRUK 房间数据，否则按眼高估算 ──
        float floorY = eyePos.y - eyeHeight;
        if (mruk != null && mruk.TryGetRoomBounds(out var room))
            floorY = room.min.y;

        // 老师超过 timeout 未检测到（driver 传 teacherVisible=false）：
        //   丢掉旧锚点，锚点标记/星路都不再指向空位置；下次看到老师再重新锁定。
        if (!teacherVisible) _anchors.ClearTeacherAnchor();

        // 星路关闭时：锚点标记仍要显示（调试）。
        // T0 修复：**不重置锚点**——老师锚点一次锁定后全程不动；
        // 只有视野锚点按显著性更新（洋红球是路径起点，可以随头动）。
        bool hadTeacherAnchor = _anchors.HasTeacherAnchor;
        if (!IsOn)
        {
            _anchors.Tick(dt, teacherValid, teacherPos, eyePos, fwd, startForward, startDrop);
            if (!hadTeacherAnchor && _anchors.HasTeacherAnchor)
                Debug.Log($"[Anchor] 锁定老师锚点 {_anchors.TeacherAnchor.ToString("F2")}（已确认人体脸）");
            UpdateDebugMarkers(floorY, teacherVisible);
            return;
        }

        // ── 双锚点：老师锚点固定；视野锚点显著移动才更新 ──
        _anchors.Tick(dt, teacherValid, teacherPos, eyePos, fwd, startForward, startDrop);
        if (!hadTeacherAnchor && _anchors.HasTeacherAnchor)
            Debug.Log($"[Anchor] 锁定老师锚点 {_anchors.TeacherAnchor.ToString("F2")}（已确认人体脸）");
        UpdateDebugMarkers(floorY, teacherVisible);

        if (!_anchors.HasTeacherAnchor || !_anchors.HasViewAnchor) return;

        // ── 仅在锚点变化时重算星路（稳定、不随每帧检测抖动）──
        if (_anchors.Dirty || !_hasPath)
        {
            Vector3 start = _anchors.ViewAnchor;
            Vector3 end = _anchors.TeacherAnchor;
            Vector3 dir = new Vector3(end.x - start.x, 0f, end.z - start.z);
            if (dir.sqrMagnitude > 1e-4f)
            {
                Vector3 side = Vector3.Cross(Vector3.up, dir.normalized);
                end += side * endSideOffset;
            }
            ComputeAnchoredPath(start, end, eyePos, fwd, floorY, detour, endHeight,
                                out _path.p0, out _path.c1, out _path.c2, out _path.p3);
            // 控制点不要穿墙：限制在 MRUK 房间内（留 0.3m 边距）
            if (mruk != null && mruk.TryGetRoomBounds(out var roomPath))
            {
                _path.c2.x = Mathf.Clamp(_path.c2.x, roomPath.min.x + 0.3f, roomPath.max.x - 0.3f);
                _path.c2.z = Mathf.Clamp(_path.c2.z, roomPath.min.z + 0.3f, roomPath.max.z - 0.3f);
            }
            _diagStart = _path.p0;
            _diagEnd = _path.p3;
            // T0：日志里的“老师锚”必须是**锁定的路径锚点**，不是实时低通位置，
            // 否则会误以为锚点在漂。
            _diagTeacher = _anchors.TeacherAnchor;
            _hasPath = true;
            UpdateCenterLine();
        }

        // ── 星点：稀疏中心线 + 两条侧轨 + 沿途 chevron + 终点箭头 ──
        //   2026-10-25：旧版 90 颗中心线星在 2m 路上间距 2cm，0.15m 的柔光星全部叠在一起
        //   → 真机是一团发光的"虫"。现在按路长定间距（中心线 0.18m / 侧轨 0.16m），
        //   单颗星能被看清；方向主要靠 2 个 chevron + 终点箭头 + 彗星。
        _flow = Mathf.Repeat(_flow + flowSpeed * dt, 1f);
        int n = Mathf.Clamp(starDots, 0, MaxDots - ArrowDots - ChevronTotal);
        float pathLen = Vector3.Distance(_path.p0, _path.p3);
        float halfWidth = RoadHalfWidthFor(pathLen, roadHalfWidth);
        _diagFloorY = floorY;
        _lastHalfWidth = halfWidth;
        int maxCenter = Mathf.Max(4, Mathf.RoundToInt(n * centerlineFraction));
        int centerCount = Mathf.Clamp(Mathf.RoundToInt(pathLen / 0.18f), 4, maxCenter);
        int railPerSide = Mathf.Clamp(Mathf.RoundToInt(pathLen / 0.16f), 4,
                                      Mathf.Max(4, (n - centerCount) / 2));

        // 中心线：暖金、亮——"路"的主脊（间距 ≥0.18m，颗颗可辨）
        for (int i = 0; i < centerCount; i++)
        {
            float t = Mathf.Repeat((i + 0.5f) / Mathf.Max(1, centerCount) + _flow, 1f);
            SamplePath(t, fwd, out Vector3 p, out Vector3 side);
            p += side * (_jSide[i] * 0.03f);
            p += Vector3.up * (0.03f + _jUp[i] * 0.06f);
            float fade = Mathf.Clamp01(t / 0.05f) * Mathf.Clamp01((1f - t) / 0.08f);
            float tw = 0.65f + 0.35f * Mathf.Sin(now * (2.2f + 2.4f * _phase[i]) + _phase[i]);
            float size = starDotSize * 1.15f * _sMul[i] * (0.85f + 0.15f * tw) * fade;
            PlaceDot(i, p, size, fade * (0.82f + 0.18f * tw), ColorFor(0.10f));
        }

        // 两条侧轨：淡紫、暗、固定在锥形宽度边缘——给出"路面"宽度但不糊住中心
        for (int j = 0; j < railPerSide; j++)
        {
            int i = centerCount + j;
            float t = Mathf.Repeat((j + 0.5f) / Mathf.Max(1, railPerSide) + _flow, 1f);
            SamplePath(t, fwd, out Vector3 p, out Vector3 side);
            float width = halfWidth * Mathf.Lerp(1f, taper, t);
            p += side * (-width * (0.90f + 0.10f * _jSide[i]));
            p += Vector3.up * (0.03f + _jUp[i] * 0.08f);
            float fade = Mathf.Clamp01(t / 0.05f) * Mathf.Clamp01((1f - t) / 0.08f);
            float tw = 0.62f + 0.38f * Mathf.Sin(now * (2.2f + 2.4f * _phase[i]) + _phase[i]);
            float size = starDotSize * 0.60f * _sMul[i] * (0.85f + 0.15f * tw) * fade;
            PlaceDot(i, p, size, fade * (0.35f + 0.15f * tw), ColorFor(0.80f));
        }
        for (int j = 0; j < railPerSide; j++)
        {
            int i = centerCount + railPerSide + j;
            float t = Mathf.Repeat((j + 0.5f) / Mathf.Max(1, railPerSide) + _flow, 1f);
            SamplePath(t, fwd, out Vector3 p, out Vector3 side);
            float width = halfWidth * Mathf.Lerp(1f, taper, t);
            p += side * (width * (0.90f + 0.10f * _jSide[i]));
            p += Vector3.up * (0.03f + _jUp[i] * 0.08f);
            float fade = Mathf.Clamp01(t / 0.05f) * Mathf.Clamp01((1f - t) / 0.08f);
            float tw = 0.62f + 0.38f * Mathf.Sin(now * (2.2f + 2.4f * _phase[i]) + _phase[i]);
            float size = starDotSize * 0.60f * _sMul[i] * (0.85f + 0.15f * tw) * fade;
            PlaceDot(i, p, size, fade * (0.35f + 0.15f * tw), ColorFor(0.80f));
        }

        int chevronBase = centerCount + railPerSide * 2;
        bool showChevrons = pathLen >= 1.2f;
        if (showChevrons)
        {
            // 沿途 2 个 V 形 chevron（5 颗星/个），指向老师，强化"往这边走"
            for (int c = 0; c < ChevronCount; c++)
            {
                float tc = Mathf.Clamp01(0.42f + c * 0.26f);
                SamplePath(tc, fwd, out Vector3 cp, out Vector3 cside);
                Vector3 tan = CubicBezier(_path.p0, _path.c1, _path.c2, _path.p3,
                                          Mathf.Min(1f, tc + 0.01f))
                            - CubicBezier(_path.p0, _path.c1, _path.c2, _path.p3,
                                          Mathf.Max(0f, tc - 0.01f));
                if (tan.sqrMagnitude < 1e-6f) tan = fwd;
                tan.Normalize();
                Vector3 backC = -tan;
                int baseI = chevronBase + c * ChevronDots;
                Vector3 tip = cp + Vector3.up * 0.04f;
                float tw0 = 0.7f + 0.3f * Mathf.Sin(now * 2.4f + c * 1.7f);
                PlaceDot(baseI, tip, starDotSize * 0.95f, 0.8f * tw0, ColorFor(0.05f));
                for (int k = 1; k < ChevronDots; k++)
                {
                    int wing = ((k - 1) & 1) == 0 ? -1 : 1;
                    int row = (k - 1) / 2;                 // 0..1
                    float along = 0.14f + row * 0.15f;
                    float across = 0.09f + row * 0.09f;
                    Vector3 p = tip + backC * along + cside * (wing * across);
                    float tw = 0.7f + 0.3f * Mathf.Sin(now * 2.4f + k * 1.3f);
                    PlaceDot(baseI + k, p, starDotSize * (0.85f - row * 0.05f), 0.72f * tw, ColorFor(0.08f));
                }
            }
        }

        // ── 终点贴地箭头：路尾一枚 V 形箭头，明确"往老师脚下走" ──
        //   2026-10-25：旧版是 6 颗竖排星柱（0.18~0.93m），真机看起来像"一排竖向星星"；
        //   改成贴地箭头（1 个箭尖 + 3 对翼），既指方向又不挡老师。
        {
            Vector3 tip = _path.p3;
            Vector3 back = CubicBezier(_path.p0, _path.c1, _path.c2, _path.p3, 0.90f) - tip;
            back.y = 0f; // 路尾现在是贴地的：箭头必须平铺在地面，不能顺着下坡方向翘起来
            if (back.sqrMagnitude < 1e-6f) back = -fwd;
            back.Normalize();
            Vector3 side = Vector3.Cross(Vector3.up, back);
            if (side.sqrMagnitude < 1e-6f) side = Vector3.Cross(Vector3.up, fwd);
            side.Normalize();
            int arrowBase = chevronBase + (showChevrons ? ChevronTotal : 0);
            for (int k = 0; k < ArrowDots; k++)
            {
                int i = arrowBase + k;
                Vector3 p;
                float sizeMul;
                if (k == 0)
                {
                    p = tip + Vector3.up * 0.04f;
                    sizeMul = 1.30f; // 箭尖
                }
                else
                {
                    int wing = ((k - 1) & 1) == 0 ? -1 : 1;
                    int row = (k - 1) / 2;              // 0..2
                    float along = 0.16f + row * 0.16f;  // 从箭尖向回铺
                    float across = 0.09f + row * 0.11f; // 翼展逐渐变宽
                    p = tip + back * along + side * (wing * across);
                    sizeMul = 1.05f - row * 0.08f;
                }
                float tw = 0.65f + 0.35f * Mathf.Sin(now * 2.7f + k * 1.4f);
                float size = starDotSize * sizeMul * (0.9f + 0.1f * tw);
                PlaceDot(i, p, size, 0.95f * tw, ColorFor(0.05f));
            }
        }

        // 其余的星隐藏（数量下调时）
        int used = chevronBase + (showChevrons ? ChevronTotal : 0) + ArrowDots;
        _diagDots = used;
        for (int i = used; i < MaxDots; i++)
            if (_dotR[i] != null) _dotR[i].enabled = false;

        // ── 指路彗星：沿中心线飞向老师，方向感的主视觉 ──
        if (_comet != null && _cometR != null)
        {
            _cometT = Mathf.Repeat(_cometT + cometSpeed * dt, 1f);
            Vector3 cp = CubicBezier(_path.p0, _path.c1, _path.c2, _path.p3, _cometT);
            _comet.position = cp;
            _comet.rotation = Quaternion.LookRotation(cp - eye.position, Vector3.up);
            _comet.localScale = Vector3.one * (cometSize * (0.9f + 0.1f * Mathf.Sin(now * 6f)));
            _cometR.enabled = true;
            if (_cometMat != null)
                _cometMat.SetColor("_Color", new Color(1f, 0.96f, 0.78f, 0.95f));
        }

        // ── 诊断（2s 一条）──
        _diagT += dt;
        if (_diagT >= 2f)
        {
            _diagT = 0f;
            Vector3 dirT = _diagTeacher - eyePos; dirT.y = 0f;
            float ang = dirT.sqrMagnitude > 1e-4f ? Vector3.SignedAngle(fwd, dirT.normalized, Vector3.up) : 0f;
            Debug.Log($"[Stardust] 起点={_diagStart.ToString("F2")} 终点={_diagEnd.ToString("F2")} " +
                      $"路长={Vector3.Distance(_diagStart, _diagEnd):F2}m 半宽={halfWidth:F2}m " +
                      $"老师锚={_diagTeacher.ToString("F2")} 眼={eyePos.ToString("F2")} " +
                      $"老师方位角={ang:F0}° 地板Y={floorY:F2} 星点={n}+{ArrowDots}");
        }
    }

    void SamplePath(float t, Vector3 fwd, out Vector3 p, out Vector3 side)
    {
        p = CubicBezier(_path.p0, _path.c1, _path.c2, _path.p3, t);
        Vector3 tangent = CubicBezier(_path.p0, _path.c1, _path.c2, _path.p3,
                                      Mathf.Min(1f, t + 0.012f))
                        - CubicBezier(_path.p0, _path.c1, _path.c2, _path.p3,
                                      Mathf.Max(0f, t - 0.012f));
        side = Vector3.Cross(tangent, Vector3.up);
        if (side.sqrMagnitude < 1e-6f) side = Vector3.Cross(fwd, Vector3.up);
        side.Normalize();
    }

    /// <summary>双锚点星路几何（纯函数）：start=视野锚点（画面外前下方），end=老师锚点。
    /// 路径从画面外进入、绕过孩子身体，升到老师锚点；控制点抬高到桌面以上，避开低矮障碍。</summary>
    public static void ComputeAnchoredPath(Vector3 start, Vector3 end, Vector3 eyePos, Vector3 eyeForward,
                                           float floorY, float detour,
                                           out Vector3 p0, out Vector3 c1, out Vector3 c2, out Vector3 p3)
        => ComputeAnchoredPath(start, end, eyePos, eyeForward, floorY, detour, 0.05f,
                               out p0, out c1, out c2, out p3);

    /// <summary>2026-11-18：路尾高度可调（用户实测贴地 0.05m 在很多角度看不见）。
    /// endHeight 是路尾离地高度；x/z 仍取老师锚点 + 侧偏。</summary>
    public static void ComputeAnchoredPath(Vector3 start, Vector3 end, Vector3 eyePos, Vector3 eyeForward,
                                           float floorY, float detour, float endHeight,
                                           out Vector3 p0, out Vector3 c1, out Vector3 c2, out Vector3 p3)
    {
        p0 = start;
        p3 = new Vector3(end.x, floorY + Mathf.Max(0.05f, endHeight), end.z);
        Vector3 fwd = new Vector3(eyeForward.x, 0f, eyeForward.z);
        if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
        fwd.Normalize();

        Vector3 startFlat = new Vector3(start.x, floorY, start.z);
        Vector3 endFlat = new Vector3(end.x, floorY, end.z);
        Vector3 dir = endFlat - startFlat;
        dir.y = 0f;
        if (dir.sqrMagnitude < 1e-4f) dir = fwd;
        dir.Normalize();
        Vector3 side = Vector3.Cross(Vector3.up, dir);
        if (side.sqrMagnitude < 1e-6f) side = Vector3.Cross(Vector3.up, fwd);
        side.Normalize();

        Vector3 child = new Vector3(eyePos.x, floorY, eyePos.z);
        float sideDot = Vector3.Dot(side, child - startFlat);
        float sign = sideDot > 0f ? -1f : 1f;
        bool behind = Vector3.Dot(fwd, new Vector3(end.x - eyePos.x, 0f, end.z - eyePos.z)) < 0.15f;
        float lateral = behind ? Mathf.Max(2.2f, detour * 2.4f) : Mathf.Max(detour, 0.9f);

        Vector3 mid = (startFlat + endFlat) * 0.5f;
        c2 = mid + side * (sign * lateral);
        // 中段抬到桌面以上（0.60m），避免从课桌/椅子中穿过；路尾再落到老师脚下
        c2.y = floorY + 0.60f;
        // 第一控制点把"画面外"的路头拉进画面
        c1 = Vector3.Lerp(p0, c2, 0.30f) + Vector3.up * 0.25f;
        c1.y = Mathf.Max(c1.y, floorY + 0.15f);
    }

    void UpdateCenterLine()
    {
        if (_centerLine == null) return;
        const int N = 33;
        _centerLine.positionCount = N;
        for (int i = 0; i < N; i++)
        {
            float t = i / (N - 1f);
            _centerLine.SetPosition(i,
                CubicBezier(_path.p0, _path.c1, _path.c2, _path.p3, t) + Vector3.up * 0.02f);
        }
        _centerLine.enabled = IsOn;
    }

    void UpdateDebugMarkers(float floorY, bool teacherVisible)
    {
        if (_teacherMarker == null) return;
        bool show = showAnchors && _anchors != null && teacherVisible;

        bool t = show && _anchors.HasTeacherAnchor;
        _teacherMarker.gameObject.SetActive(t);
        if (_teacherStem != null) _teacherStem.enabled = t;
        if (_teacherRing != null) _teacherRing.enabled = t;
        if (t)
        {
            Vector3 p = _anchors.TeacherAnchor;
            _teacherMarker.position = p;
            _teacherMarker.localScale = Vector3.one * anchorMarkerSize;
            _teacherStem.positionCount = 2;
            _teacherStem.SetPosition(0, p);
            _teacherStem.SetPosition(1, new Vector3(p.x, floorY + 0.02f, p.z));
            const int RN = 25;
            _teacherRing.positionCount = RN;
            for (int i = 0; i < RN; i++)
            {
                float a = i / (RN - 1f) * Mathf.PI * 2f;
                _teacherRing.SetPosition(i,
                    new Vector3(p.x + Mathf.Cos(a) * 0.25f, floorY + 0.03f, p.z + Mathf.Sin(a) * 0.25f));
            }
        }

        bool v = show && _anchors.HasViewAnchor;
        _viewMarker.gameObject.SetActive(v);
        if (v)
        {
            _viewMarker.position = _anchors.ViewAnchor;
            _viewMarker.localScale = Vector3.one * anchorMarkerSize;
        }
    }

    void PlaceDot(int i, Vector3 pos, float size, float alpha, Color col)
    {
        if (_dots[i] == null) return;
        bool on = alpha > 0.02f && size > 0.002f;
        _dotR[i].enabled = on;
        if (!on) return;
        _dots[i].position = pos;
        _dots[i].rotation = Quaternion.LookRotation(pos - eye.position, Vector3.up);
        _dots[i].localScale = Vector3.one * size;
        col.a = alpha;
        _mpb.SetColor("_Color", col);
        _dotR[i].SetPropertyBlock(_mpb);
    }

    static Color ColorFor(float pick)
    {
        if (pick < 0.62f) return new Color(1f, 0.93f, 0.68f, 1f);   // 暖金
        if (pick < 0.88f) return new Color(0.86f, 0.80f, 1f, 1f);   // 淡紫
        return new Color(0.92f, 0.98f, 1f, 1f);                      // 冷白
    }

    static float Hash01(float x)
    {
        float v = Mathf.Sin(x) * 43758.5453f;
        return v - Mathf.Floor(v);
    }

    /// <summary>诊断字符串（纯函数，供单测/会话日志）：起点/终点/老师锚/路长/宽度/方位角。</summary>
    public static string FormatSnapshot(Vector3 start, Vector3 end, Vector3 teacher, Vector3 eye,
                                        Vector3 forward, float floorY, float width, int dots)
    {
        Vector3 fwd = new Vector3(forward.x, 0f, forward.z);
        if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
        fwd.Normalize();
        Vector3 dirT = new Vector3(teacher.x - eye.x, 0f, teacher.z - eye.z);
        float ang = dirT.sqrMagnitude > 1e-4f
            ? Vector3.SignedAngle(fwd, dirT.normalized, Vector3.up)
            : 0f;
        return $"start={start.ToString("F2")} end={end.ToString("F2")} " +
               $"teacher={teacher.ToString("F2")} eye={eye.ToString("F2")} " +
               $"floor={floorY:F2} len={Vector3.Distance(start, end):F2} " +
               $"width={width:F2} dots={dots} angle={ang:F0}";
    }

    /// <summary>当前路径的会话诊断快照（路径开启时由驱动层每 2s 写入 events.jsonl）。</summary>
    public string DebugSnapshot()
    {
        Vector3 e = eye != null ? eye.position : Vector3.zero;
        Vector3 f = eye != null ? eye.forward : Vector3.forward;
        return FormatSnapshot(_diagStart, _diagEnd, _diagTeacher, e, f,
                              _diagFloorY, _lastHalfWidth, _diagDots);
    }

    /// <summary>路面半宽随路长收缩（纯函数，供单测）：短路若保持 1m 宽会变成"一团星星"。</summary>
    public static float RoadHalfWidthFor(float pathLength, float baseHalfWidth)
    {
        // 2026-10-24：按路长 10% 给半宽（2m→0.20m、5m→0.28m 封顶），下限 0.10m；
        // 不再固定 0.5m，避免短路上宽过长度变成"一团星星"。
        return Mathf.Clamp(pathLength * 0.10f, 0.10f, baseHalfWidth);
    }

    /// <summary>三次贝塞尔采样（公开为静态，供 EditMode 几何测试/预览使用）。</summary>
    public static Vector3 CubicBezier(Vector3 p0, Vector3 c1, Vector3 c2, Vector3 p3, float t)
    {
        float u = 1f - t;
        return u * u * u * p0
             + 3f * u * u * t * c1
             + 3f * u * t * t * c2
             + t * t * t * p3;
    }

    /// <summary>计算路径控制点（纯函数，供单测）：
    /// 起点=眼前下方（视野内）→ 落到地面 → 绕过孩子 → 老师脚下身侧。
    /// 老师位于身后时，第二控制点绕到孩子侧方（保证路面不穿过身体）。</summary>
    public static void ComputePath(Vector3 eyePos, Vector3 eyeForward, float floorY, Vector3 teacherPos,
                                   float startForward, float startDrop, float detour, float endSideOffset,
                                   out Vector3 p0, out Vector3 c1, out Vector3 c2, out Vector3 p3)
    {
        Vector3 fwd = new Vector3(eyeForward.x, 0f, eyeForward.z);
        if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
        fwd.Normalize();

        // 路头：视野前下方（在垂直 FOV 内；从"眼前一段距离"开始）
        p0 = eyePos + fwd * startForward + Vector3.down * startDrop;

        // 地板落点：路头前方 0.6m 落到地面
        Vector3 land = new Vector3(p0.x, floorY + 0.05f, p0.z) + fwd * 0.6f;

        // 路尾：老师脚下 + 身侧偏移（"老师身旁"）
        Vector3 dirT = new Vector3(teacherPos.x - eyePos.x, 0f, teacherPos.z - eyePos.z);
        if (dirT.sqrMagnitude < 1e-4f) dirT = fwd;
        dirT.Normalize();
        Vector3 right = Vector3.Cross(Vector3.up, fwd).normalized; // 孩子右手方向
        Vector3 endSide = Vector3.Cross(Vector3.up, dirT).normalized;
        // 站在老师哪一侧：取离孩子更远的一侧，避免路尾贴着孩子
        if (Vector3.Dot(endSide, eyePos - teacherPos) > 0f) endSide = -endSide;
        // 路尾：老师脚下地面投影（v9.2 试过升到老师身前，预览里变成"竖排星星"，已回退）。
        //   用贴地箭头 + 彗星表达方向；路本身仍铺在地面上。
        p3 = new Vector3(teacherPos.x, floorY + 0.05f, teacherPos.z) + endSide * endSideOffset;

        bool behind = Vector3.Dot(fwd, dirT) < 0.15f; // 老师侧后方/正后方
        if (behind)
        {
            // 绕行点：孩子侧方（取老师所在一侧），保证贝塞尔中段远离孩子身体
            float sideSign = Vector3.Dot(right, dirT);
            if (Mathf.Abs(sideSign) < 0.05f) sideSign = 1f;
            sideSign = Mathf.Sign(sideSign);
            float lateral = Mathf.Max(2.2f, detour * 2.4f);
            c2 = new Vector3(eyePos.x, floorY + 0.30f, eyePos.z)
               + right * (sideSign * lateral) + fwd * 0.35f;
        }
        else
        {
            // 老师在身前：地面段正常绕开孩子（不会穿过身体）
            Vector3 childFloor = new Vector3(eyePos.x, floorY, eyePos.z);
            Vector3 ctrl = MeteorLogic.PathControl(childFloor, land, p3, detour);
            c2 = new Vector3(ctrl.x, floorY + 0.30f, ctrl.z);
        }

        // 第一控制点：把路头快速压向地面（形成"从眼前垂下 → 贴地"的 3D 过渡）
        c1 = Vector3.Lerp(p0, land, 0.55f) + Vector3.down * 0.45f;
        c1.y = Mathf.Max(floorY + 0.12f, c1.y);
    }
}

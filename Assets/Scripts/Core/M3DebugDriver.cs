// Assets/Scripts/Core/M3DebugDriver.cs
// M3 总装胶水层（2026-11-24 全流程重构）：
//   帧源 → 检测/人脸 → 跟踪 → 注视 → AttentionFlowLogic → 精灵/星路/语音/星星/日志。
//
// 唯一权威流程（AttentionFlowLogic）：
//   FirstSearch：精灵先在视野中央出现，再缓慢游走 + 语音吸引，直到稳定确认老师。
//   Watching   ：第一次找到老师时在老师位置放下锚点（**不生成星路**）；看着老师累计星星；
//                集满 → Rewarding → 奖励后自动开始下一轮。
//   GuidingBack：看过老师后又移开视线 → 精灵飘进视野、指老师、语音“找老师”，
//                星路用“老师锚点 + 当前视野锚点”生成；重新看老师立即收路。
//
// 键盘模拟（本工程为新 Input System）：K=模拟注视切换  L=模拟讲话切换  R=重置会话
using System.Collections;
using System.Collections.Generic;
using Meta.XR;
using UnityEngine;
using UnityEngine.InputSystem;

public class M3DebugDriver : MonoBehaviour
{
    [Header("引用")]
    public FaceDetector detector;
    public FaceBoxRunner faceRunner;   // BlazeFace 人脸检测（确认“老师身上的脸”；可为空）
    public ExperienceSettings settings;
    public IPDebugView ipView;          // 电脑端（OnGUI）
    public IPWorldView ipWorldView;     // 真机（世界空间）——两者二选一
    public MeteorSystem meteorView;     // 星环/流星/流星雨
    public StardustPath stardustPath;   // 星路（真机视图；可为空）
    public GuideArrow guideArrow;       // 已停用，保留备用
    public AudioCuePlayer audioPlayer;
    public VoiceCuePlayer voiceCue;
    public MicVadService vadService;
    [Tooltip("中心眼/头位变换（真机=OVRCameraRig.centerEyeAnchor）")]
    public Transform eye;

    [Header("模拟模式（电脑端）")]
    [Tooltip("true=键盘 K 模拟注视；真机阶段换真实头姿判定")]
    public bool simulateGazeWithKeyboard = true;

    [Header("调试开关")]
    [Tooltip("上线版关闭：禁用键盘模拟、A/X 切档等测试输入")]
    public bool enableDebugControls = true;

    [Tooltip("true=麦克风 VAD 判定讲话（只用于语音门控/倾听态，不驱动引导）")]
    public bool useMicVad = true;

    // ── 纯逻辑模块 ──
    AttentionFlowLogic _flow;
    TargetTrackerLogic _tracker;
    GazeLogic _gaze;
    SessionLogger _logger;
    ConversationGateLogic _conversationGate;
    FirstSearchLogic _firstSearch;

    // 对话门控 / 语音
    bool _prevExternalSpeech;
    VoiceCue _pendingVoice;
    VoicePriority _pendingPriority;
    float _pendingAt;
    bool _hasPendingVoice;
    bool _prevGateListening;
    bool _speaking = true;         // 有效外界讲话（门控后）
    bool _prevSpeaking = true;     // 原始讲话输入（键盘边沿用）
    bool _micFallbackLogged;
    bool _findVoicePending;        // 指路“找老师”语音：被外界讲话挡住时，等静默后补播一次

    // 模拟输入
    bool _simLooking;

    // 跟踪 / 人脸
    bool _prevTargetValid;
    bool _targetLostEdge, _targetReacquiredEdge;
    float _faceDataT = -99f;       // 最近一批新人脸检测
    float _faceConfirmedT = -99f;  // 最近确认“属于人体的脸”
    int _faceSeenVer = -1;
    bool _faceBatchChanged;        // 本帧是否有新的人脸检测批次
    Vector2 _teacherFaceCenter01;  // 主脸中心（归一化，左上原点）
    float _faceCenteredDist = 99f;
    float _faceDiagT;
    List<DetectionResult> _simFaces;

    // 注视
    bool _lookingNow;
    bool _gazeAtAnchor;            // 视角滞回状态

    // 首寻
    bool _searchActive;
    float _searchDiagT;
    Vector3 _searchTarget;
    static readonly VoiceCue[] SearchVoiceOrder =
    {
        VoiceCue.SearchStart, VoiceCue.SearchLook, VoiceCue.SearchHere,
        VoiceCue.SearchAgain, VoiceCue.SearchWhere
    };

    // 精灵动画状态（顺序必须与 IPWorldView.IPAnimState 一一对应）
    enum IpAnim { Hidden, Appearing, GuidingMove, Companion, FeedbackPulse, Peek, Search, Attentive, Listening }
    IpAnim _ipAnim = IpAnim.Hidden;
    float _ipAnimT;
    Vector2 _ipPos = new Vector2(0.5f, 0.5f);
    float _ipAlpha, _ipScale = 1f;
    float _ipAlphaSmooth, _ipScaleSmooth, _ipScaleVel;
    bool _ipSmoothInit;
    Vector2 _smoothTarget = new Vector2(0.5f, 0.5f);
    Vector2 _smoothTargetVel;
    Vector2 _heldTargetPos = new Vector2(0.5f, 0.5f);
    bool _hasHeldTarget;
    float _heldTargetTime;

    // 奖励
    bool _celebrating;

    // 时钟 / 日志 / 诊断
    float _now;
    float _sampleTimer;
    float _fpsSmooth = 60f;
    float _flowDiagT;
    bool _prevPathShow;
    bool _teacherDetectedThisFrame;
    static readonly List<DetectionResult> EmptyDetections = new List<DetectionResult>();

    // ── 对外只读状态（数据面板/测试机）──
    public float SmoothFps => _fpsSmooth;
    public AttentionState State => _flow != null ? _flow.State : AttentionState.FirstSearch;
    public float GazeDwell => _gaze != null ? _gaze.Dwell : 0f;
    public float GazeSmooth => _gaze != null ? _gaze.Dwell : 0f; // 兼容旧面板命名
    public string LevelName => settings != null ? settings.CurrentLevelName : "--";
    public int CurrentLevel => settings != null ? settings.interventionLevel : 1;

    public IM3SimInputs sim;
    public bool simMode => sim != null;
    public int IpAnimProbe => (int)_ipAnim;
    public float IpAlphaProbe => _ipAlphaSmooth;
    public float NowProbe => _now;
    public int StarProbe => _flow != null ? _flow.LitStars : 0;
    public int FlowStateProbe => (int)State;
    public bool PathVisibleProbe => stardustPath != null ? stardustPath.IsOn : (_flow != null && _flow.PathWanted);
    public bool TeacherAnchorProbe => _flow != null && _flow.HasTeacherAnchor;
    public bool LookingProbe => _lookingNow;
    public bool SpeakingProbe => _speaking;
    public bool FaceCelebrateProbe => _celebrating;
    public bool SearchActiveProbe => _searchActive;
    public Vector2 IpPosProbe => _ipPos;
    public event System.Action<string, string> OnSignal;
    void Signal(string kind, string detail) => OnSignal?.Invoke(kind, detail);

    void Start()
    {
        InitCore();
        if (Application.isPlaying) StartCoroutine(LogBoundaryState());
    }

    IEnumerator LogBoundaryState()
    {
        yield return new WaitForSeconds(2f);
        var m = OVRManager.instance;
        if (m != null)
            Debug.Log($"[XR] boundarySuppressed(actual)={m.isBoundaryVisibilitySuppressed} " +
                      $"desired={m.shouldBoundaryVisibilityBeSuppressed} origin={m.trackingOriginType}");
    }

    /// <summary>核心初始化（编辑器测试机需显式调用）。</summary>
    public void InitCore()
    {
        if (_flow != null) return;
        if (settings != null) settings.ApplyLevel(settings.interventionLevel);

        _flow = new AttentionFlowLogic(
            settings != null ? settings.teacherFoundConfirmSeconds : 0.5f,
            settings != null ? settings.teacherAnchorStabilityRadius : 0.35f,
            settings != null ? settings.lookAwayGraceSeconds : 0.6f,
            settings != null ? settings.gazeReturnConfirmSeconds : 0.3f,
            settings != null ? settings.meteorStarSeconds : 2.5f,
            Mathf.Clamp(settings != null ? settings.meteorStarsPerRing : 8, 1, 8),
            settings != null ? settings.meteorShowerDuration : 6f,
            settings != null ? settings.rewardLookAwayGraceSeconds : 0.6f,
            settings != null ? settings.teacherAnchorSmoothSeconds : 0.5f);
        _tracker = new TargetTrackerLogic();
        _gaze = new GazeLogic(settings != null ? settings.gazeStartSeconds : 0.8f,
                              settings != null ? settings.gazeSustainSeconds : 2f);
        _conversationGate = settings != null
            ? new ConversationGateLogic(settings.conversationResumeSeconds, settings.selfAudioTailSeconds)
            : new ConversationGateLogic();
        _prevExternalSpeech = false;
        _prevSpeaking = true;

        CreateLogger();
        _logger.LogEvent("SessionStart");
        if (audioPlayer != null) audioPlayer.SetVolume(settings != null ? settings.cueVolumeScale : 0.6f);
        if (voiceCue != null) voiceCue.SetVolume(settings != null ? settings.cueVolumeScale : 0.9f);
        BindVoiceFollow();
        // 首寻→老师、指路→老师 的转场要“飞过去”而不是瞬移（6m 内都平滑追赶）
        if (ipWorldView != null) ipWorldView.snapDistance = Mathf.Max(ipWorldView.snapDistance, 6f);
        Debug.Log($"[M3] 会话数据目录：{_logger.SessionDir}");
        Debug.Log($"[M3] 干预档位：{LevelName} | 首寻 {(_flow != null ? "开" : "关")} | " +
                  $"星 {settings?.meteorStarsPerRing ?? 8}×{settings?.meteorStarSeconds ?? 2.5f}s");
        if (OVRManager.instance != null)
            Debug.Log($"[XR] TrackingOrigin={OVRManager.instance.trackingOriginType} " +
                      $"boundarySuppressed(desired)={OVRManager.instance.shouldBoundaryVisibilityBeSuppressed}");
        string speechMode = (useMicVad && vadService != null)
            ? "麦克风 VAD（只做语音门控/倾听态）"
            : "键盘 L 模拟讲话";
        Debug.Log($"[M3] 讲话输入：{speechMode}");
        Debug.Log("[M3] K=模拟注视 | L=模拟讲话(仅无麦时) | 1/2/3=切档 | R=重置 | 真机手柄 A 切档");
    }

    void BindVoiceFollow()
    {
        if (voiceCue != null && voiceCue.followTarget == null && ipWorldView != null)
            voiceCue.followTarget = ipWorldView.VisualTransform;
    }

    void CreateLogger()
        => _logger = new SessionLogger(Application.persistentDataPath, "", "",
            settings != null ? settings.interventionLevel : 1,
            settings != null ? settings.SnapshotJson() : null);

    void SwitchLevel(int level)
    {
        if (settings == null) return;
        settings.ApplyLevel(level);
        if (_gaze != null) _gaze.StartSeconds = settings.gazeStartSeconds;
        if (audioPlayer != null) audioPlayer.SetVolume(settings.cueVolumeScale);
        if (voiceCue != null) voiceCue.SetVolume(settings.cueVolumeScale);
        _logger?.LogEvent("LevelChange", settings.CurrentLevelName);
        Debug.Log($"[M3] 干预档位 → {settings.CurrentLevelName}");
    }

    void Update() => Tick(Time.deltaTime);

    /// <summary>单帧推进（测试机可用任意 dt 驱动）。</summary>
    public void Tick(float dtInput)
    {
        float dt = dtInput > 0f ? dtInput : 1f / 60f;
        _now += dt;
        _fpsSmooth = Mathf.Lerp(_fpsSmooth, 1f / dt, 0.05f);
        BindVoiceFollow();

        // ── 输入：模拟 → 键盘/VAD ──
        bool vadActive = false, micUnavailable = false;
        bool simLooking = false, simSpeaking = false, simSpeechEnded = false, simReset = false;
        List<DetectionResult> simDetections = null;
        if (simMode)
        {
            sim.Tick(dt, out simDetections, out _simFaces, out simLooking, out simSpeaking,
                     out simSpeechEnded, out simReset);
            if (simReset) ResetSession();
        }
        else
        {
            vadActive = useMicVad && vadService != null && vadService.MicRunning;
            micUnavailable = useMicVad && vadService != null && !vadService.MicRunning
                             && !simulateGazeWithKeyboard;
            if (enableDebugControls)
            {
                var kb = Keyboard.current;
                if (kb != null)
                {
                    if (kb.kKey.wasPressedThisFrame) _simLooking = !_simLooking;
                    if (kb.lKey.wasPressedThisFrame && !vadActive) _speaking = !_speaking;
                    if (kb.digit1Key.wasPressedThisFrame) SwitchLevel(0);
                    if (kb.digit2Key.wasPressedThisFrame) SwitchLevel(1);
                    if (kb.digit3Key.wasPressedThisFrame) SwitchLevel(2);
                    if (kb.rKey.wasPressedThisFrame) ResetSession();
                }
                if (!simulateGazeWithKeyboard && OVRInput.GetDown(OVRInput.Button.One))
                    SwitchLevel((CurrentLevel + 1) % 3);
            }
        }

        bool rawSpeaking, rawSpeechEnded, bypassGate;
        if (simMode)
        {
            rawSpeaking = simSpeaking;
            rawSpeechEnded = simSpeechEnded;
            bypassGate = false;
        }
        else if (vadActive)
        {
            rawSpeaking = vadService.Logic.Speaking;
            rawSpeechEnded = vadService.Logic.SpeechEnded;
            bypassGate = false;
        }
        else
        {
            rawSpeaking = micUnavailable ? false : _speaking;
            rawSpeechEnded = micUnavailable ? false : (_prevSpeaking && !_speaking);
            bypassGate = true;
            if (micUnavailable && !_micFallbackLogged)
            {
                _micFallbackLogged = true;
                _logger?.LogEvent("MicFallback", "no-mic: voice gate bypassed");
                Debug.Log("[M3] 麦克风不可用 → 关闭语音门控（不影响注意力流程）");
            }
        }

        // ── 对话门控：外界讲话时精灵语音延后；系统自己的声音不喂给 VAD ──
        bool selfAudio = (voiceCue != null && voiceCue.IsPlaying) ||
                         (audioPlayer != null && audioPlayer.IsPlaying);
        bool realSpeechEnd = false;
        if (bypassGate)
        {
            _conversationGate.Reset();
            _speaking = rawSpeaking;
            realSpeechEnd = rawSpeechEnded;
            _hasPendingVoice = false;
            _prevGateListening = false;
            voiceCue?.SetGateAllowed(true);
            _prevExternalSpeech = _speaking;
        }
        else
        {
            _conversationGate.Tick(dt, rawSpeaking, selfAudio);
            _speaking = _conversationGate.ExternalSpeech;
            realSpeechEnd = _prevExternalSpeech && !_speaking;
            _prevExternalSpeech = _speaking;
            if (_conversationGate.Listening != _prevGateListening)
            {
                _prevGateListening = _conversationGate.Listening;
                _logger?.LogEvent("VoiceGate",
                    $"ext={(_conversationGate.ExternalSpeech ? 1 : 0)} " +
                    $"listen={(_prevGateListening ? 1 : 0)} self={(_conversationGate.SelfAudioSuppressing ? 1 : 0)}");
            }
            voiceCue?.SetGateAllowed(_conversationGate.VoiceAllowed);
            if (_conversationGate.VoiceAllowed && _hasPendingVoice)
            {
                if (_now - _pendingAt <= 2f)
                {
                    var cue = _pendingVoice;
                    var pr = _pendingPriority;
                    _hasPendingVoice = false;
                    PlayVoice(cue, pr);
                }
                else _hasPendingVoice = false;
            }
        }
        _prevSpeaking = rawSpeaking;
        audioPlayer?.SetDuck(!bypassGate && _speaking ? 0.2f : 1f);
        if (realSpeechEnd) _logger?.LogEvent("SpeechEnd");

        // ── 跟踪 + 人脸 → “老师真的在” ──
        var detections = simMode ? (simDetections ?? EmptyDetections)
                                 : (detector != null ? detector.Latest : EmptyDetections);
        var snapshot = _tracker.Tick(detections, dt);

        // 人脸候选先更新，本帧就能用“最近确认的人体脸”判断老师是否真的在。
        UpdateFaceCandidate(snapshot, detections, dt);

        float faceConfirmWin = settings != null ? settings.teacherFaceConfirmSeconds : 1.5f;
        float gazeFreshWin = settings != null ? settings.gazeFreshnessSeconds : 0.4f;
        // 有人脸检测通道时必须确认“属于人体的脸”（防海报/纹理假脸）；
        // 没有挂人脸检测的极简调试场景则只用人体检测。
        bool faceRequired = simMode || faceRunner != null;
        bool faceFreshForTarget = !faceRequired || _now - _faceConfirmedT <= faceConfirmWin;
        bool targetFresh = snapshot.valid && snapshot.lastSeenSeconds <= gazeFreshWin;
        bool teacherDetected = targetFresh && faceFreshForTarget;
        _teacherDetectedThisFrame = teacherDetected;

        Vector3 teacherPos = default;
        bool hasTeacherPos;
        if (ipWorldView != null)
            hasTeacherPos = ipWorldView.TryGetTeacherWorldPos(out teacherPos);
        else
        {
            // 模拟/电脑端：没有世界反投影，给一个稳定的方位占位（注视由模拟输入决定）
            Vector3 basePos = eye != null ? eye.position : transform.position + Vector3.up * 1.2f;
            Vector3 fwd = eye != null ? eye.forward : transform.forward;
            teacherPos = basePos + fwd * 2f;
            hasTeacherPos = true;
        }
        bool teacherSeenForFlow = teacherDetected && hasTeacherPos;

        _targetLostEdge = _prevTargetValid && !snapshot.valid;
        _targetReacquiredEdge = !_prevTargetValid && snapshot.valid;
        if (_targetLostEdge) { _logger.LogEvent("TargetLost"); Signal("T", "Lost"); }
        if (_targetReacquiredEdge) { _logger.LogEvent("TargetAcquired"); Signal("T", "Acquired"); }
        _prevTargetValid = snapshot.valid;

        // ── 注视判定 ──
        bool looking;
        if (simMode || simulateGazeWithKeyboard)
        {
            looking = (simMode ? simLooking : _simLooking) && teacherSeenForFlow;
            _gazeAtAnchor = looking;
        }
        else if (_flow.HasTeacherAnchor && eye != null && eye.position.y > 0.5f)
        {
            // 真机：头显前向与老师世界锚点的夹角（带滞回）。比“检测框在画面中心”稳定，
            // 也避免老师稍偏一点就被判成“没看”。
            Vector3 to = _flow.TeacherAnchor - eye.position;
            if (to.sqrMagnitude > 1e-4f)
            {
                float ang = Vector3.Angle(eye.forward, to);
                float enter = settings != null ? settings.gazeAngleEnterDegrees : 16f;
                float exit = settings != null ? Mathf.Max(enter + 1f, settings.gazeAngleExitDegrees) : 22f;
                if (!_gazeAtAnchor && ang <= enter) _gazeAtAnchor = true;
                else if (_gazeAtAnchor && ang > exit) _gazeAtAnchor = false;
                looking = _gazeAtAnchor && teacherSeenForFlow;
            }
            else looking = false;
        }
        else
        {
            // 锚点建立前（首寻确认阶段）：用检测头点是否在视野中心做代理
            float radius = settings != null ? settings.gazeViewportRadius : 0.28f;
            looking = teacherSeenForFlow && GazeLogic.IsLookingAt(snapshot.headPoint01, radius);
        }
        _lookingNow = looking;
        _gaze.Tick(looking, dt);

        // ── 首寻推进（在流程之前，先用当前视野位置启动游走）──
        if (State == AttentionState.FirstSearch) UpdateFirstSearch(dt);

        // ── 唯一权威流程 ──
        AttentionState prevFlowState = _flow.State;
        AttentionEvent ev = _flow.Tick(dt, teacherSeenForFlow, teacherPos, looking);
        HandleFlowEvent(ev);
        if (_flow.State != prevFlowState)
        {
            _logger?.LogEvent("StateChange", $"{prevFlowState}->{_flow.State}");
            Signal("State", $"{prevFlowState}->{_flow.State}");
        }
        if (State != AttentionState.FirstSearch && _searchActive) StopFirstSearch();

        // 找老师语音：进入指路时触发；若正被外界讲话挡住，等静默后再补播一次。
        if (_flow.State == AttentionState.GuidingBack) TryPlayFindVoice();
        else _findVoicePending = false;

        // ── 星路：只在 GuidingBack 打开；老师锚点由流程维护 ──
        if (_flow.HasTeacherAnchor) stardustPath?.SetTeacherAnchor(_flow.TeacherAnchor);
        bool pathOn = settings == null || settings.stardustPathEnabled;
        bool wantPath = _flow.PathWanted && pathOn;
        stardustPath?.SetOn(wantPath);
        stardustPath?.Tick(_now, dt);
        if (wantPath != _prevPathShow)
        {
            _prevPathShow = wantPath;
            _logger?.LogEvent(wantPath ? "StarPathShow" : "StarPathHide");
            Signal("Path", wantPath ? "Show" : "Hide");
            Debug.Log($"[Path] {(wantPath ? "显示" : "隐藏")} " +
                      $"anchor={(stardustPath != null ? stardustPath.TeacherAnchor.ToString("F2") : "--")}");
        }

        // ── 星环/流星视图 ──
        if (meteorView != null)
        {
            Vector3 ringPos = _flow.HasTeacherAnchor ? _flow.TeacherAnchor
                             : teacherSeenForFlow ? teacherPos
                             : (eye != null ? eye.position + eye.forward * 2f : transform.position);
            meteorView.SetRingTarget(ringPos);
            meteorView.Tick(_now, dt);
        }

        // ── 精灵视图（世界/视口统一在这里喂）──
        UpdateSpriteVisuals(dt, snapshot, teacherSeenForFlow);

        // ── 日志/采样 ──
        _sampleTimer += dt;
        float every = settings != null ? settings.logSampleEverySeconds : 2f;
        if (_sampleTimer >= every)
        {
            _sampleTimer = 0f;
            _logger.LogSample(looking, _speaking, snapshot.valid, _fpsSmooth,
                detector != null ? detector.LastInferenceMs : 0f);
        }
        LogFlowDiagnostics(dt, snapshot);
    }

    // ─────────────────────────────────────────────────────────────
    // 流程事件 → 音效 / 星环 / 庆祝 / 日志
    // ─────────────────────────────────────────────────────────────
    void HandleFlowEvent(AttentionEvent ev)
    {
        switch (ev)
        {
            case AttentionEvent.TeacherFound:
                // 找到老师的那一刻孩子正看着老师；锚点夹角滞回从"在看"开始，
                // 避免锚点估计的几度误差导致刚找到就误判移开、立刻铺星路。
                _gazeAtAnchor = true;
                StopFirstSearch();
                stardustPath?.SetTeacherAnchor(_flow.TeacherAnchor);
                ipWorldView?.FxPulse("Lock");
                audioPlayer?.PlayCue(AudioCue.Feedback);
                ClearVoiceQueue();
                PlayVoice(VoiceCue.SearchFound, VoicePriority.Critical);
                Signal("Teacher", "Found");
                Signal("Fx", "Lock");
                _logger.LogEvent("TeacherFound", _flow.TeacherAnchor.ToString("F2"));
                Debug.Log($"[Flow] 第一次找到老师 → 放下锚点 {_flow.TeacherAnchor.ToString("F2")}（不生成星路）");
                break;

            case AttentionEvent.GuideBackStarted:
                if (_celebrating)
                {
                    EndCelebration(0.4f);
                    meteorView?.ClearStars(); // 奖励被打断：星环清干净，下一轮从 0 开始
                }
                ipWorldView?.FxPulse("ReGuide");
                audioPlayer?.PlayCue(AudioCue.GuideAppear);
                _findVoicePending = true;
                TryPlayFindVoice();
                Signal("Guide", "Back");
                Signal("Voice", "Find");
                _logger.LogEvent("GuideBack", $"round{_flow.CompletedRounds + 1} stars{_flow.LitStars}");
                Debug.Log("[Flow] 孩子移开视线 → 精灵飘进视野指路 + 星路");
                break;

            case AttentionEvent.GazeReturned:
                _gazeAtAnchor = true;
                ipWorldView?.FxPulse("Lock");
                Signal("Guide", "Return");
                _logger.LogEvent("GuideReturn");
                Debug.Log("[Flow] 孩子重新看老师 → 立即关闭星路");
                break;

            case AttentionEvent.StarDropped:
            {
                int idx = Mathf.Clamp(_flow.LitStars - 1, 0, (_flow.StarsPerRing - 1));
                meteorView?.LightStar(idx);
                audioPlayer?.PlayStarChime(idx);
                Signal("Star", "Drop");
                _logger.LogEvent("StarDrop", $"{_flow.LitStars}/{_flow.StarsPerRing}");
                Debug.Log($"[Flow] ⭐ 第 {_flow.LitStars} 颗星（{idx + 1}/{_flow.StarsPerRing}）");
                break;
            }

            case AttentionEvent.RewardStarted:
            {
                _celebrating = true;
                float dur = settings != null ? settings.meteorShowerDuration : 6f;
                // 第 8 颗先点亮再爆闪，让“满环”完整可见
                meteorView?.LightStar(Mathf.Clamp(_flow.StarsPerRing - 1, 0, 7));
                meteorView?.PlayShower();
                stardustPath?.SetOn(false);
                audioPlayer?.PlayMeteorShower();
                ClearVoiceQueue();
                PlayVoice(VoiceCue.MeteorShower, VoicePriority.Critical);
                ipWorldView?.Celebrate(dur);
                ipWorldView?.FxPulse("Celebrate");
                Signal("Star", "Shower");
                Signal("Face", "Celebrate");
                _logger.LogEvent("RewardStart", $"round{_flow.CompletedRounds}");
                Debug.Log($"[Flow] 🌠 集满 {_flow.StarsPerRing} 颗 → 奖励！下一轮从 0 开始");
                break;
            }

            case AttentionEvent.RewardEnded:
                EndCelebration(0.4f);
                meteorView?.ClearStars();
                Signal("Star", "RoundEnd");
                _logger.LogEvent("RewardEnd");
                Debug.Log("[Flow] 奖励结束 → 开始下一轮数星星");
                break;
        }

        // 任何离开 Rewarding 的路径都要收掉舞蹈
        if (_celebrating && _flow.State != AttentionState.Rewarding) EndCelebration(0.4f);
    }

    void EndCelebration(float fade)
    {
        if (!_celebrating) return;
        _celebrating = false;
        ipWorldView?.EndCelebrate(Mathf.Max(0.05f, fade));
    }

    /// <summary>指路语音一次性补播：进入指路时若正被外界讲话挡住，等静默后播一次；
    /// 孩子已经看回来（离开 GuidingBack）就丢弃，避免过时的“找老师”。</summary>
    void TryPlayFindVoice()
    {
        if (!_findVoicePending || _flow.State != AttentionState.GuidingBack) return;
        if (_conversationGate != null && !_conversationGate.VoiceAllowed) return;
        _findVoicePending = false;
        PlayVoice(VoiceCue.Find, VoicePriority.Guide);
    }

    // ─────────────────────────────────────────────────────────────
    // 首寻
    // ─────────────────────────────────────────────────────────────
    void UpdateFirstSearch(float dt)
    {
        if (!_searchActive)
        {
            CameraFrame startFrame = default;
            bool hasFrame = false;
            if (ipWorldView != null && ipWorldView.frameSource != null &&
                ipWorldView.frameSource.TryGetFrame(out startFrame) &&
                startFrame.valid && startFrame.texture != null)
                hasFrame = true;
            if (ipWorldView != null && !hasFrame) return; // 等相机就绪，避免“听见声音却看不到精灵”

            float fallbackEyeH = settings != null ? settings.firstSearchEyeHeight : 1.2f;
            Vector3 eyePos = transform.position + Vector3.up * fallbackEyeH;
            Quaternion eyeRot = transform.rotation;
            if (hasFrame)
            {
                eyePos = startFrame.cameraPose.position;
                eyeRot = startFrame.cameraPose.rotation;
            }
            else if (eye != null && eye.position.y > 0.5f)
            {
                eyePos = eye.position;
                eyeRot = eye.rotation;
            }
            else if (Camera.main != null)
            {
                eyePos = Camera.main.transform.position;
                eyeRot = Camera.main.transform.rotation;
            }

            float floorY = eyePos.y - fallbackEyeH;
            Bounds room;
            if (ipWorldView != null && ipWorldView.mruk != null && ipWorldView.mruk.TryGetRoomBounds(out room))
                floorY = room.min.y;
            else
                room = new Bounds(new Vector3(eyePos.x, floorY + 1.5f, eyePos.z), new Vector3(8f, 3f, 8f));

            _firstSearch = new FirstSearchLogic(
                settings != null ? settings.firstSearchMoveSpeed : 0.5f,
                settings != null ? settings.firstSearchRadius : 1.0f,
                settings != null ? settings.firstSearchHoldSeconds : 0.6f,
                settings != null ? settings.firstSearchVoiceInterval : 4f);
            _firstSearch.Begin(eyePos, eyeRot * Vector3.forward, floorY, room);
            _searchActive = true;
            _searchTarget = _firstSearch.CurrentTarget;
            _searchDiagT = 0f;
            _ipAnim = IpAnim.Search;
            _ipAnimT = 0f;
            _ipAlpha = 0f;
            _ipScale = 0.85f;
            _ipSmoothInit = false;
            _logger.LogEvent("FirstSearchStart", $"floor{floorY:F1}");
            Signal("Search", "Start");
            Debug.Log($"[Search] 首寻开始：眼={eyePos.ToString("F2")} 地板Y={floorY:F2} " +
                      $"半径={settings?.firstSearchRadius:F1}m 语音间隔={settings?.firstSearchVoiceInterval:F1}s");
        }

        var res = _firstSearch.Tick(dt);
        _searchTarget = res.target;
        if (res.voiceDue)
        {
            var cue = SearchVoiceOrder[Mathf.Clamp(res.voiceSlot, 0, SearchVoiceOrder.Length - 1)];
            PlayVoice(cue, VoicePriority.Ambient);
            Signal("Search", "Voice");
            Debug.Log($"[Search] 语音 {cue}（第 {_firstSearch.VoiceCount} 句）");
        }
        _searchDiagT += dt;
        if (_searchDiagT >= 2f)
        {
            _searchDiagT = 0f;
            Debug.Log($"[Search] 漫游中：目标={res.target.ToString("F2")} " +
                      $"t={_firstSearch.Elapsed:F1}s 语音={_firstSearch.VoiceCount}");
        }
    }

    void StopFirstSearch()
    {
        if (!_searchActive) return;
        _searchActive = false;
        _firstSearch?.Stop();
        _logger.LogEvent("FirstSearchEnd", "");
        Signal("Search", "End");
    }

    // ─────────────────────────────────────────────────────────────
    // 人脸候选 / 视口目标
    // ─────────────────────────────────────────────────────────────
    void UpdateFaceCandidate(TargetSnapshot snapshot, List<DetectionResult> detections, float dt)
    {
        int ver = simMode ? Mathf.FloorToInt(_now * 30f)
                          : (faceRunner != null ? faceRunner.Version : -1);
        _faceBatchChanged = ver != _faceSeenVer;
        if (_faceBatchChanged) { _faceSeenVer = ver; _faceDataT = _now; }
        float freshWin = settings != null ? settings.faceFreshnessSeconds : 0.8f;
        bool freshFaces = _now - _faceDataT < freshWin;
        var faces = simMode ? _simFaces : (faceRunner != null ? faceRunner.LatestFaces : null);

        bool hasCandidate = false;
        if (freshFaces && faces != null && faces.Count > 0)
        {
            float minW = settings != null ? settings.faceMinBoxWidth : 0.04f;
            float bestD = float.MaxValue;
            Vector2 bestC = default;
            for (int i = 0; i < faces.Count; i++)
            {
                var b = faces[i].normalizedBox;
                if (b.width < minW) continue;
                var c = new Vector2(b.x + b.width * 0.5f, b.y + b.height * 0.5f);
                bool belongs = FaceBelongsToAnyPerson(c, detections);
                if (!belongs && snapshot.valid)
                {
                    belongs = (snapshot.box01.width > 0f && FaceBelongsToPerson(c, snapshot.box01))
                              || Vector2.Distance(c, snapshot.headPoint01) < 0.12f;
                }
                if (!belongs) continue;
                float d = Vector2.Distance(c, new Vector2(0.5f, 0.5f));
                if (d < bestD) { bestD = d; bestC = c; hasCandidate = true; }
            }
            if (hasCandidate)
            {
                _faceConfirmedT = _now;
                _teacherFaceCenter01 = bestC;
                _faceCenteredDist = bestD;
            }
        }
        if (!hasCandidate) _faceCenteredDist = 99f;

        _faceDiagT += dt;
        if (_faceDiagT >= 2f && (hasCandidate || (faces != null && faces.Count > 0)))
        {
            _faceDiagT = 0f;
            string info = hasCandidate
                ? $"主脸距中心 {_faceCenteredDist:F2}"
                : $"检出 {faces.Count} 张脸但均不属于人体（已过滤）";
            Debug.Log($"[Face] {info}");
        }
    }

    /// <summary>老师视口点：优先主脸中心（最近确认过），否则保持最后头点，再退回画面中央。</summary>
    Vector2 TeacherViewportPoint(TargetSnapshot s)
    {
        float win = settings != null ? settings.teacherFaceConfirmSeconds : 1.5f;
        if (_now - _faceConfirmedT <= win)
        {
            _heldTargetPos = _teacherFaceCenter01;
            _hasHeldTarget = true;
            _heldTargetTime = _now;
            return _heldTargetPos;
        }
        return TargetOrCenter(s);
    }

    Vector2 TargetOrCenter(TargetSnapshot s)
    {
        if (s.valid)
        {
            _heldTargetPos = s.headPoint01;
            _hasHeldTarget = true;
            _heldTargetTime = _now;
            return _heldTargetPos;
        }
        if (_hasHeldTarget && _now - _heldTargetTime < 2.5f) return _heldTargetPos;
        return new Vector2(0.5f, 0.5f);
    }

    Vector2 SmoothTarget(TargetSnapshot s, float dt)
    {
        var raw = TeacherViewportPoint(s);
        float d = Vector2.Distance(_smoothTarget, raw);
        if (d > 1.0f)
        {
            _smoothTarget = raw;
            _smoothTargetVel = Vector2.zero;
        }
        else
        {
            float st = d > 0.4f ? 0.10f : 0.15f;
            float ms = d > 0.4f ? 8f : 20f;
            _smoothTarget = Vector2.SmoothDamp(_smoothTarget, raw, ref _smoothTargetVel, st, ms, dt);
        }
        return _smoothTarget;
    }

    // ─────────────────────────────────────────────────────────────
    // 精灵视觉（位置/动作/透明度）
    // ─────────────────────────────────────────────────────────────
    void UpdateSpriteVisuals(float dt, TargetSnapshot snapshot, bool teacherSeen)
    {
        _ipAnimT += dt;
        Vector3 eyePos = eye != null ? eye.position : transform.position + Vector3.up * 1.2f;
        Vector3 eyeFwd = eye != null ? eye.forward : transform.forward;
        bool guiding = State == AttentionState.GuidingBack;
        bool searching = State == AttentionState.FirstSearch;
        bool rewarding = State == AttentionState.Rewarding;

        // ── 视口位置（电脑端 IPDebugView / 真机非世界覆盖时使用）──
        Vector2 teacherPointRaw = TeacherViewportPoint(snapshot); // 老师锚点用原始点（准）
        Vector2 teacherPoint = SmoothTarget(snapshot, dt);        // 精灵视口位置用平滑点（稳）
        Vector2 viewPos;
        if (searching) viewPos = new Vector2(0.5f, 0.45f);
        else if (guiding)
            viewPos = ipWorldView != null ? ipWorldView.TeacherViewportHint() : teacherPoint;
        else
            viewPos = teacherPoint + new Vector2(SideOffset(), -0.02f);
        float k = 1f - Mathf.Exp(-dt / 0.12f);
        _ipPos = Vector2.Lerp(_ipPos, viewPos, k);
        _ipPos = new Vector2(Mathf.Clamp(_ipPos.x, 0.08f, 0.92f),
                             Mathf.Clamp(_ipPos.y, 0.10f, 0.90f));

        // ── 动作/透明度/缩放 ──
        if (searching) _ipAnim = IpAnim.Search;
        else if (guiding) _ipAnim = IpAnim.GuidingMove;
        else if (rewarding) _ipAnim = IpAnim.Companion;
        else _ipAnim = (_speaking && _lookingNow) ? IpAnim.Listening : IpAnim.Attentive;

        _ipAlpha = Mathf.MoveTowards(_ipAlpha, 1f, dt * (searching ? 5f : 2.5f));
        _ipScale = Mathf.MoveTowards(_ipScale, rewarding ? 1.06f : 1f, dt * 2f);

        if (!_ipSmoothInit)
        {
            _ipSmoothInit = true;
            _ipAlphaSmooth = _ipAlpha;
            _ipScaleSmooth = _ipScale;
            _ipScaleVel = 0f;
        }
        else
        {
            _ipAlphaSmooth = Mathf.MoveTowards(_ipAlphaSmooth, _ipAlpha, dt * 4f);
            _ipScaleSmooth = Mathf.SmoothDamp(_ipScaleSmooth, _ipScale, ref _ipScaleVel, 0.25f, 20f, dt);
        }

        float sizeFactor = settings != null ? settings.ipSizeFactor : 1f;
        Color tint = rewarding ? new Color(1f, 0.95f, 0.72f) : Color.white;

        if (ipWorldView != null)
        {
            // 世界位置：
            //   首寻 = 漫游路径点；指路 = 星路起点（视野前下方），飞进视野；
            //   其余 = 不覆盖，由 IPWorldView 用老师视口锚定（精灵贴在老师脸旁）。
            bool worldOverride = false;
            Vector3 worldPos = default;
            float speed = 1.2f;
            if (searching && _searchActive)
            {
                worldOverride = true;
                worldPos = _searchTarget;
                speed = settings != null ? settings.firstSearchMoveSpeed : 0.5f;
            }
            else if (guiding)
            {
                worldOverride = true;
                if (stardustPath != null && stardustPath.TryGetPathStart(out Vector3 pathStart))
                    worldPos = pathStart + Vector3.up * 0.05f;
                else
                {
                    Vector3 flatFwd = new Vector3(eyeFwd.x, 0f, eyeFwd.z);
                    if (flatFwd.sqrMagnitude < 1e-4f) flatFwd = Vector3.forward;
                    worldPos = eyePos + flatFwd.normalized * 0.75f + Vector3.down * 0.25f;
                }
                speed = 2.6f; // 快速飘进视野
            }
            ipWorldView.worldOverrideActive = worldOverride;
            if (worldOverride)
            {
                ipWorldView.worldOverridePos = worldPos;
                ipWorldView.worldOverrideMaxSpeed = speed;
            }
            ipWorldView.pointing = guiding;
            ipWorldView.faceTeacherBlend = guiding ? 1f : 0f;
            ipWorldView.signpostMode = false;
            ipWorldView.companionMode = false;
            ipWorldView.sideHint = 1f;
            // 注意：这里传“本轮是否检测到老师”（不含锚点是否已就绪），
            // 否则第一次检测时 TryGetTeacherWorldPos 还没有值 → IPWorldView 永远不更新老师锚点。
            // 有真人脸检测时，只在“有新的人脸批次”这一帧重锚；否则旧框 + 当前头姿
            // 会把精灵/老师锚点拖着跟头走（孩子看别处也会误判为还在看老师）。
            bool faceBatchAnchor = faceRunner == null || simMode || _faceBatchChanged;
            ipWorldView.targetValid = _teacherDetectedThisFrame && faceBatchAnchor;
            ipWorldView.refreshTeacherAnchor = faceBatchAnchor;
            ipWorldView.targetX01 = teacherPointRaw.x;
            ipWorldView.targetY01 = teacherPointRaw.y;
            ipWorldView.x01 = Mathf.Clamp01(teacherPoint.x + SideOffset());
            ipWorldView.y01 = Mathf.Clamp01(teacherPoint.y - 0.02f);
            if (snapshot.valid) ipWorldView.height01 = snapshot.boxHeight01;
            ipWorldView.visible = _ipAlphaSmooth > 0.01f;
            ipWorldView.alpha = _ipAlphaSmooth;
            ipWorldView.scale = _ipScaleSmooth * sizeFactor;
            ipWorldView.tint = tint;
            ipWorldView.animState = (IPWorldView.IPAnimState)(int)_ipAnim;
        }

        if (ipView != null)
        {
            ipView.visible = _ipAlphaSmooth > 0.01f;
            ipView.x01 = _ipPos.x;
            ipView.y01 = _ipPos.y;
            ipView.alpha = _ipAlphaSmooth;
            ipView.scale = _ipScaleSmooth * sizeFactor;
            ipView.tint = tint;
            string hint = State switch
            {
                AttentionState.FirstSearch => "首寻：游走吸引孩子找老师",
                AttentionState.Watching => _lookingNow ? "看老师：累计星星" : "等孩子看老师",
                AttentionState.GuidingBack => "移开视线：精灵指路 + 星路",
                AttentionState.Rewarding => "星星集满：奖励",
                _ => ""
            };
            string speech = _speaking ? "讲" : "静";
            ipView.statusLine =
                $"状态:{State} | {hint} | 看:{( _lookingNow ? "是" : "否")} " +
                $"累计:{_gaze.Dwell:0.0}s | 讲:{speech} | " +
                $"老师:{(teacherSeen ? "在" : "不在")}/锚{(_flow.HasTeacherAnchor ? "✓" : "✗")} | " +
                $"星:{_flow.LitStars}/{_flow.StarsPerRing} | 路:{(wantPathNow() ? "开" : "关")}";
        }
    }

    bool wantPathNow() => _flow != null && _flow.PathWanted;
    float SideOffset() => settings != null ? Mathf.Clamp(settings.spriteSideOffset01, 0f, 0.3f) : 0.06f;

    // ─────────────────────────────────────────────────────────────
    // 诊断日志
    // ─────────────────────────────────────────────────────────────
    void LogFlowDiagnostics(float dt, TargetSnapshot snapshot)
    {
        _flowDiagT += dt;
        if (_flowDiagT < 2f) return;
        _flowDiagT = 0f;
        Debug.Log($"[Flow] {State} | 看={_lookingNow} 讲={_speaking} 老师={(snapshot.valid ? "在" : "无")} " +
                  $"锚={(_flow.HasTeacherAnchor ? _flow.TeacherAnchor.ToString("F2") : "无")} " +
                  $"星={_flow.LitStars}/{_flow.StarsPerRing}({_flow.StarProgress01:P0}) " +
                  $"轮={_flow.CompletedRounds + 1} 路={(_flow.PathWanted ? "开" : "关")} | " +
                  $"眼={(eye != null ? eye.position.ToString("F2") : "--")}");
        if (stardustPath != null && stardustPath.IsOn)
        {
            _logger?.LogEvent("StarPath", stardustPath.DebugSnapshot());
            Signal("Path", "Update");
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 语音出口
    // ─────────────────────────────────────────────────────────────
    Vector3 IpVoicePos() => ipWorldView != null ? ipWorldView.CurrentWorldPos : transform.position;

    void PlayVoice(VoiceCue cue, VoicePriority priority)
    {
        if (_conversationGate != null && !_conversationGate.VoiceAllowed)
        {
            _logger?.LogEvent("VoiceBlocked", $"{cue}:{priority}");
            Signal("Voice", "Blocked");
            if (priority != VoicePriority.Ambient)
            {
                _pendingVoice = cue;
                _pendingPriority = priority;
                _pendingAt = _now;
                _hasPendingVoice = true;
            }
            return;
        }
        voiceCue?.Play(cue, IpVoicePos(), priority);
        _logger?.LogEvent("VoicePlayed", $"{cue}:{priority}");
        Signal("Voice", "Played");
    }

    void ClearVoiceQueue()
    {
        _hasPendingVoice = false;
        voiceCue?.ClearQueue();
    }

    // ─────────────────────────────────────────────────────────────
    // 纯函数（单测覆盖）
    // ─────────────────────────────────────────────────────────────

    /// <summary>脸中心是否落在某个检测到的人体框头部区域内（过滤海报/纹理假脸）。</summary>
    public static bool FaceBelongsToAnyPerson(Vector2 faceCenter, List<DetectionResult> persons)
    {
        if (persons == null) return false;
        for (int i = 0; i < persons.Count; i++)
            if (FaceBelongsToPerson(faceCenter, persons[i].normalizedBox)) return true;
        return false;
    }

    /// <summary>单框判定：人体框上半部 55% 区域；边距容忍检测抖动。</summary>
    public static bool FaceBelongsToPerson(Vector2 faceCenter, Rect personBox)
    {
        if (personBox.width <= 0f || personBox.height <= 0f) return false;
        return faceCenter.x > personBox.x - 0.05f
            && faceCenter.x < personBox.x + personBox.width + 0.05f
            && faceCenter.y > personBox.y - 0.12f
            && faceCenter.y < personBox.y + personBox.height * 0.55f;
    }

    // ─────────────────────────────────────────────────────────────
    // 重置
    // ─────────────────────────────────────────────────────────────
    void ResetSession()
    {
        _logger?.Close();
        CreateLogger();
        _logger.LogEvent("SessionStart");
        _tracker.Reset();
        _gaze.Reset();
        _flow.Reset();
        _conversationGate.Reset();
        _prevExternalSpeech = false;
        _prevSpeaking = true;
        _speaking = true;
        _simLooking = false;
        _micFallbackLogged = false;
        _hasPendingVoice = false;
        _findVoicePending = false;
        _prevGateListening = false;
        voiceCue?.SetGateAllowed(true);
        voiceCue?.ClearQueue();
        audioPlayer?.SetDuck(1f);

        _firstSearch?.Stop();
        _searchActive = false;
        _searchTarget = default;
        _searchDiagT = 0f;

        _prevTargetValid = false;
        _targetLostEdge = _targetReacquiredEdge = false;
        _faceSeenVer = -1;
        _faceBatchChanged = false;
        _faceDataT = -99f;
        _faceConfirmedT = -99f;
        _teacherFaceCenter01 = default;
        _faceCenteredDist = 99f;
        _faceDiagT = 0f;
        _simFaces = null;
        _lookingNow = false;
        _gazeAtAnchor = false;
        _teacherDetectedThisFrame = false;

        _ipAnim = IpAnim.Hidden;
        _ipAnimT = 0f;
        _ipPos = new Vector2(0.5f, 0.5f);
        _ipAlpha = 0f;
        _ipScale = 1f;
        _ipAlphaSmooth = 0f;
        _ipScaleSmooth = 1f;
        _ipScaleVel = 0f;
        _ipSmoothInit = false;
        _smoothTarget = new Vector2(0.5f, 0.5f);
        _smoothTargetVel = Vector2.zero;
        _heldTargetPos = new Vector2(0.5f, 0.5f);
        _hasHeldTarget = false;
        _heldTargetTime = 0f;

        _celebrating = false;
        _prevPathShow = false;
        _flowDiagT = 0f;

        if (ipWorldView != null)
        {
            ipWorldView.worldOverrideActive = false;
            ipWorldView.ResetTeacherAnchor();
            ipWorldView.pointing = false;
            ipWorldView.faceTeacherBlend = 0f;
            ipWorldView.EndCelebrate(0.05f);
        }
        stardustPath?.SetOn(false);
        stardustPath?.ResetSession();
        meteorView?.ClearStars();
        Debug.Log($"[M3] 会话已重置：{_logger.SessionDir}");
    }

    void OnDestroy() => _logger?.Close();
}

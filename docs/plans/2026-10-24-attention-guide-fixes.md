# 注意力引导 v8 问题修复 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task.
> Steps use checkbox (`- [ ]`) syntax for tracking.
>
> **状态：已确认（2026-10-24）。**
> - 注视期：先 IdleFly + Smile/Curious 去掉挥手；建模师 `Listen` 到位后替换。
> - 外界讲话：精灵语音全部延后；Critical 留一条到静默后；自音频 0.3s 尾。
> - 建模师：要做 `Listen` 动作（见 spec §4）。
>
> **项目 Git 规范：Claude 不执行 `git add/commit/push`；每个任务末尾的提交命令
> 由用户手动执行。**

**Goal:** 让星路一眼看出方向、进度星星可辨认、注视时精灵不再挥手并能在对话中安静倾听，
同时把外界讲话时的精灵语音降为零（不盖住老师）。

**Architecture:** 在现有 `StardustPath`/`MeteorSystem`/`IPWorldView`/`M3DebugDriver`/
`VoiceCuePlayer` 上做增量修改；新增一个纯逻辑 `ConversationGateLogic` 统一“外界讲话/
自音频/语音放行”的判断，并新增 `Attentive`/`Listening` 两个精灵状态；所有可视化改动
先走离线预览，再真机验证。

**Tech Stack:** Unity 6000.4.3f1 / URP / Meta XR SDK 207 / C# / EditMode 测试 /
M3SimHarness 模拟机 / edge-tts 语音（现有音色）。

**Spec:** `docs/specs/2026-10-24-attention-guide-issues.md`

## Global Constraints

- 不读压缩包（项目规范）；不执行 git 写操作（项目规范）。
- 所有新逻辑先写失败测试（TDD）；每个任务结束跑 EditMode 全量 + 模拟机 3 种子。
- 不改用户准备替换的表情/动画美术素材（问题 4）。
- 不合并组员 v1.1 的流星引导代码；本计划只针对当前工程。
- 真机验证必须用手柄启动；adb 启动会被 Horizon 拦截。
- 语音优先级：`Critical=0 < Guide=1 < Ambient=2`（数字越小越高）。
- 注视期“看老师”口径：`_attending = snapshot.valid && looking`（现有实现）。

---

### Task 1: 星路/语音/倾听的会话诊断

**Files:**
- Modify: `Assets/Scripts/UI/StardustPath.cs`
- Modify: `Assets/Scripts/Core/M3DebugDriver.cs`
- Test: `Assets/Tests/EditMode/StardustPathTests.cs`

**Interfaces:**
- Produces: `StardustPath.DebugSnapshot()` → `string`，格式
  `start=(x,y,z) end=(x,y,z) teacher=(x,y,z) angle=±NN len=N.NN width=N.NN dots=N`。
- Produces: 会话事件 `StarPath`（每 2s 一条，路径开启时）；
  事件 `VoiceGate`（门控状态变化时）；事件 `ListenState`（进入/退出 Attentive/Listening）。

- [ ] **Step 1: 写失败测试（格式化函数）**

在 `StardustPathTests.cs` 增加：

```csharp
[Test]
public void DebugSnapshot_ContainsGeometryFields()
{
    var s = StardustPath.FormatSnapshot(
        new Vector3(0, 1, 1), new Vector3(0, 0, 3), new Vector3(0, 1, 3),
        new Vector3(0, 1.6f, 0), 0f, 0.25f, 150);
    StringAssert.Contains("len=", s);
    StringAssert.Contains("width=0.25", s);
    StringAssert.Contains("angle=", s);
}
```

- [ ] **Step 2: 运行并确认失败**

```bash
Unity.exe -batchmode -projectPath D:/Development/Unity/ARAttentionTrainer \
  -runTests -testPlatform EditMode \
  -testResults D:/Development/Unity/ARAttentionTrainer/Builds/editmode-results.xml \
  -logFile D:/Development/Unity/ARAttentionTrainer/Builds/editmode.log
```

Expected: compile error / `FormatSnapshot` not found.

- [ ] **Step 3: 实现格式化 + 接入会话事件**

`StardustPath` 增加纯静态函数：

```csharp
public static string FormatSnapshot(Vector3 start, Vector3 end, Vector3 teacher,
                                   Vector3 eye, float floorY, float width, int dots)
{
    Vector3 dirT = teacher - eye; dirT.y = 0f;
    Vector3 fwd = Vector3.forward; // 调用方传入真实前向时可通过重载
    float ang = dirT.sqrMagnitude > 1e-4f
        ? Vector3.SignedAngle(fwd, dirT.normalized, Vector3.up) : 0f;
    return $"start={start.ToString("F2")} end={end.ToString("F2")} " +
           $"teacher={teacher.ToString("F2")} eye={eye.ToString("F2")} " +
           $"floor={floorY:F2} len={Vector3.Distance(start, end):F2} " +
           $"width={width:F2} dots={dots} angle={ang:F0}";
}

public string DebugSnapshot() =>
    FormatSnapshot(_diagStart, _diagEnd, _diagTeacher, eye != null ? eye.position : Vector3.zero,
                   _diagFloorY, _lastHalfWidth, starDots + BeaconDots);
```

（在 `Tick` 中保存 `_diagFloorY = floorY; _lastHalfWidth = halfWidth;`；
真实前向可在 `FormatSnapshot` 重载中传入，避免角度基准不对。）

`M3DebugDriver` 在星路开启分支中每 2s：

```csharp
_pathLogT += dt;
if (_pathLogT >= 2f)
{
    _pathLogT = 0f;
    _logger.LogEvent("StarPath", stardustPath.DebugSnapshot());
    Signal("Path", "Update");
}
```

- [ ] **Step 4: 运行测试并确认通过**

同 Step 2；Expected: `DebugSnapshot_ContainsGeometryFields` 通过，全量 110 项通过。

- [ ] **Step 5: 用户手动提交**

```bash
git add Assets/Scripts/UI/StardustPath.cs Assets/Scripts/Core/M3DebugDriver.cs \
        Assets/Tests/EditMode/StardustPathTests.cs
git commit -m "test: 星路几何会话诊断" -m "Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

---

### Task 2: 星路方向/宽度重设计

**Files:**
- Modify: `Assets/Scripts/UI/StardustPath.cs`
- Modify: `Assets/Tests/EditMode/StardustPathTests.cs`
- Modify: `Assets/Scripts/Editor/StardustPreview.cs`

**Interfaces:**
- Consumes: existing `MeteorLogic.Bezier/PathControl`, `_smooth` path, `starMat`.
- Produces: `RoadHalfWidthFor` new formula; path with centerline + tapered sides +
  a moving comet; `DebugSnapshot` includes length/width.

**参数（默认值）：**
`starDots=150`、`centerlineFraction=0.40f`、`maxHalfWidth=0.28f`、
`taper=0.62f`、`flowSpeed=0.20f`、`cometSpeed=0.55f`、`cometSize=0.18f`。

- [ ] **Step 1: 更新失败测试（宽度）**

`StardustPathTests.RoadWidth_ShrinksForShortPaths` 改为：

```csharp
Assert.AreEqual(0.10f, StardustPath.RoadHalfWidthFor(0.4f, 0.28f), 0.01f);
Assert.AreEqual(0.28f, StardustPath.RoadHalfWidthFor(5f, 0.28f), 0.001f);
Assert.AreEqual(0.20f, StardustPath.RoadHalfWidthFor(2f, 0.28f), 0.01f);
```

- [ ] **Step 2: 运行确认失败**

Expected: 旧的 `RoadHalfWidthFor` 返回 0.28/0.28 等，断言失败。

- [ ] **Step 3: 实现宽度公式与中心线/侧边分层**

```csharp
public static float RoadHalfWidthFor(float pathLength, float maxHalfWidth)
{
    return Mathf.Clamp(pathLength * 0.10f, 0.10f, maxHalfWidth);
}
```

`Tick` 中点循环拆成两段（中心线 + 侧边）：

```csharp
int centerCount = Mathf.RoundToInt(n * centerlineFraction);
for (int i = 0; i < centerCount; i++)
{
    float t = Mathf.Repeat((i + 0.5f) / Mathf.Max(1, centerCount) + _flow, 1f);
    Vector3 p = CubicBezier(_smooth.p0, _smooth.c1, _smooth.c2, _smooth.p3, t);
    Vector3 side = Vector3.Cross(CubicBezier(..., t + 0.01f) - CubicBezier(..., t - 0.01f),
                                 Vector3.up).normalized;
    if (side.sqrMagnitude < 1e-6f) side = Vector3.Cross(fwd, Vector3.up);
    p += side * (_jSide[i] * 0.08f) + Vector3.up * (0.03f + _jUp[i] * 0.08f);
    float fade = Mathf.Clamp01(t / 0.05f) * Mathf.Clamp01((1f - t) / 0.08f);
    float tw = 0.65f + 0.35f * Mathf.Sin(now * (2.2f + 2.4f * _phase[i]) + _phase[i]);
    float size = starDotSize * 1.25f * _sMul[i] * (0.85f + 0.15f * tw) * fade;
    PlaceDot(i, p, size, fade * (0.8f + 0.2f * tw), ColorFor(0.1f)); // 暖金/白
}

int sideCount = n - centerCount;
for (int j = 0; j < sideCount; j++)
{
    int i = centerCount + j;
    float t = Mathf.Repeat((j + 0.5f) / Mathf.Max(1, sideCount) + _flow, 1f);
    float width = RoadHalfWidthFor(pathLen, maxHalfWidth) *
                  Mathf.Lerp(1f, taper, t); // 起点宽、终点窄
    // ... 与中心线相同的采样，jitter = _jSide[i] * width
    float size = starDotSize * 0.75f * _sMul[i] * ...;
    PlaceDot(i, p, size, fade * (0.55f + 0.2f * tw), ColorFor(0.8f)); // 淡紫
}
```

- [ ] **Step 4: 加一颗沿路移动的“指路彗星”**

在 `EnsureBuilt` 建一个额外的彗星 quad（不占 150 颗），在 `Tick` 中：

```csharp
_cometT = Mathf.Repeat(_cometT + cometSpeed * dt, 1f);
Vector3 cp = CubicBezier(_smooth.p0, _smooth.c1, _smooth.c2, _smooth.p3, _cometT);
_comet.position = cp;
_comet.rotation = Quaternion.LookRotation(cp - eye.position, Vector3.up);
_comet.localScale = Vector3.one * cometSize * (0.9f + 0.1f * Mathf.Sin(now * 6f));
_cometR.enabled = true;
var cm = _cometR.sharedMaterial;
if (cm != null) cm.SetColor("_Color", new Color(1f, 0.96f, 0.78f, 1f));
```

彗星从起点飞向老师，循环；这是“方向感”的主视觉。

- [ ] **Step 5: 离线预览验收**

```bash
"D:/Development/Unity/Editor/Unity.exe" -batchmode \
 -projectPath "D:/Development/Unity/ARAttentionTrainer" \
 -executeMethod StardustPreview.Render \
 -logFile "D:/Development/Unity/ARAttentionTrainer/Builds/stardust-preview-v9.log" -quit
```

检查 `C:/Users/Administrator/.claude/jobs/bbf5228a/tmp/stardust_preview/*.png`：
中心线、锥形宽度、彗星方向；若仍像星团，调整 `centerlineFraction/taper/width` 后再渲染。

- [ ] **Step 6: 运行全量测试**

Expected: 110/110 通过。

- [ ] **Step 7: 用户手动提交**

```bash
git add Assets/Scripts/UI/StardustPath.cs Assets/Tests/EditMode/StardustPathTests.cs \
        Assets/Scripts/Editor/StardustPreview.cs
git commit -m "feat: 星路中心线/锥形宽度/指路彗星" -m "Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

---

### Task 3: 进度星环变大 + celebrate 烟花接线

**Files:**
- Modify: `Assets/Scripts/UI/MeteorSystem.cs`
- Modify: `Assets/Scripts/UI/IpEffects.cs`
- Test: `Assets/Tests/EditMode/FlipbookCelebrateTests.cs`（新建）

**Interfaces:**
- `MeteorSystem.ringStarSize`（默认 0.24f）、`ringRadius`（默认 0.42f）。
- `IpEffects.CelebratePlaying`（bool 探针）；`Pulse("Celebrate")` 启动 celebrate
  flipbook（0.9s 冷却）。

- [ ] **Step 1: 写失败测试（celebrate 脉冲）**

```csharp
[Test]
public void CelebratePulse_StartsCelebrateFlipbook()
{
    var go = new GameObject("fx");
    var fx = go.AddComponent<IpEffects>();
    fx.EnsureForTest();
    Assert.IsFalse(fx.CelebratePlaying);
    fx.Pulse("Celebrate");
    Assert.IsTrue(fx.CelebratePlaying);
    Object.DestroyImmediate(go);
}
```

`IpEffects` 增加 `public void EnsureForTest() => EnsureInit();` 与
`public bool CelebratePlaying => _celebrateFlip != null && _celebrateFlip.IsPlaying;`。

- [ ] **Step 2: 运行确认失败**（`CelebratePlaying` 不存在）

- [ ] **Step 3: 实现 celebrate 接线**

`IpEffects` 增加：

```csharp
Transform _celebrate; MeshRenderer _celebrateR; FlipbookEffect _celebrateFlip;
float _nextCelebBurst;

void EnsureCelebrateFlipbook()
{
    if (_celebrate != null) return;
    _celebrate = MakeQuad("Celebrate", starMat != null ? starMat : glowMat, out _celebrateR);
    if (FlipbookLibrary.TryGet("celebrate", out var c))
    {
        _celebrateFlip = _celebrate.gameObject.AddComponent<FlipbookEffect>();
        _celebrateFlip.Bind(_celebrateR, c.atlas, c.cols, c.rows, c.fps, c.loop);
        _celebrateFlip.Stop();
    }
}
```

在 `EnsureInit`/`EnsureFlipbooks` 调用；`Pulse("Celebrate")` 中：

```csharp
EnsureCelebrateFlipbook();
if (_celebrateFlip != null && Time.time >= _nextCelebBurst)
{
    _nextCelebBurst = Time.time + 0.9f;
    Vector3 eyePos = EyePos();
    _celebrate.position = transform.position + Vector3.up * 0.15f;
    _celebrate.rotation = Quaternion.LookRotation(_celebrate.position - eyePos, Vector3.up);
    _celebrate.localScale = Vector3.one * 1.15f;
    _celebrateR.enabled = true;
    _celebrateFlip.Play(true);
}
```

- [ ] **Step 4: 放大进度星环**

`MeteorSystem`：

```csharp
[Tooltip("进度星环单颗尺寸（米）")] public float ringStarSize = 0.24f;
public float ringRadius = 0.42f;
```

替换 `MakeQuad(go.transform, 0.16f)`、`localScale = 0.16f * tw`、
`BurstStar baseScale = Vector3.one * 0.16f` 为 `ringStarSize`。

- [ ] **Step 5: 运行测试 + 设备预览**

EditMode 全量；设备上确认进度星环明显变大、celebrate 烟花在流星雨时出现。

- [ ] **Step 6: 用户手动提交**

---

### Task 4: 注视期 Attentive/Listening 状态（无挥手）【需用户确认】

**Files:**
- Modify: `Assets/Scripts/Core/M3DebugDriver.cs`
- Modify: `Assets/Scripts/UI/IPWorldView.cs`
- Modify: `Assets/Scripts/UI/IpEffects.cs`
- Modify: `Assets/Scripts/UI/StickFigureIp.cs`
- Modify: `Assets/Scripts/Editor/M3SimHarness.cs`

**Interfaces:**
- `IpAnim.Attentive=7, Listening=8`；`IPWorldView.IPAnimState.Attentive=7, Listening=8`。
- `M3DebugDriver` 在 `_attending && !_guideActive && !_celebrating &&
  _ipAnim != FeedbackPulse/Peek/Hidden` 时进入 `Attentive`；
  `_listening`（Task 6 提供）为 true 时进入 `Listening`。
- Animator 映射（短期）：`Attentive/Listening` 都令 `Guiding=false, Companion=false`
  → `IdleFly`；表情 `Smile`/`Curious`；不设置尚不存在的 Animator 参数。
- 建模师 `Listen` 动作到位后，Task 7 再改为 `Listening` 布尔/状态。

- [ ] **Step 1: 写失败测试（模拟机探针）**

在 `M3SimHarness` 加断言：场景中 `looking=1` 且引导未激活时，
`driver.IpAnimProbe` 必须是 `Attentive`（7）或 `Listening`（8），不能是 `Companion`（3）。

- [ ] **Step 2: 运行确认失败**（当前是 Companion）

- [ ] **Step 3: 实现枚举/驱动/映射**

`M3DebugDriver.UpdateIpAnim` 在 `case IpAnim.Companion` 之前加：

```csharp
case IpAnim.Attentive:
case IpAnim.Listening:
{
    _ipPos = tgt + new Vector2(_companionSideSmooth * 0.10f,
             -0.03f + Mathf.Sin(_now * 1.35f) * 0.009f);
    _ipScale = 0.85f + 0.05f * Mathf.Sin(_now * 1.7f);
    _ipAlpha = Mathf.MoveTowards(_ipAlpha, 0.55f, dt * 0.8f);
    if (_sm.State == InteractionState.Guiding) _ipAnim = IpAnim.GuidingMove;
    break;
}
```

在 `UpdateIpAnim` 开头（`tgt` 计算后）：

```csharp
if (_attending && !_guideActive && !_celebrating &&
    _ipAnim != IpAnim.FeedbackPulse && _ipAnim != IpAnim.Peek && _ipAnim != IpAnim.Hidden)
    _ipAnim = _listening ? IpAnim.Listening : IpAnim.Attentive;
```

`IPWorldView`：

```csharp
public enum IPAnimState { ..., Search = 6, Attentive = 7, Listening = 8 }

bool guiding = visual == Guiding || visual == Search || visual == Found || visual == Leading;
animator.SetBool("Guiding", guiding);
animator.SetBool("Companion", visual == Companion);
// Attentive/Listening: 两 bool 都 false → IdleFly（CompanionIdle 的挥手被绕过）
```

`DriveExpression`: `Attentive → Smile`，`Listening → Curious`；
`DebounceAnim`: `Listening` 视为瞬时（讲话立即切换），`Attentive` 非瞬时；
`IpEffects.SetMood`: 两者都映射到 `Companion`；
`StickFigureIp.PoseFor`: 两者用现有柔和悬浮姿态（无手部硬摆）。

- [ ] **Step 4: 运行测试**

Expected: 模拟机断言通过；109+ 全量通过。

- [ ] **Step 5: 设备验证**

看着老师时不再挥手；短暂移开再回看，动作不闪烁。

- [ ] **Step 6: 用户手动提交**

---

### Task 5: ConversationGateLogic（纯逻辑）

**Files:**
- Create: `Assets/Scripts/Core/ConversationGateLogic.cs`
- Create: `Assets/Tests/EditMode/ConversationGateLogicTests.cs`

**Interfaces:**
- `ConversationGateLogic.Tick(float dt, bool vadSpeaking, bool selfAudioPlaying)`
- 属性：`VoiceAllowed`、`Listening`、`ExternalSpeech`、`SelfAudioSuppressing`。
- 参数：`resumeDelay=0.5f`、`selfAudioTail=0.30f`。

- [ ] **Step 1: 写失败测试**

```csharp
[Test]
public void ExternalSpeech_BlocksAndListens()
{
    var g = new ConversationGateLogic(0.5f, 0.3f);
    g.Tick(0.1f, true, false);
    Assert.IsTrue(g.Listening);
    Assert.IsFalse(g.VoiceAllowed);
    Assert.IsTrue(g.ExternalSpeech);
}

[Test]
public void SilenceResumesAfterDelay()
{
    var g = new ConversationGateLogic(0.5f, 0.3f);
    g.Tick(0.1f, true, false);
    g.Tick(0.3f, false, false);
    Assert.IsFalse(g.VoiceAllowed, "静默不足 0.5s 不应放行");
    g.Tick(0.3f, false, false);
    Assert.IsTrue(g.VoiceAllowed);
    Assert.IsFalse(g.Listening);
}

[Test]
public void SelfAudio_IsNotExternalSpeech()
{
    var g = new ConversationGateLogic(0.5f, 0.3f);
    g.Tick(0.1f, true, true); // VAD 被自己的扬声器污染
    Assert.IsFalse(g.ExternalSpeech);
    Assert.IsFalse(g.Listening);
    Assert.IsFalse(g.VoiceAllowed);
    g.Tick(0.2f, true, false); // self tail 内仍忽略
    Assert.IsFalse(g.ExternalSpeech);
    g.Tick(0.2f, true, false);
    Assert.IsTrue(g.ExternalSpeech);
}
```

- [ ] **Step 2: 运行确认 RED**（类不存在）

- [ ] **Step 3: 实现**

```csharp
public class ConversationGateLogic
{
    public bool VoiceAllowed { get; private set; } = true;
    public bool Listening { get; private set; }
    public bool ExternalSpeech { get; private set; }
    public bool SelfAudioSuppressing { get; private set; }
    readonly float _resumeDelay, _selfTail;
    float _silenceT, _selfT;

    public ConversationGateLogic(float resumeDelay = 0.5f, float selfAudioTail = 0.30f)
    {
        _resumeDelay = Mathf.Max(0.05f, resumeDelay);
        _selfTail = Mathf.Max(0f, selfAudioTail);
    }

    public void Reset() { ... }

    public void Tick(float dt, bool vadSpeaking, bool selfAudioPlaying)
    {
        if (selfAudioPlaying) _selfT = _selfTail;
        else if (_selfT > 0f) _selfT = Mathf.Max(0f, _selfT - dt);
        SelfAudioSuppressing = selfAudioPlaying || _selfT > 0f;
        if (SelfAudioSuppressing)
        {
            ExternalSpeech = false; Listening = false;
            VoiceAllowed = false; _silenceT = 0f;
            return;
        }
        ExternalSpeech = vadSpeaking;
        Listening = vadSpeaking;
        if (vadSpeaking) { _silenceT = 0f; VoiceAllowed = false; }
        else { _silenceT += dt; VoiceAllowed = _silenceT >= _resumeDelay; }
    }
}
```

- [ ] **Step 4: 运行确认 GREEN（4 个测试）**

- [ ] **Step 5: 用户手动提交**

---

### Task 6: 语音门控集成 + 自音频抑制 + 引导降频【需用户确认】

**Files:**
- Modify: `Assets/Scripts/Core/M3DebugDriver.cs`
- Modify: `Assets/Scripts/Audio/VoiceCuePlayer.cs`
- Modify: `Assets/Scripts/Audio/AudioCuePlayer.cs`
- Modify: `Assets/Scripts/Core/GuideDirector.cs`
- Modify: `Assets/Scripts/Core/ExperienceSettings.cs`
- Modify: `Assets/Scripts/Editor/M3SimHarness.cs`
- Test: `Assets/Tests/EditMode/MeteorLogicTests.cs`（GuideDirector 冷却）

**Interfaces:**
- `VoiceCuePlayer.IsPlaying`、`SetGateAllowed(bool)`、`ClearQueue()`。
- `AudioCuePlayer.IsPlaying`。
- `GuideDirectorLogic` 构造增加 `retriggerCooldownSeconds=10f`；
  `GuideConfusedDelay` 默认 0.8 → 1.5s。
- 驱动新增 `PlayVoice(cue, priority)`：门控放行时 `voiceCue.Play` 并
  `Signal("Voice","Played")`；阻断时 `Signal("Voice","Blocked")`，Critical 排队。

- [ ] **Step 1: 写失败测试（Guide 冷却）**

```csharp
[Test]
public void Guide_DoesNotRetriggerDuringCooldown()
{
    var g = new GuideDirectorLogic(1.5f, 1.8f, 0.9f, 6f, 8f, 3, 1f, 2f, 15f, 10f);
    // 进入 Success（或 Silent）后回 Idle，10s 内即使不 attending 也不应再 Confused
    Step(g, 1.0f, true, true, false); // 不触发
    Step(g, 1.0f, false, true, false); // Confused
    // ...推进到 Success...
    var e = Step(g, 1f, false, true, false);
    Assert.AreEqual(GuideEvent.None, e, "冷却期内不应重新触发");
}
```

（具体 Step 见 `MeteorLogicTests` 现有辅助方法。）

- [ ] **Step 2: 运行确认失败**

- [ ] **Step 3: 实现 Guide 冷却 + 触发延迟**

`GuideDirectorLogic` 增加 `_retriggerCooldown/_cooldownT`；在 `EnterSuccess`/`EnterSilent`
时设置 `_cooldownT`；`Idle` 分支开头：

```csharp
if (_cooldownT > 0f) { _cooldownT = Mathf.Max(0f, _cooldownT - dt); return GuideEvent.None; }
```

`ExperienceSettings`：`guideConfusedDelay=1.5f`、`guideRetriggerCooldownSeconds=10f`。

- [ ] **Step 4: 实现语音门控**

`VoiceCuePlayer`：

```csharp
public bool IsPlaying => _source != null && _source.isPlaying;
bool _gateBlocked; VoiceCue _gatedCue; Vector3 _gatedPos; float _gatedAt; bool _hasGated;

public void SetGateAllowed(bool allowed)
{
    if (_gateBlocked && allowed && _hasGated && Time.realtimeSinceStartup - _gatedAt <= 2f)
    {
        var c = _gatedCue; var p = _gatedPos; _hasGated = false; _gateBlocked = false;
        Play(c, p, VoicePriority.Critical);
    }
    _gateBlocked = !allowed;
    if (!allowed) _hasGated = false;
}

public void Play(...)
{
    if (_gateBlocked)
    {
        if (priority == VoicePriority.Ambient) return;
        _gatedCue = cue; _gatedPos = worldPos; _gatedAt = Time.realtimeSinceStartup;
        _hasGated = true; return;
    }
    // 原逻辑
}
```

`AudioCuePlayer` 增加 `IsPlaying`（`_source != null && _source.isPlaying`）。

驱动：

```csharp
bool selfAudio = (voiceCue != null && voiceCue.IsPlaying) ||
                 (audioPlayer != null && audioPlayer.IsPlaying);
_conversationGate.Tick(dt, vadSpeaking, selfAudio);
_speaking = _conversationGate.ExternalSpeech;
voiceCue?.SetGateAllowed(_conversationGate.VoiceAllowed);
_listening = _attending && _conversationGate.Listening;
```

把 `voiceCue?.Play(...)` 调用统一替换为 `PlayVoice(...)`（Critical/Guide/Ambient
按现有优先级），以便信号与门控一致。

- [ ] **Step 5: 调整模拟机 I3 口径**

I3 从“看开视线后 1.4s 内有 Voice:Find 信号”改为：

- 看开视线且外界静默 → 1.4s 内 `Voice:Played`；
- 外界讲话期间 → 允许延迟到“讲话结束 + resumeDelay + 1.4s”；
- 新增断言：`speaking=true` 期间 `Voice:Played` 计数为 0。

（sim 的 `speaking` 就是外界讲话；`selfAudio` 为 false。）

- [ ] **Step 6: 跑全量 + 模拟机 3 种子**

Expected: 测试通过；17 分钟模拟的 `GuideConfused` 计数显著下降（目标 ≤20）。

- [ ] **Step 7: 用户手动提交**

---

### Task 7: 建模师 `Listen` 动作接入（阻塞项）

**Files:**
- Modify: `Assets/Art/IP/CH_Sprite.controller`
- Modify: `Assets/Scripts/UI/IPWorldView.cs`
- Modify: `Assets/Scripts/Core/M3DebugDriver.cs`
- External: 建模师 FBX `Listen` Action + 2–3s 预览

**Interfaces:**
- Animator 参数 `Listening`（bool）+ 状态 `Listen`（loop）。
- `IPWorldView` 在 `visual == Listening` 时 `animator.SetBool("Listening", true)`，
  其余状态 false；`Attentive` 仍走 IdleFly。

- [ ] **Step 1: 收到 `Listen` Action 后导入 FBX，确认 Action 名称/时长/循环**
- [ ] **Step 2: 在 Animator 控制器加 `Listen` 状态与 `Listening` 参数**
- [ ] **Step 3: `IPWorldView.DriveAnimator` 映射 `Listening`（替换 IdleFly 回退）**
- [ ] **Step 4: 真机验证手到耳、无 Root 位移、与 IdleFly 过渡自然**
- [ ] **Step 5: 用户手动提交**

**若建模师暂未交付**：Task 4 的 IdleFly+Curious 回退先上线；本任务保持阻塞，
不阻塞其它任务。

---

### Task 8: 真机验证与日志复核

**Files:** 无（验证任务）

- [ ] **Step 1: 构建并安装**

```bash
"D:/Development/Unity/Editor/Unity.exe" -batchmode \
 -projectPath "D:/Development/Unity/ARAttentionTrainer" \
 -executeMethod BuildScript.BuildDevice \
 -logFile "D:/Development/Unity/ARAttentionTrainer/Builds/build-v9.log" -quit
ADB="D:/Development/Unity/Editor/Data/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb.exe"
"$ADB" install -r "D:/Development/Unity/ARAttentionTrainer/Builds/ARAttention-device.apk"
```

- [ ] **Step 2: 手柄启动，测试以下场景**
  1. 看老师 20s：进度星环变大；无挥手；星路方向明显；
  2. 老师/旁人讲话：精灵不出声；讲话结束后语音恢复；
  3. 移开视线：引导触发不再 0.8s 这么急；语音不密集；
  4. 流星雨：celebrate 烟花出现；
  5. 老师侧后方：星路中心线/彗星指向老师。

- [ ] **Step 3: 拉取日志**

```bash
export MSYS_NO_PATHCONV=1
SESS="/sdcard/Android/data/com.UnityTechnologies.com.unity.template.urpblank/files/sessions"
LATEST=$("$ADB" shell "ls -dt $SESS/* | head -1" | tr -d '\r')
"$ADB" pull "$LATEST" "D:/Development/Unity/ARAttentionTrainer/Builds/device-logs/"
```

检查 `events.jsonl` 中的 `StarPath`/`VoiceGate`/`ListenState` 事件：
星路方向/宽度、讲话期间 `Voice:Played=0`、注视期状态不是 Companion。

- [ ] **Step 4: 记录结果到本 spec（追加“实测结果”一节）**

---

## 自检

- 问题 1 → Task 2；问题 2 → Task 3；问题 3 → Task 4；问题 4 → 明确不做；
  问题 5 → Task 3；问题 6 → Task 6；问题 7 → Task 5/6/7。
- 每个任务都有可执行测试或明确的预览/真机验收；Task 4/6 的行为需用户确认。
- 所有文件/接口名称与现有代码一致：`IPAnimState`、`IpAnim`、`VoicePriority`、
  `FlipbookLibrary.TryGet`、`MeteorSystem.ringStarSize`、`StardustPath.RoadHalfWidthFor`。
- 不包含“TBD/TODO”；Task 7 是明确的阻塞项（等建模师资产），不是占位符。

---

## 执行记录（2026-10-24）

- Task 1–5：完成。EditMode `117/117` 通过（新增 `ConversationGateLogicTests` 5 项、
  `FlipbookCelebrateTests` 1 项、`Guide_RetriggerCooldownAfterSuccess` 1 项）。
- Task 6：完成。
  - 模拟机 3 种子全部通过（20.2 分钟剧本）：`Voice:Played=199`、`Blocked=301`、
    **讲话中播放=0**。
  - `GuideConfused=49/20.2min`。注意：该模拟剧本每一轮都故意“讲话+不看”，
    触发密度远高于真实课堂；实际降噪效果以真机 17 分钟日志复核为准。
  - 为适配“成功后冷却 10s”，模拟机 I3 改为冷却感知（冷却期内不要求语音），
    G1' 离场段从 45s 延长到 68s（最坏链长 15s 静默 + 10s 冷却 + 28.1s 引导链）。
- Task 7：需求文档已写（`docs/specs/2026-10-24-listen-animation-requirements.md`），
  等建模师交付 `Listen` Action；当前 `IdleFly + Curious` 回退已生效。
- Task 8：构建/安装见本轮操作；真机测试结果待补。

## 追加修复（2026-10-25，v9.1 真机第二轮反馈）

用户真机测试后新增 3 个现象 + 3 条建议，已按用户拍板实现：

1. **看到老师星路不立刻消失**：`M3DebugDriver.ShouldShowStarPath(...)` 增加 `!attending`；
   孩子看到老师当帧关闭（旧版要等 Guide Success 的 1s）。新增单测。
2. **老师出视野星环不消失/不重置**：`StarProgressLogic.Tick(dt, looking, teacherVisible)`；
   老师不可见 0.5s → `Reset + Cleared`（星环隐藏、星数清零）；
   孩子只是移开视线、老师仍在画面内时保留星星（B1' 语义不变）。新增单测。
3. **视野里一排竖向星星**：删除 `StardustPath` 的 6 颗竖直“终点星柱”，
   改为贴地 V 形箭头（1 箭尖 + 3 对翼，7 颗星点）；终点侧偏 0.30 → 0.10m，
   铺到老师脚下。`Device.unity` 的 `endSideOffset` 同步改为 0.1。
   同时把中心线/侧轨从固定 90/60 颗改为**按路长定间距**（中心线 0.18m、侧轨 0.16m），
   2m 路约 11 颗中心线星，解决“星星叠成一团发光虫”；沿途再加 2 个 V 形 chevron
   （5 颗/个）强化方向。`flowSpeed` 0.20→0.08、`cometSize` 0.30→0.26。
4. **话术合并**：删除状态机 `GuideAppear/Find` 自动播放，只保留引导导演三段式。
5. **重复语音计时**：仅在 `Leading` 阶段重复；进入 Confused/Found/Leading/RoundUp
   重置 5s/3.5s 计时，修复“Confused 与 Lead1 同帧”“Found 后 0.07s 又 Lead2”。
6. **非语音音效避让**：`AudioCuePlayer.SetDuck()`；外界讲话时音效降到 20%，
   安静时恢复 100%。
7. **精灵指向更明显**：引导时视野偏移权重 0.85 → 0.92，`Point` 手势间隔 2.8 → 2.2s。

验证：EditMode **119/119**；模拟机 20.2 分钟 × 3 种子全绿；
`Voice:Played` 199 → 140（去掉冗余出场句/同帧重复），讲话中播放仍为 0。

## 星路双锚点重做（2026-10-25 晚，用户要求）

用户实测后认为星路“没有正确指向老师、没有延申出视野”，要求完全重做：
**老师位置放一个空间锚点（默认不动）；视野前方放一个空间锚点（路径起点）；
两个锚点生成星路；视野移动显著后才更新星路；锚点用实物标出来调试。**
（特别澄清：老师锚点固定，只有视野锚点会动。）

实现：

- 新增 `StarPathAnchorLogic`（纯逻辑 + 4 个单测）：
  - 老师锚点 = 第一次有效检测到的老师位置，之后**固定**；丢失时保持；
  - 视野锚点 = `eye + forward×0.55m - up×0.95m`（画面底边之外），
    移动 >0.25m 或转角 >12° 且持续 0.15s 才更新；
  - 每次星路开启（`SetOn(true)`）重置锚点，老师锚点取当前/最后已知位置。
- `StardustPath` 重写：
  - 取消每 0.1s 的平滑重算；只在锚点更新时重算贝塞尔路径；
  - `ComputeAnchoredPath`：起点=视野锚点（画面外），终点=老师锚点（3D，头部），
    中段控制点绕开孩子并抬到桌面以上（0.60m），支持老师位于身后的大绕行；
    控制点再用 MRUK 房间边界夹紧（留 0.3m 边距），避免穿墙；
  - 新增 `LineRenderer` 中心线（"路"的主脊）+ 稀疏星点 + 2 个 chevron + 终点箭头；
  - 调试实物：青色球=老师锚点（+垂线+地面圆环），洋红色球=视野锚点；
    `showAnchors` 默认开启，星路关闭时也显示。
- `M3DebugDriver`：即使星路关闭也每帧 `Tick` 锚点（调试标记持续可见）；
  `teacherValid` 传 `HasTeacherAnchor`（丢失期间用最后已知位置）。
- `IPWorldView` 1Hz 日志新增锚点调试字段：
  `老师锚 / target / distH / ray / chosen / focal`，
  用于核对“身高估距 vs 环境射线”到底选了哪个距离。
- `StardustPreview` 适配新锚点；预览图显示一条从画面底边外弯到老师头部的星路。

新增/更新测试：`StarPathAnchorLogicTests`（7 项）、
`AnchoredPath_EndpointsAndDetour`（端点 + 绕开孩子 + 抬升）。

### T0 修复（2026-10-25 深夜）：老师锚点一直移动

根因：
- 星路关闭时 `StardustPath.Tick` 每帧 `_anchors.Reset()`，老师锚点被反复重建；
- `SetOn(true)` 也 `Reset()`，每次看回老师再移开都会重新取位置；
- `StarPath` 诊断里的 `teacher=` 用的是实时低通位置，看起来像锚点在漂。

修复：
- 老师锚点只在**第一次有效检测**时锁定，之后不因检测/路径开关更新；
- 新增 `ResetView()`：新一轮星路只重置视野锚点，老师锚点保持；
- 星路关闭时不再 Reset；只有新会话 `ResetAnchors()`（`M3DebugDriver.ResetSession`）才重新锁定；
- `StarPath` 日志的 `teacher=` 改为锁定的锚点；`IPWorldView` 日志改称 `老师实时=`，
  避免和路径锚点混淆；
- 新增测试 `ResetView_KeepsTeacherAnchorFixed`。

### 2026-11-03 真机追加：锚点被拉到桌面

日志证据：`distH=2.8m, ray=1.1m, chosen=1.1m`（`ray.normal.y` 朝上，命中课桌/地面）；
旧 `ResolveTargetDistance` 允许 0.35~1.8 倍，导致锚点锁在 1.1m 的桌面上，位置不准。

修复：
- `ResolveTargetDistance` 增加 `hitHorizontal`：**水平面命中一律拒绝**；
- 射线只在身高估计 **0.7~1.35 倍**内才采用；
- 锚点锁定从 0.5s 提高到 **1.2s**，并加 **0.25m 稳定性半径**：
  候选位置在半径内连续 1.2s 才锁定；检测/换目标导致位置跳变会重新计时；
- 新增测试：`DeskRayHit_AtOneMeter_IsRejected`、`HorizontalSurfaceHit_IsRejected`、
  `TeacherAnchor_MovingPositionResetsStability`。

### 追加（2026-11-03）：无老师时锚点/星路仍然出现

真机日志（`20260809_192329`）：
- 24.38s 检测到“人”（`targetValid=1`）→ 锚点锁定、星路出现；
- 36.13s `TargetLost` 后星路继续存在到会话结束（41s）——没有新鲜度超时；
- 该“人”没有可确认的脸，疑似误检（椅子/海报/旁人）。

修复：
- `ExperienceSettings.teacherAnchorTimeoutSeconds=1.5`：老师超过 1.5s 未被检测到 →
  `teacherFresh=false`，锚点/星路/调试标记全部隐藏；
- `ExperienceSettings.teacherFaceConfirmSeconds=1.5`：锁定锚点必须在最近 1.5s 内
  检测到**属于人体的脸**（复用 `FaceBelongsToAnyPerson` 假脸过滤）；
- `StarPathAnchorLogic` 锁定前需连续确认 **1.5s** 且位置稳定在 0.25m 内，单帧误检不锁定；
- 首寻结束条件从“任意人体框”改为“最近确认过人体脸的人体框”
  （`_everHadTarget` 只在 `faceConfirmedForTarget` 时置位）；避免误检把首寻骗停；
- 超时后 `ClearTeacherAnchor()`：丢掉旧锚点，下次看到老师重新锁定（不再指向空位置）；
- `IPWorldView.ResetTeacherAnchor()`：按 R 新会话清掉上一轮锚点；
- 日志：锁定锚点时输出 `[Anchor] 锁定老师锚点 ...（已确认人体脸）`。

新增测试：`TeacherAnchor_RequiresStableConfirmation`、`ClearTeacherAnchor_AllowsRelockWhenTeacherReturns`。

## seg 调试验证层（2026-11-03 用户新方案）

背景：用户确认 **seg 模型并没有真正放弃**（场景里 `SelfieSegRunner` 仍启用、0.5s/次）；
最初设想是“精灵躲到老师身后/探头”，需要人体轮廓做遮挡。先不做精灵动作，
只验证分割是否正确。

实现：

- `SelfieSegRunner` 增加 `Active`/`SetActive(bool)`：**YOLO 人体 + BlazeFace 人脸
  都检测到**（`snapshot.valid && faceConfirmedRecently`）才开启推理；丢失则暂停，
  省 CPU；`IPWorldView` 的伴随轮廓使用也加 `seg.Active` 判断。
- 新增 `SegMaskHighlight`（运行时由 `M3DebugDriver.InitCore` 挂到 PCA 对象）：
  在中心眼前 0.2m 建全屏四边形，用 `MaskOccluder.ComputeRows` 的同一单应矩阵
  把眼相机屏幕 UV 映射到 256×144 掩码 UV。
- 新增 `Assets/Shaders/SegMaskHighlight.shader` + `Assets/Resources/SegMaskHighlight.mat`：
  青色填充 + 掩码梯度轮廓更亮；只做调试高亮，不做遮挡。
- 验证：人+脸确认后青色高亮出现在真实老师身上；人/脸丢失后高亮消失；
  检查分割边缘是否贴合老师（头发、肩膀、手臂）。

后续（分割确认正确后）：用 `MaskErase` 做精灵被真实人体遮挡的“躲到身后/探头”交互。

### 2026-11-03 二次真机反馈：高亮仍不可见 + 锚点锁太快

真机现象：人+脸日志出现过 `[Seg] 人+脸已确认 → 开启分割`、`[Seg] 41ms 覆盖 7%`，
但用户看不到青色轮廓；同时老师锚点锁定过快、位置不准。

高亮不可见的排查与修复：

- **四边形距离**：0.2m → **0.5m**。`OVRCameraRig` 单眼相机 `near clip = 0.3`，
  0.2m 的全屏四边形会被近裁面整个裁掉（中心眼 near=0.1，容易误判为安全）。
- **眼相机选择**：优先选“启用 + 无 targetTexture + stereo 相机”，
  避免选到采集相机/非渲染相机；日志打印 `near/stereo/targetTex/culling`。
- **门控锁存**：人脸检测会间歇性丢帧，旧逻辑 `faceConfirmedWithin(1.5s)` 会让
  seg 反复开关；调试期改为“本会话确认过一次人体脸就保持开启（仍需 person 有效）”，
  避免高亮闪烁/整段看不到。手柄 **B/Y** 可强制开关 seg，用于区分门控与渲染问题。
- **人脸归属兜底**：除 `detections` 外，脸落在**当前跟踪人框**内或距**跟踪头部点**
  <0.12 也认作老师脸，解决“人体检测批次为空/滞后时真脸全被判假脸”；
  `faceMinBoxWidth` 0.04 → **0.03**（3m 外老师脸宽约 3%~4%，0.04 会漏检）。
- **全画面弱高亮 + 目标框强高亮**：目标框外扩从 4% 提到 15%；
  即使 YOLO 目标框偏了，整幅掩码仍有一层淡青色，能看出分割是否出来。
- **右上角原始掩码小窗**：`SegMaskPreview.shader` 直接显示 256×144 掩码，
  不经过单应映射；主高亮若仍不可见，小窗可独立证明“分割有没有跑”。
- **阈值/亮度**：`_EdgeLow/High` 0.5/0.75 → 0.4/0.7，填充/轮廓 alpha 提高；
  适配远处老师掩码值偏软的情况。
- **单应焦距单位错误（根因）**：`frame.focalLengthPx` 是**摄像头纹理像素**（如 960×544），
  掩码是 256×144；旧代码直接 `focal/maskW`，把映射放大约 **3.75 倍**，
  画面边缘的老师掩码落到 `[0,1]` 外 → 整块高亮消失。现在先缩放
  `fx = focal × maskW / texW`、`fy = focal × maskH / texH`，再算单应；
  新增单测 `TextureFocal_IsScaledToMaskPixels` 锁定该换算。
- 新增编辑器渲染探针 `SegHighlightRenderProbe`：
  不等真机，直接用 URP `SingleCameraRequest` 渲染带顶部横带的合成掩码，
  检查“着色器是否编译、四边形是否画出来、上下是否翻转”。
  **结果（2026-11-03）**：`Builds/seg-highlight-probe.png` 顶部横带、外部透明，
  说明着色器/四边形/UV 方向均正常；真机不可见只剩“近裁面”或“门控/映射”两类原因。

锚点锁定修复：

- 位置稳定确认从 1.2s 提高到 **1.5s**（用户反馈“锁太快”）；
  候选位置必须在 0.25m 半径内连续 1.5s 才锁定，期间人脸/人体丢失会重新计时。
- `ResolveTargetDistance` 已在上一节修复水平桌面射线；本节保持。

星路终点修复：

- `ComputeAnchoredPath` 的 `p3` 从“老师头部锚点”改为 **老师脚下**
  （`floorY + 0.05m`，x/z 仍取锚点 + 侧偏），与用户定调的“老师脚下 + 贴地箭头”一致；
- 终点箭头方向去掉 Y 分量，保证 V 形箭头平铺在地面，不顺着下坡翘起。

验证（2026-11-03）：

- EditMode **131/131** 通过（新增 `TextureFocal_IsScaledToMaskPixels`）；
- `M3SimHarness` 20.2 分钟 × 3 种子全绿，讲话期间语音播放 = 0；
- `SegHighlightRenderProbe` 渲染通过，`Builds/seg-highlight-probe.png`
  为“顶部横带 + 外部透明”，证明着色器/四边形/UV 方向正常。

## 2026-11-18 真机反馈：seg 两团蓝雾、星路锚点/触发、房间级游戏空间

### 1) seg 两团蓝色雾（先分析，不修）

用户描述：人体分割完全失败，看到两团影响视线的蓝色雾。
初步判断：**主要是分割模型误检，不是显示映射**。

依据：

- 2026-11-03 的 Python 测试视频已记录：SelfieSeg 对窗户反光、白板等大面积亮区有误检；
- v9.4 为了验证映射，把高亮改成“全画面淡青 + 目标框内强青”，
  等于把目标框外的误检也显示出来；两团蓝雾与“窗户/白板两块亮区”高度吻合；
- 映射链路已由 `TextureFocal_IsScaledToMaskPixels` 和渲染探针验证；
  如果是映射错误，通常看到的是一个被拉伸/偏移/双眼重影的人形，而不是两块独立的大区域。

快速确认（下次测试）：看右上角原始掩码小窗——若小窗里也是两团，说明掩码本身错；
若小窗正常，只有主画面两团，则是显示/立体问题。也可遮住窗户/白板看蓝雾是否消失。

若后续继续：应对 YOLO 人框裁剪后送入 seg、提高阈值、加时序一致性；
production 不应显示全画面淡青。当前按用户要求不继续修，可能放弃该线。

### 2) 星路锚点抬高 + 触发放宽

真机会话日志（20260825_011941）证据：

- 老师锚点 `teacher=(1.62,-0.69,0.40)`，地板 `-1.54` → 锚点只有 **0.85m** 高，
  落在老师腰部而不是头部；
- 路尾 `end=(1.64,-1.49,0.30)` 贴地；起点 `start=(0.09,-0.90,0.23)` 约 0.64m 高；
- 早期 `StarPath` 事件 `start/end` 全 0：`IsOn=true` 但锚点还在 1.5s 稳定期，
  路径没生成——用户看到“有时不显示”。

修复：

- **老师锚点改用主脸中心**（`_teacherFaceCenter01`），不再用 YOLO 人框 25% 头点；
  没人脸时回退到最后已知头点。锚点锁定本身要求人脸，所以锁定瞬间一定有脸。
- **视野锚点抬高**：`startDrop 0.95 → 0.60`，`startForward 0.55 → 0.60`。
- **路尾抬高**：新增 `endHeight=0.45f`，`ComputeAnchoredPath` 的 `p3.y = floorY + endHeight`
  （旧重载仍默认 0.05，兼容旧测试/预览）。
- **触发改为眼睛夹角**：不再要求引导导演激活，也不依赖 `_attending`；
  `Vector3.Angle(eye.forward, teacherAnchor - eye.position)` **>8° 显示、<5° 隐藏**（滞回），
  只要老师偏离视野中央就出现，哪怕 YOLO 仍检测到人。
- **必须已锁定锚点才开路**：`pathAnchorReady = HasTeacherAnchor`，不再用 `canLockAnchor`
  提前打开，消除 `IsOn=true` 但路径全 0 的窗口。
- 新增 `[Path] 显示/隐藏 eyeAngle=...` 日志，便于现场核对触发。

### 3) 房间级游戏空间

`OVRCameraRig.prefab` 的 `OVRManager._trackingOriginType = 0`（EyeLevel，静止小圈）。
`DeviceSceneCreator` 现在显式设置：

```csharp
manager.trackingOriginType = OVRManager.TrackingOrigin.Stage;
```

启动日志新增 `[XR] TrackingOrigin=...`。OpenXR 下 Stage 会使用房间级 Floor 空间。
注意：系统 Guardian 若仍是 Stationary 小圈，应用无法扩大；需要用户在 Quest
设置里重画房间边界。

### 验证

- EditMode 测试、构建和真机安装见本轮命令输出；
- 会话日志：`Builds/session-20260825_011941/`（用户 11-18 测试）。

## 2026-11-18 晚追加：YOLO 人框裁剪送 seg + Floor/边界抑制

### seg 裁剪

用户要求“用 YOLO 检测框内的图像送 seg 试一次”：

- `SelfieSegRunner` 新增 `Crop01` / `MaskCrop01`：
  - `ComputeCrop(box01)`：人框外扩 12%，限制在 [0,1]；**不扩成 16:9**
    （16:9 会给远处瘦高的人框左右补进大片背景，反而保留窗户/白板误检）；
    人框直接拉伸到 256×144 送模型；
  - 推理前把源纹理裁剪区域 `Graphics.Blit` 到 256×144 sRGB RT，再送 seg；
  - `MaskCrop01` 记录“产出当前掩码时实际用的裁剪框”，高亮不能用正在变化的新框；
  - `TryGetEdge` 把全画面坐标转换到裁剪局部，再转回，陪伴模式不破。
- `SegMaskHighlight`：
  - 着色器新增 `_CropRect`，全画面 UV → 裁剪局部 UV；
  - 目标框也转到裁剪局部；小窗直接显示裁剪掩码；
  - 小窗从右上角 (0.42,0.24) 挪到 (0.26,0.18) 并放大到 0.18m（用户上次没看到）；
  - 日志增加 `crop=...`。
- 预期：模型输入以老师为主，窗户/白板/反光的大块误检应大幅减少；小窗可直接确认。

### 游戏空间

用户确认 Quest 3 房间级游戏可走动；v9.5 的 Stage 只改了世界原点（会话 floor 从 -1.54 变 0），
但 Guardian 小圈仍在。按 Meta 官方 MR 建议改为：

- `OVRManager.trackingOriginType = FloorLevel`（不是 Stage）；
- `OVRManager.shouldBoundaryVisibilityBeSuppressed = true`（透视已开启，抑制 Guardian 边界）；
- 启动日志增加 `[XR] TrackingOrigin=... boundarySuppressed(desired/actual)=...`；
- 系统 Guardian 仍建议房间规模；若系统强制 Stationary，在应用内快捷设置改回房间边界。

参考：
- Meta OVRManager reference / boundaryless reference（agentic-tools）；
- Unity OpenXR Meta Boundary visibility 文档；
- Meta 社区 v74 Stationary 边界回归讨论。

### 测试

- 新增 `Crop_ContainsPersonBoxAndStaysInside`、`Crop_NearEdge_StaysInside`、
  `Crop_InvalidBox_FallsBackToFullFrame`；
- EditMode **135/135** 通过。

## 2026-11-18 深夜：删除 seg + 无边界（BOUNDARYLESS_APP）修复

### 1) seg 功能删除

用户真机确认：裁剪后小窗/主画面仍是乱蓝色，seg 不可用。处理：

- `DeviceSceneCreator` 不再添加 `SelfieSegRunner`，也不再添加 `MaskOccluder`；
- `M3DebugDriver` 删除 `SegMaskHighlight` 创建、seg 开关、`B/Y` 强制开关、
  `_faceEverConfirmed`/`_segForce`、`SetTargetBox`、`targetBox01`；
- 删除 `SegMaskHighlight.cs`、`SegMaskHighlight.shader`、`SegMaskPreview.shader`、
  两个材质、`SegHighlightRenderProbe.cs` 及 meta；
- `SelfieSegRunner.cs`/`MaskOccluder.cs`/onnx 保留但未挂载（以后换模型再启用）；
- 主画面不再有蓝色污渍。

### 2) 游戏空间：log 证明不是 Floor/Stage 写错

真机系统日志：

- `AutoStationaryHelper::getGuardianDataFromNearbyRoomscale] Checking 0 existing guardian data`
- `SpatialShiftLogger ... numGuardians: 0 hasActiveSafetyBound: 0`
- `GuardianTelemetry ... confirm_autogenerated_guardian`
- Guardian 状态 `BoundaryNotFound -> HasActiveSafetyBoundary`

结论：头显当前**没有房间级 Guardian 数据**，系统自动生成 Stationary 小圈；
应用把 tracking origin 改成 Floor/Stage 只改世界原点，**无法创建系统 Guardian**。
走两步弹“在这里建立游戏边界”是系统 Guardian 的设置提示，不是应用能直接画出来的。

应用侧正确修复：无边界 MR 需要 manifest 声明

```xml
<uses-feature android:name="com.oculus.feature.BOUNDARYLESS_APP" android:required="true" />
```

- 已写入 `Assets/Plugins/Android/AndroidManifest.xml`；
- `AndroidManifestPatcher` 在构建后对最终 manifest 兜底写入；
- 保留 `FloorLevel` + `shouldBoundaryVisibilityBeSuppressed=true`；
- 启动日志 `[XR] TrackingOrigin=... boundarySuppressed(desired/actual)=...`。

注意：BOUNDARYLESS_APP 没有 Guardian 保护，测试场地要安全；Quest Store 可能拒绝。
应用侧无法创建系统 Guardian；若系统仍不接受 BOUNDARYLESS_APP，只能在头显里手动创建
房间级边界或使用应用内快捷设置。

### 验证

- EditMode **135/135** 通过；
- v9.7 构建/安装见构建日志。

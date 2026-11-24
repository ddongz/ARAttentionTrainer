# ARAttentionTrainer — AR 注意力引导系统

> 南京大学大创项目「面向孤独症儿童的社交融合系统」核心软件
> 结题目标：2026 年 12 月｜目标平台：Meta Quest 3

## 这是什么

一个跑在 **Meta Quest 3 混合现实眼镜**上的训练应用。

孩子戴上眼镜上课时，系统用眼镜前置摄像头实时看着周围的大人（老师/家长），
并有一个**卡通小精灵**在孩子的真实视野里飞来飞去「陪着」他：

- **开局** → 小精灵先出现在**视野中央**，再在正前方平滑游走 + 语音吸引，
  引导孩子抬头/转头去找老师；
- 孩子**第一次看到老师** → 在老师位置放下**世界锚点**，进入"看老师"状态；
  **此时不生成星路**；
- 孩子**看着老师** → 老师头周开始积累**进度星**（每 2.5 秒一颗），
  集满 8 颗触发**流星雨 + 庆典**，奖励结束后自动开始下一轮（星星归零继续累计）；
- 孩子**看过老师后又移开视线** → 小精灵触发"找老师"语音、飘进孩子视野、手指老师方位，
  同时用**老师锚点 + 当前视野锚点**铺一条指向老师的**星路**；
  孩子的目光一回到老师身上，星路**立即关闭**，继续数星星；
- **老师/旁边人讲话** → 小精灵进入**倾听态**（最终版为扶耳动作），**完全不插话**；
  讲话结束约 0.5 秒后才恢复语音；
- 全程**小精灵绝不消失**——它像一个小伙伴，用"寻找 → 陪伴 → 指路 → 奖励"的循环，
  帮孩子一点点养成"看对方"的注意力习惯。

这套思路参考了 MIT 背景的 Empowered Brain（Face2Face）项目，并针对课堂场景做了改进。

## 技术全景（一张图读懂）

```
              ┌──────────────── 眼镜前置摄像头 ────────────────┐
              │                                               │
        ┌─────▼─────┐   ┌──────────┐   ┌──────────────┐
        │ YOLOv9    │   │ BlazeFace│   │ SelfieSeg    │   两个在用；分割模型
        │ 检测"人"  │   │ 检测"脸" │   │ 已停用       │   2026-11-18 删除
        └─────┬─────┘   └────┬─────┘   └──────────────┘   （不再挂载）
              └──────┬───────┴
                     ▼
        ┌────────────────────────┐   ┌──────────────────┐
        │ 逻辑大脑（纯代码）      │   │ 麦克风语音检测   │
        │ · 老师在哪、孩子在不在看 │◄──┤ 判断老师是否在讲话│
        │ · 注视累计 / 人脸居中计时│   └──────────────────┘
        └───────────┬────────────┘
                    ▼
        ┌────────────────────────┐
        │ 小精灵（3D 模型+动画）  │ 状态机驱动：
        │ 引导 / 陪伴 / 倾听 /    │ 引导→陪伴→落星→流星雨
        │ 反馈 / 庆典 + 特效      │ + 语音&音效&星尘路&情绪泡泡
        └────────────────────────┘
```

小知识：AI 模型通过 **Unity Sentis**（Unity 官方的模型运行库）在眼镜本地运行，
**不需要联网**，摄像头画面也不上传。

## 组员快速上手

### 只想看看项目 / 改文档
不用装任何软件，从本 README 和 `docs/` 开始读就行（文档导航见文末）。

### 想运行起来看效果（电脑上预览）
1. 安装 **Unity Hub** + **Unity 6000.4.3f1**（安装时勾选 Android Build Support）
2. Unity Hub → Add → **Add project from disk** → 选中本文件夹
3. 打开后双击场景 `Assets/Scenes/DevEditor.unity` → 点 Play
   （场景缺失时：菜单栏 **ARAttention → Dev → Create DevEditor Scene** 重建）
4. 电脑端没有眼镜，用键盘操作：**K** = 切换"孩子在注视"，**L** = 切换"老师在讲话"，**R** = 重置会话

### 想装到 Quest 3 上（真机）
1. Quest 3 用 USB 连电脑（开启开发者模式），确认 `adb devices` 能看到设备
2. （首次或场景缺失时）菜单栏 **ARAttention → Device → Create Device Scene** 重建真机场景
3. 命令行打包（暂无一键菜单，在工程根目录执行，Unity 路径换成你自己的）：
   ```bash
   # 调试版：带识别框/数据面板/显式锚点/AX 切档
   "<Unity安装路径>\Editor\Unity.exe" -batchmode -quit -projectPath "<本工程路径>" -executeMethod BuildScript.BuildDevice

   # 上线版：无识别框/数据面板/显式锚点/AX 调试输入，非 Development 构建
   "<Unity安装路径>\Editor\Unity.exe" -batchmode -quit -projectPath "<本工程路径>" -executeMethod BuildScript.BuildDeviceProduction
   ```
   产物：调试版 `Builds/ARAttention-device.apk`；上线版 `Builds/ARAttention-release.apk`
4. 安装到眼镜（adb 在 Unity 安装目录的 `PlaybackEngines/AndroidPlayer/SDK/platform-tools/` 下）：
   ```bash
   adb install -r -g Builds/ARAttention-release.apk   # 上线版
   ```
5. 在眼镜里**拿起手柄**启动应用（Horizon 系统要求必须拿手柄）；上线版启动后不需要再按手柄

## 目录结构（哪里放着什么）

| 路径 | 内容 |
|------|------|
| `Assets/Scripts/Core/` | **总装胶水层**：M3DebugDriver、**AttentionFlowLogic（唯一主流程状态机）**、经验参数（ExperienceSettings） |
| `Assets/Scripts/Detection/` | AI 模型运行器（人体/人脸检测）+ 解码 |
| `Assets/Scripts/Tracking/` | 目标跟踪（锁定"老师"这个人并平滑他的位置） |
| `Assets/Scripts/Gaze/` | 注视判定（孩子在看哪个方向） |
| `Assets/Scripts/UI/` | 小精灵的可视化（3D 模型、特效、情绪泡泡）+ 调试面板 |
| `Assets/Scripts/FrameSource/` | 摄像头画面接入 |
| `Assets/Scripts/Audio/`、`Vad/` | 语音提示 / 麦克风说话检测 |
| `Assets/Scripts/Logging/` | 会话数据记录（双眼见闻都被记下来供分析） |
| `Assets/Tests/EditMode/` | 单元测试（141 个，改代码后必须全绿） |
| `Assets/Art/IP/` | **美术资源**：精灵模型、贴图（清单见 `docs/assets-index.md`） |
| `Assets/Models/` | AI 模型文件（.onnx / .sentis） |
| `Assets/Scenes/` | Device（真机）、DevEditor（电脑调试） |
| `docs/` | 文档（设计/计划/实测/资产清单，导航见 `docs/README.md`） |

## 各角色怎么参与

### 🎨 美术组
- 改精灵模型：从交付包 `source/CH_Sprite_master.blend` 改 → 重新导出 FBX → 覆盖 `Assets/Art/IP/CH_Sprite.fbx`
- 改特效/贴图样式：改 `docs/design/*.html` → 截图导出 PNG → 覆盖 `Assets/Art/IP/` 同名文件
- 资源清单与说明：`docs/assets-index.md`

### 🧪 测试组
- **模拟测试机**（不用眼镜，把 20 分钟课堂剧本演一遍验证逻辑）：
  Unity 菜单 或命令行 `-executeMethod M3SimHarness.Run`（3 个随机种子必须全绿）
- **单元测试**：Unity 菜单 Window → General → Test Runner → EditMode → Run All
- 真机测试清单与观察要点：`docs/test-video-guide.md`

### 📝 文档/其他
- 想看设计意图和每个参数怎么定的 → `docs/specs/2026-10-07-ar-attention-design.md`
- 提出改进意见 → 项目群里说，或写进对应文档

## 目前进度（2026-11-24）

### ✅ 已完成

- **v10 注意力主流程重构（2026-11-24）**：把"星路 / 语音 / 精灵位置 / 移动"的旧逻辑
  全部收拢到唯一的 `AttentionFlowLogic` 状态机：
  - `FirstSearch`：精灵在视野中央出现 → 连续平滑游走（不再逐点停顿/瞬移）+ 语音吸引；
  - `Watching`：孩子第一次真正看向老师时放锚点（**不生成星路**）；看着老师累计星；
    集满 → 奖励 → 下一轮自动从 0 开始；星星不再因走神/老师短暂出画面而清空；
  - `GuidingBack`：看过老师后移开视线才触发——精灵飘进视野、手指老师、语音一次，
    星路 = 老师锚点 + 当前视野锚点；看回来 **立即收路**；
  - 删除了旧的"讲话驱动状态机 / 三段式引导导演 / 星星清空"逻辑；
  - 语音空间声源跟随精灵；被外界讲话挡住时，"找老师"语音会在静默后补播一次；
  - EditMode 单测与模拟测试机已按新流程重写。
- **真机全链路**：透视 + 人体/人脸检测 + 引导/隐藏/反馈已在 Quest 3 跑通。
- **流星引导交互 v2**：注视累计落星 → 8 颗触发流星雨 + 庆典；
  三段式引导（疑惑 → 发现 → 带领 → 成功/静默）。
- **v9 注意力引导修复**（本轮，对应 2026-10-24 真机问题清单）：
  - **星尘路重做**：60% 星星固定在中心线（暖金、1.5 倍大）+ 两侧淡紫护栏 +
    起点宽终点窄的锥形宽度 + 一颗沿路飞向老师的**指路彗星**，方向一眼可辨；
  - **进度星环**：单颗 0.16m → 0.24m，半径 0.42m，注视时清晰可见；
  - **注视期不再挥手**：新增 `Attentive` / `Listening` 状态，播放 `IdleFly`，
    不再进入带挥手的 `CompanionIdle`；
  - **对话门控**：老师/旁人讲话时精灵**完全不插话**（语音延后或丢弃），
    系统自己的 TTS/音效播完后 0.3s 内忽略麦克风，防止自听；
  - **引导降频**：触发延迟 0.8 → 1.5s，成功后 10s 冷却，短暂移开不再频繁喊话；
  - **celebrate 烟花**：接入流星雨庆典，集满 8 星时在精灵旁播放。
- **v9.2 真机第二轮修复**（2026-10-25，按用户反馈）：
  - **星路立刻消失**：看到老师当帧关闭，不再等引导成功（旧版会多留约 1s）；
  - **星环重置**：老师出视野 **0.5s** 后星环消失、星数清零；只是移开视线但老师仍在画面内时保留；
  - **竖向星柱改贴地箭头**：删掉 6 颗竖排星，改为中心线/侧轨按路长稀疏铺开 +
    2 个沿途 chevron + 终点 V 形箭头 + 彗星；
  - **话术合并**：删除状态机“看这里呀/看看老师在哪儿呀”，只保留引导导演三段式；
  - **重复语音**：只在 Leading 阶段重复，修复“疑惑+跟我来”同帧、“找到+跟我来”紧贴；
  - **音效避让**：外界讲话时星星叮/流星雨降到 **20%** 音量；
  - **指向增强**：引导时精灵更贴向老师方向，`Point` 手势间隔 2.8 → 2.2s。
- **v9.3 星路双锚点重做**（2026-10-25 晚，用户要求完全重做）：
  - **老师锚点**：第一次检测到老师的位置，之后**固定不动**（"上一次看到老师的位置"）；
    **T0 修复（2026-10-25）**：星路关闭时也不再重置老师锚点；只有新会话（R）才重新锁定；
    **2026-11-03 追加**：锁定前必须连续确认 **1.5s**、位置稳定在 0.25m 内，
    且最近 **1.5s** 内检测到属于人体的脸；距离估计拒绝桌面/地面射线（只接受身高估计 0.7–1.35 倍）；
    首寻也只在确认人脸后才结束（不再被椅子/海报误检骗停）；
    老师超过 **1.5s** 未被检测到 → 锚点/星路自动隐藏；再看到老师时重新锁定；
  - **视野锚点**：当前视野前下方（画面底边之外），随头动；
    只有移动 >0.25m 或转角 >12° 且持续 0.15s 才更新星路；
  - 星路从画面下缘附近铺到**老师身侧**（路尾离地 **0.45m**，不再贴地；从更多角度可见），
    中段绕开孩子、抬到桌面以上；
  - **2026-11-18 锚点抬高**：老师锚点改用**主脸中心**（旧版人框 25% 头点在真机会偏低到腰部）；
    视野锚点 `startDrop` 0.95 → **0.60**、`startForward` 0.55 → **0.60**；
  - **2026-11-18 星路触发放宽**：不再要求引导导演激活，也不依赖宽口径 `attending`；
    眼睛 forward 到老师锚点的 **3D 夹角 >8° 显示、<5° 隐藏**（滞回），
    只要老师偏离视野中央就出现——即使 YOLO 仍检测到人；
  - 新增中心线 `LineRenderer` + 稀疏星点 + 2 个 chevron + 终点箭头；
  - **锚点实物调试**：青色球=老师锚点（带垂线+地面圆环），洋红色球=视野锚点；
    星路关闭时也显示，用于核对"检测到的老师锚点到底准不准"；
  - `IPWorldView` 1Hz 日志增加 `distH / ray / chosen / focal`，可判断锚点距离用的是身高估计还是环境射线。
- **2026-11-18 游戏空间：无边界 MR**：真机日志出现
  `AutoStationaryHelper ... 0 existing guardian data`、`numGuardians: 0`
  → 头显当前**没有房间级 Guardian**，系统自动生成 Stationary 小圈，走两步就弹“建立游戏边界”。
  应用侧已做：FloorLevel + **`com.oculus.feature.BOUNDARYLESS_APP`**（全无边界模式），
  并启用 **Use Custom Main Manifest**（`useCustomMainManifest=1`），`AndroidManifestPatcher` 兜底。
  **注意：BOUNDARYLESS_APP 与 `shouldBoundaryVisibilityBeSuppressed`（contextual）互斥**，
  同时开会打架；现在关闭 contextual，只走 manifest 全无边界。
  启动日志 `[XR] TrackingOrigin=... boundarySuppressed(desired/actual)=...`（desired 应为 False）。
  若系统仍弹边界，是 OS 没接受 BOUNDARYLESS_APP：可在头显开 Travel Mode / 关闭
  “Physical space features” / 手动创建房间级 Guardian。无边界应用无 Guardian 保护，测试场地要安全。
- **seg 功能已删除（2026-11-18 用户决定）**：真机小窗和主画面都是乱蓝色，
  确认 SelfieSeg 在本场景不可用。已从 Device 场景移除 `SelfieSegRunner`，
  删除 `SegMaskHighlight` / `SegMaskPreview` / 高亮材质 / 渲染探针；`M3DebugDriver`
  不再开启 seg，也没有 B/Y 强制开关。模型脚本/onnx 保留但未挂载，方便以后换模型再启用。
  历史测试见 `docs/measurements/2026-11-03-seg-video-test.md`。
- **动效素材**：7 个 flipbook（表情/光效/celebrate/star）已接入并通过 7/7 校验。
- **质量**：EditMode **141/141**；模拟测试机 20 分钟 × 3 种子全绿；
  讲话期间精灵语音播放数 = **0**。
- **交付**：v9.8 APK 已构建并安装到 Quest 3（`Builds/ARAttention-device.apk`），
  **seg 已删除；BOUNDARYLESS_APP full + custom main manifest，待真机复核**。

### 🔜 当前工作

1. **v10 真机复核（最优先）**：
   - **主流程**：首寻游走 → 找到老师（放锚点、不铺路）→ 看老师累计星 →
     移开视线精灵进视野指路 + 星路 → 回看立即收路 → 集满奖励 → 下一轮；
     重点确认“看老师时星路绝不出现、移开视线才出现、回看后 ≤0.3s 关闭”；
   - **seg 已删除**：场景里不再有 SelfieSegRunner/高亮/小窗，主画面不应再有蓝色污渍；
   - **游戏空间**：BOUNDARYLESS_APP（full）+ Use Custom Main Manifest=1；contextual
     `shouldBoundaryVisibilityBeSuppressed` 已关闭（两条路互斥，同时开会打架）。
     启动日志 `[XR] TrackingOrigin=... boundarySuppressed(desired/actual)=...`。
     真机系统日志之前显示 `numGuardians: 0`，系统自动 Stationary 小圈；
     若仍弹边界，是 OS 未接受 BOUNDARYLESS_APP，改用 Travel Mode / 关闭 Physical space features /
     手动创建房间级 Guardian；
   - **星路/锚点（v10）**：只在"看过老师后移开视线"的指路状态显示；
     老师锚点第一次真正看向老师时放下（看到时平滑跟随、丢失时冻结），不再超时清除；
     孩子看回来立即收路；正在看老师时绝不显示星路。参数见
     `ExperienceSettings` 的"注意力主流程"段。
2. **`Listen`（扶耳倾听）动作**：需求已交建模师
   （`docs/specs/2026-10-24-listen-animation-requirements.md`），
   等 FBX 到位后接 Animator；当前先用 `IdleFly + Curious` 回退。
3. **表情/动画素材升级**：后续替换更精美的表情与动作素材，程序侧只换资源、不改逻辑。
4. **真机数据回归（v10 口径）**：对比课堂日志，确认
   ① 看老师时星路绝不出现；② 移开视线后才出现星路；③ 看回来 ≤0.3s 收路；
   ④ 讲话期间精灵语音播放数为 0。
5. **语音清单确认（v10）**：`docs/specs/2026-10-25-voice-cue-map.md` 已标注新口径——
   首寻每 4s 轮播、第一次找到老师一句、移开视线只播一次「找老师」、集满一句奖励；
   待产品确认台词/语速与首寻语音是否提高优先级。
6. **seg 功能（已删除）**：用户确认不可用，运行时不再挂载；高亮/小窗/预览资源已删除。
   模型脚本/onnx 保留但未启用，后续若换更好的分割模型再考虑。

### 📌 待办

- 庆典专属语音真人录音（放 `Assets/Resources/Voice/voice_celebrate.mp3` 即生效）。
- 结题材料中的演示视频与截图更新为 v10 效果。

---

## 文档导航

| 想去哪 | 看这个 |
|---|---|
| 全部文档索引 | `docs/README.md` |
| 设计文档（为什么这样做） | `docs/specs/2026-10-07-ar-attention-design.md` |
| 建模与贴图清单 | `docs/assets-index.md` |
| 测试视频/真机检查指南 | `docs/test-video-guide.md` |
| Git 使用指南（组员必读） | `docs/git-guide.md` |
| 模型交付要求（给美术） | `docs/specs/2026-10-10-ip-3d-model-requirements.md` |
| 扶耳倾听动作需求（给美术） | `docs/specs/2026-10-24-listen-animation-requirements.md` |
| 精灵语音清单（按状态） | `docs/specs/2026-10-25-voice-cue-map.md` |
| 动效素材需求（给美术） | `docs/specs/2026-10-20-flipbook-effects-requirements.md` |
| 2026-10-24 真机问题与修复计划 | `docs/specs/2026-10-24-attention-guide-issues.md`、`docs/plans/2026-10-24-attention-guide-fixes.md` |
| 里程碑计划与实测记录 | `docs/plans/`、`docs/measurements/` |

# 精灵语音清单（按交互状态）· 2026-10-25

> 用途：给产品/内容确认“每个状态下小精灵会说什么”。
> 代码入口：`Assets/Scripts/Core/M3DebugDriver.cs`、`Assets/Scripts/Audio/VoiceCuePlayer.cs`
> 音频目录：`Assets/Resources/Voice/`
> 当前版本：v9.1（2026-10-25）

## 规则

- 优先级：**Critical > Guide > Ambient**。
  - Critical：可打断低优先级；
  - Guide：同级/低级排队（最多 1 条）；
  - Ambient：忙时直接丢弃。
- 对话门控：外界（老师/旁人/孩子）讲话时，精灵**所有语音都不播**；
  - Critical/Guide 最多挂起 1 条，讲话结束 + 静默 0.5s 后，若挂起不超过 2s 则补播；
  - Ambient 直接丢弃。
- 非语音音效（星星叮、流星雨、引导出现音）不受语音门控，但外界讲话时音量降到 **20%**。

## 1. 首寻（第一次找到老师前）

每 4 秒轮播一句（Ambient）：

| 语音 | 文件 | 台词 |
|---|---|---|
| SearchStart | `voice_search_start.mp3` | 老师在哪里呀？我们一起找一找！ |
| SearchLook | `voice_search_look.mp3` | 看看这边～ |
| SearchHere | `voice_search_here.mp3` | 我在这里呀～ |
| SearchAgain | `voice_search_again.mp3` | 再找找看～ |
| SearchWhere | `voice_search_where.mp3` | 咦，老师在哪儿呢？ |

首次检测到老师（Critical）：

| 语音 | 文件 | 台词 |
|---|---|---|
| SearchFound | `voice_search_found.mp3` | 找到老师啦！ |

## 2. 没看老师（引导）

> v9.1 起，状态机的 `GuideAppear`/`Find` 自动出场句已删除；
> 引导语统一由“引导导演”三段式负责。

| 阶段 / 条件 | 语音 | 文件 | 台词 | 优先级 |
|---|---|---|---|---|
| 疑惑（有星星） | StarChase | `voice_star_chase.mp3` | 星星飞走啦，我们去追星星吧！ | Guide |
| 疑惑（无星星） | Confused | `voice_confused.mp3` | 老师去哪里了呢？我来找找吧。 | Guide |
| 发现（有星星） | StarLead | `voice_star_lead.mp3` | 看，星星往那边去了～ | Guide |
| 发现（无星星） | Found | `voice_found.mp3` | 找到老师了！跟我来～ | Guide |
| 带领·第 1 轮 | Lead1 | `voice_lead1.mp3` | 小朋友，跟我来～ | Guide |
| 带领·第 2 轮 | Lead2 | `voice_lead2.mp3` | 老师在这边！跟我来！ | Guide |
| 带领·第 3 轮 | Lead3 | `voice_lead3.mp3` | 看！老师在那边！ | Guide |
| 带领（有星星，所有轮次） | StarLead | `voice_star_lead.mp3` | 看，星星往那边去了～ | Guide |
| 跟随成功 | Success | `voice_success.mp3` | 找到老师了，真棒！ | Critical |
| 三轮后静默 | Rest | `voice_rest.mp3` | 我们先休息一下，等老师来～ | Ambient |

- 重复节拍：**仅 Leading 阶段**；第 1 轮 5s，第 2/3 轮 3.5s。
- `hasStars`（当前星环 ≥1 颗）时，发现/带领都使用追星星话术（StarLead）。
  → 这意味着 Lead1/2/3 只在“没有星星”时才会播。

## 3. 看着老师（陪伴/倾听）

| 条件 | 语音 | 文件 | 台词 | 优先级 |
|---|---|---|---|---|
| 讲话结束 + 注视已达成 + 仍在看 | Feedback | `voice_feedback.mp3` | 太棒啦 | Critical |
| 长讲话中每 12s 探头（**当前不会触发**） | Peek | `voice_peek.mp3` | 我在这儿 | Ambient |

> v9 起注视期精灵是 `Attentive`/`Listening`，不再是 `Hidden`；
> 探头的 `_ipAnim == Hidden` 条件不再满足，所以 Peek 目前是死路径。

## 4. 星星 / 流星

| 条件 | 语音 | 文件 | 台词 | 优先级 |
|---|---|---|---|---|
| 第一颗星落成 | StarFirst | `voice_star_first.mp3` | 看！第一颗星星落到老师身边了～ | Ambient |
| 集满 8 星 | MeteorShower | `voice_meteor_shower.mp3` | 太棒了！星星雨送给你！ | Critical |

## 5. 未使用条目

| 语音 | 文件 | 台词 | 状态 |
|---|---|---|---|
| Start | `voice_start.mp3` | 我们一起来找老师吧！ | 无调用（开场吸引已废弃） |
| Hum | `voice_hum.mp3` | 我们一起等老师呀～ | 无调用 |
| Celebrate | 文件不存在 | 太棒了！你一直看着老师的眼睛，真了不起！ | 无调用；当前庆典用 MeteorShower |

## 6. 待确认

1. `SearchStart` 是 Ambient：老师和旁人正在讲话时会被门控丢掉；
   10/24 日志里首句被吞，第一句实际听到的是 4.59s 的“看看这边～”。
   是否把首寻语音改为 Guide 优先级（或首寻期间不做对话门控）？
2. 有星星时所有轮次都只说 `StarLead`，Lead1/2/3 不再出现。
   是否保留轮次强化（需要新增 `StarLead1/2/3` 或交替使用）？
3. `Peek` 是否要恢复（在 `Attentive` 且老师讲话时探头）？还是删除该条目？
4. `Start`/`Hum`/`Celebrate` 是否删除或重新启用？
5. 语速：最早 4 条（guide/find/feedback/peek）是 -8% 语速，其余为默认；
   如需统一，可重新生成。

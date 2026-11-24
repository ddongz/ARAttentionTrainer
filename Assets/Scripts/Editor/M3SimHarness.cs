// Assets/Scripts/Editor/M3SimHarness.cs
// 模拟测试机 · 运行器 + 不变量断言（2026-11-24 注意力流程重构版）。
// 用法：-executeMethod M3SimHarness.Run
//
// 断言（每个种子）：
//   I1 稳定性：全程无异常
//   I2 精灵不消失：出现后 alpha ≥ 0.18
//   I3 首寻：只开始一次；在第一次稳定检测老师后结束；结束后不再有搜索语音
//   I4 第一次找到老师：只发一次 TeacherFound；此后老师锚点一直存在（离场也不清）
//   I5 星路门控：第一次"看过老师后移开"之前绝不显示；重新看老师后 ≤1s 隐藏
//   I6 指路：每次 GuideBack 都有路径打开；找老师语音确实播放（门控期间延后但不丢弃）
//   I7 星星：只增不减；集满触发奖励；至少完成多轮；假脸窗口内不落星
//   I8 讲话门控：外界讲话期间实际播放语音数 = 0
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

public static class M3SimHarness
{
    public static void Run()
    {
        int[] seeds = { 7, 21, 99 };
        int totalViol = 0;
        foreach (int seed in seeds)
        {
            var v = RunOne(seed);
            totalViol += v.Count;
            Debug.Log($"[SIM] 种子 {seed}：{(v.Count == 0 ? "✅ 通过" : $"❌ {v.Count} 项违规")}");
            foreach (var line in v.GetRange(0, Math.Min(v.Count, 15)))
                Debug.LogError($"[SIM][seed={seed}] " + line);
        }
        Debug.Log(totalViol == 0
            ? $"[SIM] ✅✅ 全部 {seeds.Length} 个种子通过——注意力流程达标"
            : $"[SIM] ❌ 共 {totalViol} 项违规");
        EditorApplication.Exit(totalViol == 0 ? 0 : 2);
    }

    static List<string> RunOne(int seed)
    {
        var violations = new List<string>();
        var go = new GameObject("SimDriver");
        var driver = go.AddComponent<M3DebugDriver>();
        driver.simulateGazeWithKeyboard = false;
        driver.useMicVad = false;
        driver.settings = AssetDatabase.LoadAssetAtPath<ExperienceSettings>("Assets/Settings/ExperienceSettings.asset");
        var scenario = new M3SimScenario(seed: seed);
        driver.sim = scenario;
        driver.InitCore();

        int cntSearchStart = 0, cntSearchEnd = 0, cntSearchVoice = 0;
        int cntTeacherFound = 0, cntGuideBack = 0, cntGuideReturn = 0;
        int cntPathShow = 0, cntPathHide = 0;
        int cntVoicePlayed = 0, cntVoiceBlocked = 0, cntFind = 0;
        int cntDrop = 0, cntShower = 0;
        int playedDuringSpeech = 0;
        int searchVoiceAfterEnd = 0;
        float searchEndAt = -1f;
        float firstTeacherFoundAt = -1f;
        float firstPathShowAt = -1f;
        var showerTimes = new List<float>();
        bool teacherFound = false;

        driver.OnSignal += (kind, detail) =>
        {
            float now = driver.NowProbe;
            switch (kind + ":" + detail)
            {
                case "Search:Start": cntSearchStart++; break;
                case "Search:End":
                    cntSearchEnd++;
                    if (searchEndAt < 0f) searchEndAt = now;
                    break;
                case "Search:Voice":
                    cntSearchVoice++;
                    if (searchEndAt >= 0f) searchVoiceAfterEnd++;
                    break;
                case "Teacher:Found":
                    cntTeacherFound++;
                    teacherFound = true;
                    if (firstTeacherFoundAt < 0f) firstTeacherFoundAt = now;
                    break;
                case "Guide:Back": cntGuideBack++; break;
                case "Guide:Return": cntGuideReturn++; break;
                case "Path:Show":
                    cntPathShow++;
                    if (firstPathShowAt < 0f) firstPathShowAt = now;
                    break;
                case "Path:Hide": cntPathHide++; break;
                case "Voice:Played":
                    cntVoicePlayed++;
                    if (driver.SpeakingProbe) playedDuringSpeech++;
                    break;
                case "Voice:Blocked": cntVoiceBlocked++; break;
                case "Voice:Find": cntFind++; break;
                case "Star:Drop": cntDrop++; break;
                case "Star:Shower":
                    cntShower++;
                    showerTimes.Add(now);
                    break;
            }
        };

        float dt = 1f / 30f;
        int ticks = Mathf.CeilToInt((scenario.TotalDuration + 3f) / dt);
        int invisibleStreak = 0;
        float pathWhileLookingStreak = 0f;
        int attentiveWatchingTicks = 0;
        int fakeDropsBefore = -1;
        bool anchorLossReported = false;
        bool earlyPathReported = false;
        bool pathWhileLookingReported = false;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        for (int i = 0; i < ticks; i++)
        {
            try { driver.Tick(dt); }
            catch (Exception e)
            {
                violations.Add($"[异常] t={driver.NowProbe:F1}s {e.GetType().Name}: {e.Message}");
                break;
            }
            float now = driver.NowProbe;

            // I2 不消失
            if (now > 4f && driver.IpAnimProbe != 0 && driver.IpAlphaProbe < 0.18f)
            {
                if (invisibleStreak++ == 0)
                    violations.Add($"[隐形] t={now:F1}s anim={(IPWorldView.IPAnimState)driver.IpAnimProbe} " +
                                   $"alpha={driver.IpAlphaProbe:F2}");
            }
            else invisibleStreak = 0;

            // I4 锚点只在第一次找到后存在，永不清除
            if (teacherFound && !driver.TeacherAnchorProbe && !anchorLossReported)
            {
                anchorLossReported = true;
                violations.Add($"[锚点丢失] t={now:F1}s 第一次找到老师后锚点不应清除");
            }

            // I5 星路与注视互斥：重新看老师后最多允许 1s 过渡（returnConfirm=0.3s）
            if (driver.PathVisibleProbe && driver.LookingProbe) pathWhileLookingStreak += dt;
            else pathWhileLookingStreak = 0f;
            if (pathWhileLookingStreak > 1.0f && !pathWhileLookingReported)
            {
                pathWhileLookingReported = true;
                violations.Add($"[星路] t={now:F1}s 孩子正在看老师，星路却持续 >1s");
            }

            // I5b 第一次看过老师并移开之前，星路绝不能出现
            if (!earlyPathReported && firstPathShowAt > 0f && scenario.FirstLookAwaySec > 0f &&
                firstPathShowAt < scenario.FirstLookAwaySec - 0.3f)
            {
                earlyPathReported = true;
                violations.Add($"[星路过早] 第一次显示 {firstPathShowAt:F1}s < 首次移开 {scenario.FirstLookAwaySec:F1}s");
            }

            // I7 假脸窗口：窗口开始 2s 后不应再落星（1.5s 人脸新鲜度余量）
            if (scenario.FakeFaceStartSec > 0f && fakeDropsBefore < 0 &&
                now >= scenario.FakeFaceStartSec + 2f)
                fakeDropsBefore = cntDrop;
            if (fakeDropsBefore >= 0 && now < scenario.FakeFaceEndSec && cntDrop != fakeDropsBefore)
            {
                violations.Add($"[假脸] t={now:F1}s 假脸窗口内落星 {cntDrop - fakeDropsBefore} 颗");
                fakeDropsBefore = cntDrop; // 只报一次
            }

            // I9 注视期视觉状态：Watching + 在看 → Attentive/Listening
            if (driver.State == AttentionState.Watching && driver.LookingProbe &&
                (driver.IpAnimProbe == 7 || driver.IpAnimProbe == 8))
                attentiveWatchingTicks++;
        }

        // ── 汇总断言 ──
        if (cntSearchStart != 1) violations.Add($"[首寻] Search:Start={cntSearchStart}（应恰为 1）");
        if (cntSearchEnd != 1) violations.Add($"[首寻] Search:End={cntSearchEnd}（应恰为 1）");
        if (cntSearchVoice < 2) violations.Add($"[首寻] Search:Voice={cntSearchVoice} < 2（无老师时应有语音吸引）");
        if (searchVoiceAfterEnd > 0) violations.Add($"[首寻] 搜索结束后仍播了 {searchVoiceAfterEnd} 句搜索语音");

        if (cntTeacherFound != 1) violations.Add($"[老师] Teacher:Found={cntTeacherFound}（应恰为 1）");
        if (scenario.FirstTeacherSec > 0f && firstTeacherFoundAt > 0f)
        {
            float delay = firstTeacherFoundAt - scenario.FirstTeacherSec;
            if (delay < 0.3f || delay > 1.2f)
                violations.Add($"[老师确认] 首次检测 {scenario.FirstTeacherSec:F1}s → 确认 {firstTeacherFoundAt:F1}s（延迟 {delay:F2}s，应 0.3~1.2s）");
        }

        if (cntGuideBack < 10) violations.Add($"[指路] Guide:Back={cntGuideBack} < 10");
        if (cntGuideReturn < 8) violations.Add($"[指路] Guide:Return={cntGuideReturn} < 8");
        if (cntPathShow < 8) violations.Add($"[星路] Path:Show={cntPathShow} < 8");
        if (cntPathShow != cntGuideBack)
            violations.Add($"[星路] Path:Show={cntPathShow} 与 Guide:Back={cntGuideBack} 应一一对应");
        if (cntFind < 5) violations.Add($"[语音] 找老师语音 Voice:Find={cntFind} < 5");
        if (cntVoicePlayed < 5) violations.Add($"[语音] Voice:Played={cntVoicePlayed} < 5");

        if (cntDrop < 16) violations.Add($"[星星] Star:Drop={cntDrop} < 16");
        if (cntShower < 2) violations.Add($"[奖励] Star:Shower={cntShower} < 2");
        if (playedDuringSpeech > 0)
            violations.Add($"[门控] 外界讲话期间实际播放了 {playedDuringSpeech} 条语音（应全部延后）");
        if (attentiveWatchingTicks < 100)
            violations.Add($"[注视状态] 看老师时 Attentive/Listening 仅 {attentiveWatchingTicks} tick");

        float minutes = scenario.TotalDuration / 60f;
        Debug.Log($"[SIM] ⏱ {minutes:F1} 分钟剧本 / {ticks} tick / 实跑 {sw.Elapsed.TotalSeconds:F1}s");
        Debug.Log($"[SIM] 首寻 Start={cntSearchStart} Voice={cntSearchVoice} End={cntSearchEnd} " +
                  $"老师确认={firstTeacherFoundAt:F1}s 搜索结束={searchEndAt:F1}s");
        Debug.Log($"[SIM] 指路 Back={cntGuideBack} Return={cntGuideReturn} 路 Show={cntPathShow} Hide={cntPathHide} " +
                  $"找老师语音={cntFind} 首次显示路={firstPathShowAt:F1}s（首次移开={scenario.FirstLookAwaySec:F1}s）");
        Debug.Log($"[SIM] 语音 Played={cntVoicePlayed} Blocked={cntVoiceBlocked} 讲话中播放={playedDuringSpeech}");
        Debug.Log($"[SIM] 星星 Drop={cntDrop} Shower={cntShower} " +
                  $"[{string.Join(", ", showerTimes.ConvertAll(t => t.ToString("F1")))}] 注视态tick={attentiveWatchingTicks}");

        UnityEngine.Object.DestroyImmediate(go);
        return violations;
    }
}

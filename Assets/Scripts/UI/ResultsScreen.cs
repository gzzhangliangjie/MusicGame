using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace MusicGame.UI
{
    /// <summary>
    /// 结算界面：准确率 / 最大连击 / 得分 / 评级（S≥95% A≥90% B≥80% C 其他）。
    /// 破纪录时显示 NEW RECORD 金色横幅（弹出 + 摇摆），并给出与历史最佳的对比行。
    /// 全部 uGUI 代码创建；由 GameFlow 调用 Show 并订阅三个跳转事件。
    /// </summary>
    public class ResultsScreen : MonoBehaviour
    {
        public event Action RetryRequested;
        public event Action SongSelectRequested;
        public event Action MenuRequested;

        RectTransform panel;
        Text songText, rankText, accText, comboText, scoreText, fcText, recordText;
        Image recordBanner, rankArt;
        Text[] countTexts = new Text[4];
        RectTransform[] countRows = new RectTransform[4];
        Vector2[] countRowBasePos = new Vector2[4];
        Coroutine bannerAnim, rankPopAnim, countsPopAnim;
        readonly Dictionary<char, Sprite> rankSpriteCache = new Dictionary<char, Sprite>();
        static readonly Color[] CountColors =
        {
            new Color(1f, 0.83f, 0.30f), new Color(0.49f, 0.88f, 0.49f),
            new Color(0.40f, 0.72f, 1f), new Color(1f, 0.42f, 0.42f),
        };

        /// <summary>在本物体下构建结算面板（本物体需已挂在 Canvas 下）。</summary>
        public void Setup()
        {
            panel = UiKit.Panel(transform, "ResultsPanel", UiKit.Theme.Bg).rectTransform;

            songText = UiKit.Label(panel, "song", "", 26, TextAnchor.MiddleCenter, UiKit.Theme.Dim, true);
            UiKit.Stretch(songText.rectTransform, new Vector2(0f, 0.88f), new Vector2(1f, 0.955f));

            // 结算标题美术字（思源黑体金渐变，Resources/Ui/title_settle；缺图回退文字标题）
            var titleTex = Resources.Load<Texture2D>("Ui/title_settle");
            if (titleTex != null)
            {
                var titleGo = UiKit.Node(panel, "titleArt");
                var titleRt = titleGo;
                titleRt.anchorMin = new Vector2(0.5f, 0.5f);
                titleRt.anchorMax = new Vector2(0.5f, 0.5f);
                titleRt.anchoredPosition = new Vector2(0f, 367f);
                titleRt.sizeDelta = new Vector2(176f, 86f);
                var titleImg = titleGo.gameObject.AddComponent<Image>();
                titleImg.sprite = Sprite.Create(titleTex, new Rect(0f, 0f, titleTex.width, titleTex.height), new Vector2(0.5f, 0.5f), 100f);
                titleImg.raycastTarget = false;
                titleImg.preserveAspect = true;
            }
            else
            {
                var title = UiKit.Label(panel, "title", "结 算", 42, TextAnchor.MiddleCenter, Color.white);
                title.font = UiKit.HeavyFont; // 中文大标题统一思源黑体 Heavy
                UiKit.Stretch(title.rectTransform, new Vector2(0f, 0.80f), new Vector2(1f, 0.88f));
            }

            rankText = UiKit.Label(panel, "rank", "S", 190, TextAnchor.MiddleCenter, new Color(0.98f, 0.80f, 0.25f), true);
            UiKit.Stretch(rankText.rectTransform, new Vector2(0f, 0.34f), new Vector2(1f, 0.80f));

            // 评级美术字（判定艺术字同语言，Resources/Ui/Rank 缺图时回退上面的文字评级），
            // Show 时按等级换贴图 + OutBack 冲击弹出
            var rankGo = new GameObject("RankArt", typeof(RectTransform));
            rankGo.transform.SetParent(panel, false);
            var rankRt = (RectTransform)rankGo.transform;
            rankRt.anchorMin = new Vector2(0.5f, 0.5f);
            rankRt.anchorMax = new Vector2(0.5f, 0.5f);
            rankRt.anchoredPosition = new Vector2(60f, 75f);
            rankRt.sizeDelta = new Vector2(420f, 420f);
            rankArt = rankGo.AddComponent<Image>();
            rankArt.raycastTarget = false;
            rankArt.preserveAspect = true;
            rankGo.SetActive(false);

            // 判定统计（评级左侧）：美术字图标 + 次数，行级错峰弹入
            string[] kinds = { "perfect", "good", "bad", "miss" };
            for (int i = 0; i < kinds.Length; i++)
            {
                var row = UiKit.Node(panel, "countRow" + i);
                var rowRt = row; // UiKit.Node 返回 RectTransform
                rowRt.anchorMin = new Vector2(0.5f, 0.5f);
                rowRt.anchorMax = new Vector2(0.5f, 0.5f);
                rowRt.anchoredPosition = new Vector2(-390f, 150f - 100f * i);
                rowRt.sizeDelta = new Vector2(360f, 84f);
                countRows[i] = rowRt;
                countRowBasePos[i] = rowRt.anchoredPosition;
                row.gameObject.AddComponent<CanvasGroup>();

                // 美术字图标与计数文字必须是行的子节点——把行根本身重新 Stretch 会撑爆布局
                var artGo = UiKit.Node(row, "art");
                var art = artGo.gameObject.AddComponent<Image>();
                art.raycastTarget = false;
                art.preserveAspect = true;
                var artTex = Resources.Load<Texture2D>("Judgement/" + kinds[i]);
                if (artTex != null)
                    art.sprite = Sprite.Create(artTex, new Rect(0f, 0f, artTex.width, artTex.height), new Vector2(0.5f, 0.5f), 100f);
                UiKit.Stretch(art.rectTransform, new Vector2(0f, 0.12f), new Vector2(0.56f, 0.88f));

                var cnt = UiKit.Label(row, "n", "", 36, TextAnchor.MiddleLeft, CountColors[i], true);
                UiKit.Stretch(cnt.rectTransform, new Vector2(0.62f, 0f), new Vector2(1f, 1f));
                countTexts[i] = cnt;
            }

            fcText = UiKit.Label(panel, "fc", "FULL COMBO !", 34, TextAnchor.MiddleCenter, UiKit.Theme.Accent);
            UiKit.Stretch(fcText.rectTransform, new Vector2(0f, 0.27f), new Vector2(1f, 0.34f));

            accText = UiKit.Label(panel, "acc", "准确率  --", 34, TextAnchor.MiddleCenter, Color.white);
            UiKit.Stretch(accText.rectTransform, new Vector2(0f, 0.185f), new Vector2(1f, 0.27f));

            comboText = UiKit.Label(panel, "combo", "最大连击  0", 34, TextAnchor.MiddleCenter, Color.white);
            UiKit.Stretch(comboText.rectTransform, new Vector2(0f, 0.115f), new Vector2(1f, 0.185f));

            scoreText = UiKit.Label(panel, "score", "总分  0", 40, TextAnchor.MiddleCenter, new Color(1f, 0.90f, 0.55f), true);
            UiKit.Stretch(scoreText.rectTransform, new Vector2(0f, 0.038f), new Vector2(1f, 0.115f));

            // 历史纪录信息行（对比/首玩提示），贴着按钮行下方
            recordText = UiKit.Label(panel, "record", "", 24, TextAnchor.MiddleCenter, new Color(1f, 0.85f, 0.45f), true);
            UiKit.Stretch(recordText.rectTransform, new Vector2(0f, 0.0f), new Vector2(1f, 0.042f));

            // NEW RECORD 金色艺术字横幅（默认隐藏，破纪录时弹出并摇摆）
            var bannerGo = new GameObject("RecordBanner", typeof(RectTransform));
            bannerGo.transform.SetParent(panel, false);
            var brt = (RectTransform)bannerGo.transform;
            brt.anchorMin = new Vector2(0.5f, 0.5f);
            brt.anchorMax = new Vector2(0.5f, 0.5f);
            brt.anchoredPosition = new Vector2(0f, -140f);
            brt.sizeDelta = new Vector2(520f, 124f);
            var tex = Resources.Load<Texture2D>("Ui/newrecord");
            if (tex != null)
            {
                var sprite = Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
                sprite.name = "NewRecord";
                recordBanner = bannerGo.AddComponent<Image>();
                recordBanner.sprite = sprite;
                recordBanner.raycastTarget = false;
            }
            bannerGo.SetActive(false);

            UiKit.ButtonRect(panel, "btnRetry", "再来一次", new Vector2(-520f, -450f), new Vector2(340f, 80f), OnRetry, UiKit.Theme.Primary, 30);
            UiKit.ButtonRect(panel, "btnSelect", "返回选曲", new Vector2(0f, -450f), new Vector2(340f, 80f), OnSelect, UiKit.Theme.Secondary, 30);
            UiKit.ButtonRect(panel, "btnMenu", "主菜单", new Vector2(520f, -450f), new Vector2(340f, 80f), OnMenu, UiKit.Theme.PanelBg, 30);

            panel.gameObject.SetActive(false);
        }

        /// <summary>填充并显示结算。accuracyPercent 为 -1 表示无数据（ScoreSystem 不可达）。
        /// recordLine：历史纪录对比/提示文案；newRecord：是否破纪录（弹横幅）。</summary>
        public void Show(string songTitle, string difficultyLabel,
            double accuracyPercent, int maxCombo, double score, int totalNotes,
            int perfectCount, int goodCount, int badCount, int missCount,
            string recordLine, bool newRecord)
        {
            if (panel == null) return;
            songText.text = songTitle + " · " + difficultyLabel;

            char rank;
            Color color;
            if (accuracyPercent < 0.0) { rank = '-'; color = UiKit.Theme.Dim; }
            else if (accuracyPercent >= 95.0) { rank = 'S'; color = new Color(0.98f, 0.80f, 0.25f); }
            else if (accuracyPercent >= 90.0) { rank = 'A'; color = new Color(0.35f, 0.85f, 0.45f); }
            else if (accuracyPercent >= 80.0) { rank = 'B'; color = new Color(0.25f, 0.70f, 0.95f); }
            else { rank = 'C'; color = UiKit.Theme.Dim; }

            // 评级：优先美术字冲击弹出，缺图回退文字评级
            if (rankPopAnim != null) { StopCoroutine(rankPopAnim); rankPopAnim = null; }
            bool hasRankArt = rank != '-' && Resources.Load<Texture2D>("Ui/Rank/rank_" + char.ToLowerInvariant(rank)) != null;
            rankArt.gameObject.SetActive(hasRankArt);
            rankText.gameObject.SetActive(!hasRankArt);
            if (hasRankArt)
            {
                if (!rankSpriteCache.TryGetValue(rank, out var sp))
                {
                    var rt2 = Resources.Load<Texture2D>("Ui/Rank/rank_" + char.ToLowerInvariant(rank));
                    sp = Sprite.Create(rt2, new Rect(0f, 0f, rt2.width, rt2.height), new Vector2(0.5f, 0.5f), 100f);
                    sp.name = "Rank" + rank;
                    rankSpriteCache[rank] = sp;
                }
                rankArt.sprite = sp;
                rankPopAnim = StartCoroutine(RankPopRoutine());
            }
            else
            {
                rankText.text = rank.ToString();
                rankText.color = color;
            }

            accText.text = accuracyPercent < 0.0 ? "准确率  --" : "准确率  " + accuracyPercent.ToString("F2") + "%";
            comboText.text = "最大连击  " + maxCombo;
            scoreText.text = "总分  " + ((long)score).ToString("N0");
            fcText.gameObject.SetActive(totalNotes > 0 && maxCombo >= totalNotes); // totalNotes 为判定数口径（hold=2，由 GameFlow 传入）

            int[] vals = { perfectCount, goodCount, badCount, missCount };
            for (int i = 0; i < countTexts.Length; i++) countTexts[i].text = "× " + vals[i];
            if (countsPopAnim != null) StopCoroutine(countsPopAnim);
            countsPopAnim = StartCoroutine(CountsPopRoutine());

            recordText.text = recordLine ?? "";

            if (bannerAnim != null) { StopCoroutine(bannerAnim); bannerAnim = null; }
            if (recordBanner != null)
            {
                recordBanner.transform.gameObject.SetActive(newRecord && accuracyPercent >= 0.0);
                if (newRecord && accuracyPercent >= 0.0)
                    bannerAnim = StartCoroutine(BannerRoutine());
            }

            panel.gameObject.SetActive(true);
        }

        /// <summary>横幅冲击入场：放大砸落(2.6→0.95)+快速显形，回弹站稳后持续轻微摇摆+呼吸缩放。</summary>
        IEnumerator BannerRoutine()
        {
            var rt = recordBanner.rectTransform;
            var cg = recordBanner.GetComponent<CanvasGroup>();
            if (cg == null) cg = recordBanner.gameObject.AddComponent<CanvasGroup>();
            // 砸落
            float t = 0f;
            while (t < 0.14f)
            {
                t += Time.unscaledDeltaTime;
                float k = Mathf.Clamp01(t / 0.14f);
                cg.alpha = k;
                float s = Mathf.Lerp(2.6f, 0.95f, OutCubic(k));
                rt.localScale = new Vector3(s, s, 1f);
                yield return null;
            }
            cg.alpha = 1f;
            // 回弹站稳 + 摇摆
            t = 0f;
            while (t < 0.3f)
            {
                t += Time.unscaledDeltaTime;
                float k = Mathf.Clamp01(t / 0.3f);
                float wobble = Mathf.Cos(k * Mathf.PI * 3f) * (1f - k);
                float s = 0.95f + 0.09f * wobble;
                rt.localScale = new Vector3(s, s, 1f);
                rt.localRotation = Quaternion.Euler(0f, 0f, Mathf.Sin(k * Mathf.PI * 5f) * 4f * (1f - k));
                yield return null;
            }
            rt.localRotation = Quaternion.identity;
            // 待机：轻微摇摆+呼吸缩放，直到面板隐藏
            while (panel != null && panel.gameObject.activeSelf)
            {
                t += Time.unscaledDeltaTime;
                float s = 1f + Mathf.Sin(t * 3.2f) * 0.03f;
                rt.localScale = new Vector3(s, s, 1f);
                rt.localRotation = Quaternion.Euler(0f, 0f, Mathf.Sin(t * 2.1f) * 2.5f);
                yield return null;
            }
        }

        /// <summary>评级美术字：OutBack 冲击弹出（0.2→1 过冲）+ 落定前微摆。</summary>
        IEnumerator RankPopRoutine()
        {
            var rt = rankArt.rectTransform;
            float t = 0f;
            const float Dur = 0.42f;
            while (t < Dur)
            {
                t += Time.unscaledDeltaTime;
                float k = Mathf.Clamp01(t / Dur);
                float s = Mathf.Lerp(0.2f, 1f, OutBack(k));
                rt.localScale = new Vector3(s, s, 1f);
                rt.localRotation = Quaternion.Euler(0f, 0f, Mathf.Sin(k * Mathf.PI * 2f) * 5f * (1f - k));
                yield return null;
            }
            rt.localScale = Vector3.one;
            rt.localRotation = Quaternion.identity;
            rankPopAnim = null;
        }

        /// <summary>判定统计行：自左错峰滑入弹现（美术字 + 次数）。</summary>
        IEnumerator CountsPopRoutine()
        {
            const float Dur = 0.3f;
            for (int i = 0; i < countRows.Length; i++)
            {
                if (countRows[i] == null) continue;
                var cg = countRows[i].GetComponent<CanvasGroup>();
                if (cg == null) cg = countRows[i].gameObject.AddComponent<CanvasGroup>();
                Vector2 target = countRowBasePos[i];
                float t = 0f;
                while (t < Dur)
                {
                    t += Time.unscaledDeltaTime;
                    float k = Mathf.Clamp01(t / Dur);
                    float e = OutCubic(k);
                    cg.alpha = e;
                    countRows[i].anchoredPosition = target + new Vector2(-46f * (1f - e), 0f);
                    yield return null;
                }
                cg.alpha = 1f;
                countRows[i].anchoredPosition = target;
            }
            countsPopAnim = null;
        }

        private static float OutBack(float t)
        {
            const float c1 = 1.70158f;
            float k = t - 1f;
            return 1f + (c1 + 1f) * k * k * k + c1 * k * k;
        }

        private static float OutCubic(float t) { float k = 1f - t; return 1f - k * k * k; }

        public void Hide()
        {
            if (bannerAnim != null) { StopCoroutine(bannerAnim); bannerAnim = null; }
            if (rankPopAnim != null) { StopCoroutine(rankPopAnim); rankPopAnim = null; }
            if (countsPopAnim != null) { StopCoroutine(countsPopAnim); countsPopAnim = null; }
            if (panel != null) panel.gameObject.SetActive(false);
        }

        void OnRetry() { var h = RetryRequested; if (h != null) h(); }
        void OnSelect() { var h = SongSelectRequested; if (h != null) h(); }
        void OnMenu() { var h = MenuRequested; if (h != null) h(); }
    }
}

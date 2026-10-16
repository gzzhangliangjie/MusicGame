using System;
using UnityEngine;
using UnityEngine.UI;

namespace MusicGame.UI
{
    /// <summary>
    /// 结算界面：准确率 / 最大连击 / 得分 / 评级（S≥95% A≥90% B≥80% C 其他）。
    /// 全部 uGUI 代码创建；由 GameFlow 调用 Show 并订阅三个跳转事件。
    /// </summary>
    public class ResultsScreen : MonoBehaviour
    {
        public event Action RetryRequested;
        public event Action SongSelectRequested;
        public event Action MenuRequested;

        RectTransform panel;
        Text songText, rankText, accText, comboText, scoreText, fcText;

        /// <summary>在本物体下构建结算面板（本物体需已挂在 Canvas 下）。</summary>
        public void Setup()
        {
            panel = UiKit.Panel(transform, "ResultsPanel", UiKit.Theme.Bg).rectTransform;

            songText = UiKit.Label(panel, "song", "", 26, TextAnchor.MiddleCenter, UiKit.Theme.Dim, true);
            UiKit.Stretch(songText.rectTransform, new Vector2(0f, 0.88f), new Vector2(1f, 0.955f));

            var title = UiKit.Label(panel, "title", "结 算", 42, TextAnchor.MiddleCenter, Color.white);
            UiKit.Stretch(title.rectTransform, new Vector2(0f, 0.80f), new Vector2(1f, 0.88f));

            rankText = UiKit.Label(panel, "rank", "S", 190, TextAnchor.MiddleCenter, new Color(0.98f, 0.80f, 0.25f), true);
            UiKit.Stretch(rankText.rectTransform, new Vector2(0f, 0.34f), new Vector2(1f, 0.80f));

            fcText = UiKit.Label(panel, "fc", "FULL COMBO !", 34, TextAnchor.MiddleCenter, UiKit.Theme.Accent);
            UiKit.Stretch(fcText.rectTransform, new Vector2(0f, 0.27f), new Vector2(1f, 0.34f));

            accText = UiKit.Label(panel, "acc", "准确率  --", 34, TextAnchor.MiddleCenter, Color.white);
            UiKit.Stretch(accText.rectTransform, new Vector2(0f, 0.185f), new Vector2(1f, 0.27f));

            comboText = UiKit.Label(panel, "combo", "最大连击  0", 34, TextAnchor.MiddleCenter, Color.white);
            UiKit.Stretch(comboText.rectTransform, new Vector2(0f, 0.115f), new Vector2(1f, 0.185f));

            scoreText = UiKit.Label(panel, "score", "得分  0", 34, TextAnchor.MiddleCenter, Color.white);
            UiKit.Stretch(scoreText.rectTransform, new Vector2(0f, 0.045f), new Vector2(1f, 0.115f));

            UiKit.ButtonRect(panel, "btnRetry", "再来一次", new Vector2(-520f, -450f), new Vector2(340f, 80f), OnRetry, UiKit.Theme.Primary, 30);
            UiKit.ButtonRect(panel, "btnSelect", "返回选曲", new Vector2(0f, -450f), new Vector2(340f, 80f), OnSelect, UiKit.Theme.Secondary, 30);
            UiKit.ButtonRect(panel, "btnMenu", "主菜单", new Vector2(520f, -450f), new Vector2(340f, 80f), OnMenu, UiKit.Theme.PanelBg, 30);

            panel.gameObject.SetActive(false);
        }

        /// <summary>填充并显示结算。accuracyPercent 为 -1 表示无数据（ScoreSystem 不可达）。</summary>
        public void Show(string songTitle, string difficultyLabel,
            double accuracyPercent, int maxCombo, double score, int totalNotes)
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
            rankText.text = rank.ToString();
            rankText.color = color;

            accText.text = accuracyPercent < 0.0 ? "准确率  --" : "准确率  " + accuracyPercent.ToString("F2") + "%";
            comboText.text = "最大连击  " + maxCombo;
            scoreText.text = "得分  " + ((long)score).ToString("N0");
            fcText.gameObject.SetActive(totalNotes > 0 && maxCombo >= totalNotes); // totalNotes 为判定数口径（hold=2，由 GameFlow 传入）

            panel.gameObject.SetActive(true);
        }

        public void Hide()
        {
            if (panel != null) panel.gameObject.SetActive(false);
        }

        void OnRetry() { var h = RetryRequested; if (h != null) h(); }
        void OnSelect() { var h = SongSelectRequested; if (h != null) h(); }
        void OnMenu() { var h = MenuRequested; if (h != null) h(); }
    }
}

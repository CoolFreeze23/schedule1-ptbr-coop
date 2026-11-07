using System.Collections.Generic;
using System.Text;
using Il2CppTMPro;
using UnityEngine;
using UnityEngine.UI;

namespace S1CoopTalk
{
    /// <summary>The on-screen list of what the other players are hearing, plus its history panel.</summary>
    public static class Feed
    {
        public const string NpcColor = "#8FD9FF", PlayerColor = "#B8F28C", PhoneColor = "#FFC36B";
        private const string SystemColor = "#C8C8C8", AlertColor = "#FF7A6B";
        private const int MaxVisible = 4, MaxHistory = 60;
        private const float Width = 470f;
        private const float MinSeconds = 7f, MaxSeconds = 16f, SecondsPerChar = 0.065f, FadeSeconds = 0.8f;

        private sealed class Entry
        {
            public GameObject Root;
            public CanvasGroup Group;
            public float Expires;
        }

        private static readonly List<Entry> Visible = new List<Entry>();
        private static readonly List<string> History = new List<string>();
        private static GameObject _root;
        public static GameObject Root => _root;
        private static RectTransform _list;
        private static GameObject _historyPanel;
        private static TextMeshProUGUI _historyText;

        public static void Speech(string header, string body, string color)
        {
            string text = $"<color={color}><b>{header}</b></color>\n{Text.ForFeed(body)}";
            Add(text, Mathf.Clamp(body.Length * SecondsPerChar, MinSeconds, MaxSeconds));
        }

        public static void System(string message) => Add($"<color={SystemColor}>{message}</color>", MinSeconds);

        public static void Alert(string message)
        {
            Add($"<color={AlertColor}><b>{message}</b></color>", MinSeconds + 3f);
            try
            {
                Il2CppScheduleOne.DevUtilities.Singleton<Il2CppScheduleOne.UI.NotificationsManager>.Instance?.Sound?.Play();
            }
            catch (System.Exception e)
            {
                Mod.Log.Warning("Alert sound failed: " + e.Message);
            }
        }

        private static void Add(string text, float seconds)
        {
            History.Add(text);
            if (History.Count > MaxHistory)
                History.RemoveAt(0);
            RefreshHistory();

            if (!Ensure())
                return;

            while (Visible.Count >= MaxVisible)
                Remove(0);

            GameObject go = new GameObject("S1 Feed Line");
            go.transform.SetParent(_list, false);
            Image bg = go.AddComponent<Image>();
            bg.color = new Color(0f, 0f, 0f, 0.62f);
            bg.raycastTarget = false;
            CanvasGroup group = go.AddComponent<CanvasGroup>();
            group.blocksRaycasts = false;
            group.interactable = false;

            VerticalLayoutGroup pad = go.AddComponent<VerticalLayoutGroup>();
            pad.padding = new RectOffset(14, 14, 8, 8);
            pad.childControlHeight = true;
            pad.childControlWidth = true;
            pad.childForceExpandHeight = false;
            ContentSizeFitter fit = go.AddComponent<ContentSizeFitter>();
            fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            GameObject label = new GameObject("Text");
            label.transform.SetParent(go.transform, false);
            NewLabel(label, 20f).text = text;

            Visible.Add(new Entry { Root = go, Group = group, Expires = Time.unscaledTime + seconds });
        }

        public static TextMeshProUGUI NewLabel(GameObject go, float size)
        {
            TextMeshProUGUI t = go.AddComponent<TextMeshProUGUI>();
            TMP_FontAsset font = GameFont();
            if (font != null)
                t.font = font;
            t.fontSize = size;
            t.color = Color.white;
            t.enableWordWrapping = true;
            t.richText = true;
            t.raycastTarget = false;
            return t;
        }

        private static TMP_FontAsset _font;

        // The same face as the game's own HUD.
        private static TMP_FontAsset GameFont()
        {
            if (_font != null)
                return _font;
            foreach (TMP_FontAsset f in Resources.FindObjectsOfTypeAll<TMP_FontAsset>())
                if (f != null && f.name.StartsWith("OpenSans-SemiBold SDF"))
                    _font = f;
            return _font != null ? _font : TMP_Settings.defaultFontAsset;
        }

        private static void Remove(int index)
        {
            Entry e = Visible[index];
            Visible.RemoveAt(index);
            if (e.Root != null)
                Object.Destroy(e.Root);
        }

        public static void Tick()
        {
            for (int i = Visible.Count - 1; i >= 0; i--)
            {
                Entry e = Visible[i];
                if (e.Root == null)
                {
                    Visible.RemoveAt(i);
                    continue;
                }
                float left = e.Expires - Time.unscaledTime;
                if (left <= 0f)
                    Remove(i);
                else if (left < FadeSeconds)
                    e.Group.alpha = left / FadeSeconds;
            }
        }

        public static bool Ensure()
        {
            if (_root != null)
                return true;

            _root = new GameObject("S1 Co-op Talk");
            Object.DontDestroyOnLoad(_root);
            Canvas canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 150;
            CanvasScaler scaler = _root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 1f;

            GameObject list = new GameObject("List");
            list.transform.SetParent(_root.transform, false);
            _list = list.AddComponent<RectTransform>();
            _list.anchorMin = _list.anchorMax = Vector2.zero;
            _list.pivot = Vector2.zero;
            _list.anchoredPosition = new Vector2(24f, 170f);
            _list.sizeDelta = new Vector2(Width, 0f);
            VerticalLayoutGroup v = list.AddComponent<VerticalLayoutGroup>();
            v.spacing = 6f;
            v.childControlHeight = true;
            v.childControlWidth = true;
            v.childForceExpandHeight = false;
            ContentSizeFitter f = list.AddComponent<ContentSizeFitter>();
            f.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            _historyPanel = new GameObject("History");
            _historyPanel.transform.SetParent(_root.transform, false);
            RectTransform hr = _historyPanel.AddComponent<RectTransform>();
            hr.anchorMin = new Vector2(0.2f, 0.1f);
            hr.anchorMax = new Vector2(0.8f, 0.9f);
            hr.offsetMin = hr.offsetMax = Vector2.zero;
            Image hbg = _historyPanel.AddComponent<Image>();
            hbg.color = new Color(0f, 0f, 0f, 0.86f);
            hbg.raycastTarget = false;
            GameObject ht = new GameObject("Text");
            ht.transform.SetParent(_historyPanel.transform, false);
            RectTransform htr = ht.AddComponent<RectTransform>();
            htr.anchorMin = Vector2.zero;
            htr.anchorMax = Vector2.one;
            htr.offsetMin = new Vector2(24f, 24f);
            htr.offsetMax = new Vector2(-24f, -24f);
            _historyText = NewLabel(ht, 20f);
            _historyText.alignment = TextAlignmentOptions.BottomLeft;
            _historyText.overflowMode = TextOverflowModes.Masking;
            _historyPanel.SetActive(false);
            return true;
        }

        public static void ToggleHistory()
        {
            Ensure();
            _historyPanel.SetActive(!_historyPanel.activeSelf);
            RefreshHistory();
        }

        private static void RefreshHistory()
        {
            if (_historyPanel == null || !_historyPanel.activeSelf)
                return;
            var sb = new StringBuilder();
            sb.Append("<size=120%><b>").Append(string.Format(Text.Ui("history"), Mod.HistoryKeyName)).Append("</b></size>\n\n");
            if (History.Count == 0)
                sb.Append(Text.Ui("history_empty"));
            foreach (string line in History)
                sb.Append(line).Append("\n\n");
            _historyText.text = sb.ToString();
        }
    }
}

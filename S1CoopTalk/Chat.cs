using System;
using System.Collections.Generic;
using System.Globalization;
using Il2CppScheduleOne;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Money;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppTMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace S1CoopTalk
{
    /// <summary>Text chat, sending cash to another player, and the end-of-day scoreboard.</summary>
    public static class Chat
    {
        private const int MaxLength = 200;
        private const float RefundAfter = 15f;

        private sealed class Pending
        {
            public string To;
            public float Amount, Expires;
            public string Id;
        }

        private sealed class Tally
        {
            public int Sales;
            public float Money;
        }

        private static readonly List<Pending> PendingCash = new List<Pending>();
        private static readonly Dictionary<string, Tally> Today = new Dictionary<string, Tally>();
        private static GameObject _box;
        private static TMP_InputField _input;
        private static bool _closeNextFrame;

        public static bool IsOpen => _box != null && _box.activeSelf;

        public static void Tick()
        {
            if (_closeNextFrame)
            {
                _closeNextFrame = false;
                Close();
            }

            for (int i = PendingCash.Count - 1; i >= 0; i--)
            {
                Pending p = PendingCash[i];
                if (Time.unscaledTime < p.Expires)
                    continue;
                PendingCash.RemoveAt(i);
                AddCash(p.Amount);
                Feed.Alert(string.Format(Text.Ui("cash_refund"), p.To, Money(p.Amount)));
            }

            if (!IsOpen)
                return;

            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb != null && kb.escapeKey.wasPressedThisFrame)
            {
                Close();
                return;
            }
            if (kb != null && (kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame))
            {
                string message = _input.text.Trim();
                _closeNextFrame = true;
                if (message.Length > 0)
                    Submit(message);
            }
        }

        // ---- Input box ----

        public static void Toggle()
        {
            Build();
            if (IsOpen)
            {
                Close();
                return;
            }
            _box.SetActive(true);
            _input.text = "";
            _input.ActivateInputField();
            if (EventSystem.current != null)
                EventSystem.current.SetSelectedGameObject(_input.gameObject);
        }

        private static void Close()
        {
            if (_box != null)
                _box.SetActive(false);
            if (EventSystem.current != null)
                EventSystem.current.SetSelectedGameObject(null);
        }

        private static void Build()
        {
            if (_box != null)
                return;
            Feed.Ensure();

            _box = new GameObject("S1 Chat Input");
            _box.transform.SetParent(Feed.Root.transform, false);
            RectTransform r = _box.AddComponent<RectTransform>();
            r.anchorMin = r.anchorMax = Vector2.zero;
            r.pivot = Vector2.zero;
            r.anchoredPosition = new Vector2(24f, 128f);
            r.sizeDelta = new Vector2(470f, 34f);
            Image bg = _box.AddComponent<Image>();
            bg.color = new Color(0f, 0f, 0f, 0.82f);

            var area = new GameObject("Text Area");
            area.transform.SetParent(_box.transform, false);
            RectTransform ar = area.AddComponent<RectTransform>();
            ar.anchorMin = Vector2.zero;
            ar.anchorMax = Vector2.one;
            ar.offsetMin = new Vector2(10f, 4f);
            ar.offsetMax = new Vector2(-10f, -4f);
            area.AddComponent<RectMask2D>();

            var textGo = new GameObject("Text");
            textGo.transform.SetParent(area.transform, false);
            TextMeshProUGUI text = Feed.NewLabel(textGo, 18f);
            text.enableWordWrapping = false;
            RectTransform tr = textGo.GetComponent<RectTransform>();
            tr.anchorMin = Vector2.zero;
            tr.anchorMax = Vector2.one;
            tr.offsetMin = tr.offsetMax = Vector2.zero;

            var placeholderGo = new GameObject("Placeholder");
            placeholderGo.transform.SetParent(area.transform, false);
            TextMeshProUGUI placeholder = Feed.NewLabel(placeholderGo, 18f);
            placeholder.color = new Color(1f, 1f, 1f, 0.45f);
            placeholder.enableWordWrapping = false;
            placeholder.text = Text.Ui("chat_hint");
            RectTransform pr = placeholderGo.GetComponent<RectTransform>();
            pr.anchorMin = Vector2.zero;
            pr.anchorMax = Vector2.one;
            pr.offsetMin = pr.offsetMax = Vector2.zero;

            _input = _box.AddComponent<TMP_InputField>();
            _input.textViewport = ar;
            _input.textComponent = text;
            _input.placeholder = placeholder;
            _input.characterLimit = MaxLength;
            _input.lineType = TMP_InputField.LineType.SingleLine;
            _box.SetActive(false);
        }

        public static void Submit(string message)
        {
            string[] words = message.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string first = words[0].ToLowerInvariant();
            if (first == "/grana" || first == "/cash" || first == "/dar" || first == "/give")
            {
                SendCash(words);
                return;
            }

            if (first == "/sync")
            {
                Sync.RequestNow();
                return;
            }

            Net.Send(Net.ChatLine, Net.LocalName, "", "", message);
            if (!Mod.Echo.Value)
                ShowChat(Net.LocalName, message);
        }

        public static void ShowChat(string who, string message)
        {
            // Player text is not rich text: a typed tag must not restyle the feed.
            string safe = message.Replace("<", "<noparse><</noparse>");
            Feed.Speech(who, safe, Team.HexOf(who));
        }

        // ---- Cash ----

        // "/grana 500" gives to the nearest player; "/grana Name 500" picks one.
        private static void SendCash(string[] words)
        {
            float amount = 0f;
            string target = null;
            foreach (string w in words)
            {
                if (w.StartsWith("/"))
                    continue;
                if (float.TryParse(w.TrimStart('$').Replace("R$", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed))
                    amount = parsed;
                else
                    target = w;
            }

            Player to = FindPlayer(target);
            MoneyManager money = NetworkSingleton<MoneyManager>.Instance;
            if (amount <= 0f || to == null || money == null)
            {
                Feed.System(Text.Ui(to == null ? "cash_nobody" : "cash_usage"));
                return;
            }
            if (money.cashBalance < amount)
            {
                Feed.System(Text.Ui("cash_short"));
                return;
            }

            string id = Guid.NewGuid().ToString("N").Substring(0, 8);
            money.ChangeCashBalance(-amount, true, true);
            PendingCash.Add(new Pending { To = to.PlayerName, Amount = amount, Id = id, Expires = Time.unscaledTime + RefundAfter });
            Net.Send(Net.CashSend, Net.LocalName, to.PlayerName, id, amount.ToString(CultureInfo.InvariantCulture));
            Feed.System(string.Format(Text.Ui("cash_sent"), to.PlayerName, Money(amount)));
        }

        private static Player FindPlayer(string name)
        {
            Player best = null;
            float bestDistance = float.MaxValue;
            if (Player.PlayerList == null || Player.Local == null)
                return null;
            foreach (Player p in Player.PlayerList)
            {
                if (p == null || (p.IsLocalPlayer && !Mod.Echo.Value))
                    continue;
                if (name != null)
                {
                    if (p.PlayerName.StartsWith(name, StringComparison.OrdinalIgnoreCase))
                        return p;
                    continue;
                }
                float d = Vector3.Distance(p.transform.position, Player.Local.transform.position);
                if (d < bestDistance)
                {
                    best = p;
                    bestDistance = d;
                }
            }
            return best;
        }

        private static void AddCash(float amount)
        {
            NetworkSingleton<MoneyManager>.Instance?.ChangeCashBalance(amount, true, true);
        }

        public static void ReceiveCash(string from, string to, string id, string amountText)
        {
            if (to != Net.LocalName || !float.TryParse(amountText, NumberStyles.Float, CultureInfo.InvariantCulture, out float amount) || amount <= 0f)
                return;
            AddCash(amount);
            Net.Send(Net.CashAck, Net.LocalName, from, id, "");
            Feed.Alert(string.Format(Text.Ui("cash_got"), from, Money(amount)));
        }

        public static void ReceiveAck(string to, string id)
        {
            if (to == Net.LocalName)
                PendingCash.RemoveAll(p => p.Id == id);
        }

        private static string Money(float v) => Text.Display("$" + Mathf.RoundToInt(v).ToString("#,0", CultureInfo.InvariantCulture));

        // ---- Scoreboard ----

        public static void CountSale(string who, string amountText)
        {
            if (!Today.TryGetValue(who, out Tally t))
                Today[who] = t = new Tally();
            t.Sales++;
            if (float.TryParse(amountText, NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
                t.Money += v;
        }

        public static void ShowScoreboard()
        {
            if (Today.Count == 0)
                return;
            var rows = new List<KeyValuePair<string, Tally>>(Today);
            rows.Sort((a, b) => b.Value.Money.CompareTo(a.Value.Money));
            var parts = new List<string>();
            foreach (KeyValuePair<string, Tally> row in rows)
                parts.Add($"<color={Team.HexOf(row.Key)}><b>{row.Key}</b></color>: {string.Format(Text.Ui("score_row"), row.Value.Sales, Money(row.Value.Money))}");
            Feed.Speech(Text.Ui("score_title"), string.Join("\n", parts), "#FFD966");
            Today.Clear();
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.UI;
using Il2CppScheduleOne.UI.Compass;
using Il2CppScheduleOne.UI.Phone;
using Il2CppTMPro;
using UnityEngine;
using UnityEngine.UI;

namespace S1CoopTalk
{
    /// <summary>Player icons, compass entries, the partner panel, markers and pings.</summary>
    public static class Team
    {
        private const float PingSeconds = 30f, PingRange = 400f, MarkerMinDistance = 40f, ActivitySeconds = 30f;
        private const float IconSize = 26f;

        private static readonly Color32[] Palette =
        {
            new Color32(92, 200, 255, 255), new Color32(255, 128, 190, 255), new Color32(140, 230, 100, 255), new Color32(255, 196, 70, 255),
            new Color32(190, 140, 255, 255), new Color32(255, 120, 100, 255), new Color32(80, 225, 190, 255), new Color32(235, 235, 235, 255)
        };

        private sealed class Remote
        {
            public string Activity = "";
            public float ActivityAt;
            public bool Wanted;
        }

        private sealed class Marker
        {
            public GameObject Root;
            public RectTransform Rect;
            public TextMeshProUGUI Label;
        }

        private sealed class Ping
        {
            public string Who;
            public Vector3 Position;
            public float Expires;
            public Marker Marker;
            public GameObject Anchor;
            public CompassManager.Element Compass;
        }

        private sealed class Row
        {
            public GameObject Root;
            public Image Icon;
            public TextMeshProUGUI Letter, Text;
        }

        private static readonly Dictionary<string, Remote> Remotes = new Dictionary<string, Remote>();
        private static readonly Dictionary<string, Marker> PlayerMarkers = new Dictionary<string, Marker>();
        private static readonly Dictionary<string, Row> Rows = new Dictionary<string, Row>();
        private static readonly Dictionary<string, CompassManager.Element> CompassEntries = new Dictionary<string, CompassManager.Element>();
        private static readonly List<Ping> Pings = new List<Ping>();
        private static readonly HashSet<string> Present = new HashSet<string>();
        private static readonly HashSet<string> WaitingInBed = new HashSet<string>();
        private static List<string> _order = new List<string>();

        private static GameObject _panel;
        private static RectTransform _overlay;
        private static GameObject _templateHolder;
        private static Sprite _circle;
        private static float _nextPing;
        private static bool _wasTalking, _wasOnPhone, _wasWanted, _wasDown;
        private static bool _rosterReady;

        public static void Reset()
        {
            Remotes.Clear();
            Present.Clear();
            WaitingInBed.Clear();
            _rosterReady = false;
            _wasTalking = _wasOnPhone = _wasWanted = _wasDown = false;
            foreach (Ping p in Pings)
                DestroyPing(p);
            Pings.Clear();
            foreach (Marker m in PlayerMarkers.Values)
                if (m.Root != null)
                    UnityEngine.Object.Destroy(m.Root);
            PlayerMarkers.Clear();
            foreach (Row r in Rows.Values)
                if (r.Root != null)
                    UnityEngine.Object.Destroy(r.Root);
            Rows.Clear();
            CompassEntries.Clear(); // The compass belongs to the scene and goes away with it.
        }

        private static Remote Get(string who)
        {
            if (!Remotes.TryGetValue(who, out Remote r))
                Remotes[who] = r = new Remote();
            return r;
        }

        public static void SetActivity(string who, string activity)
        {
            Remote r = Get(who);
            r.Activity = activity;
            r.ActivityAt = Time.unscaledTime;
        }

        public static void ClearActivity(string who) => Get(who).Activity = "";

        public static void SetWanted(string who, bool wanted) => Get(who).Wanted = wanted;

        // ---- Player identity: the same color and letter on every player's screen ----

        public static Color32 ColorOf(string name)
        {
            int i = _order.IndexOf(name);
            return Palette[(i >= 0 ? i : StableHash(name)) % Palette.Length];
        }

        public static string HexOf(string name)
        {
            Color32 c = ColorOf(name);
            return $"#{c.r:X2}{c.g:X2}{c.b:X2}";
        }

        private static int StableHash(string s)
        {
            int h = 17;
            foreach (char c in s)
                h = h * 31 + c;
            return h & int.MaxValue;
        }

        private static string LetterOf(string name) => string.IsNullOrEmpty(name) ? "?" : name.Substring(0, 1).ToUpperInvariant();

        private static List<Player> Others()
        {
            var list = new List<Player>();
            if (Player.PlayerList == null)
                return list;
            foreach (Player p in Player.PlayerList)
                if (p != null && (!p.IsLocalPlayer || Mod.Echo.Value))
                    list.Add(p);
            return list;
        }

        // ---- Sending ----

        public static void PollLocal()
        {
            Player me = Player.Local;
            if (me == null)
                return;

            bool talking = Safe(() => Singleton<DialogueCanvas>.Instance != null && Singleton<DialogueCanvas>.Instance.IsOpen);
            bool phone = Safe(() => Singleton<CallInterface>.Instance != null && Singleton<CallInterface>.Instance.IsOpen);
            if ((_wasTalking && !talking) || (_wasOnPhone && !phone))
                Net.Send(Net.ActivityEnd, Net.LocalName, "", "", "");
            _wasTalking = talking;
            _wasOnPhone = phone;

            bool wanted = Safe(() => me.CrimeData != null && (int)me.CrimeData.CurrentPursuitLevel != 0);
            if (wanted != _wasWanted)
                Net.Send(wanted ? Net.WantedOn : Net.WantedOff, Net.LocalName, "", "", "");
            _wasWanted = wanted;

            bool down = me.IsUnconscious;
            if (down && !_wasDown)
                Net.Send(Net.Died, Net.LocalName, "", "", "");
            _wasDown = down;
        }

        private static bool Safe(Func<bool> f)
        {
            try
            {
                return f();
            }
            catch
            {
                return false;
            }
        }

        public static void SendPing(bool sos)
        {
            if (Time.unscaledTime < _nextPing || Camera.main == null)
                return;
            _nextPing = Time.unscaledTime + 1.5f;

            Transform cam = Camera.main.transform;
            Vector3 spot = Physics.Raycast(new Ray(cam.position, cam.forward), out RaycastHit hit, PingRange)
                ? hit.point
                : cam.position + cam.forward * 60f;
            string body = string.Format(CultureInfo.InvariantCulture, "{0:F1},{1:F1},{2:F1},{3}", spot.x, spot.y, spot.z, sos ? 1 : 0);
            Net.Send(Net.PingMark, Net.LocalName, "", "", body);
            ShowPing(Net.LocalName, spot, sos, false);
        }

        // ---- Receiving ----

        public static void ReceivePing(string who, string body)
        {
            string[] p = body.Split(',');
            if (p.Length < 3
                || !float.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                || !float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y)
                || !float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
                return;
            ShowPing(who, new Vector3(x, y, z), p.Length > 3 && p[3] == "1", true);
        }

        private static void DestroyPing(Ping p)
        {
            try
            {
                if (p.Compass != null && Singleton<CompassManager>.Instance != null)
                    Singleton<CompassManager>.Instance.RemoveElement(p.Compass, true);
            }
            catch (Exception e)
            {
                Mod.Log.Warning("Compass ping removal failed: " + e.Message);
            }
            if (p.Marker?.Root != null)
                UnityEngine.Object.Destroy(p.Marker.Root);
            if (p.Anchor != null)
                UnityEngine.Object.Destroy(p.Anchor);
        }

        private static void ShowPing(string who, Vector3 where, bool sos, bool announce)
        {
            Ensure();
            Pings.RemoveAll(existing =>
            {
                if (existing.Who != who)
                    return false;
                DestroyPing(existing);
                return true;
            });

            Color color = sos ? new Color(1f, 0.25f, 0.2f) : (Color)ColorOf(who);
            var anchor = new GameObject("S1 Ping Anchor");
            anchor.transform.position = where;
            Pings.Add(new Ping
            {
                Who = who,
                Position = where,
                Expires = Time.unscaledTime + PingSeconds,
                Marker = NewMarker(color, 26f),
                Anchor = anchor,
                Compass = AddToCompass(anchor.transform, color, sos ? "!" : "+")
            });
            if (!announce)
                return;

            string text = string.Format(Text.Ui(sos ? "sos" : "ping"), who, Meters(where));
            if (sos)
                Feed.Alert(text);
            else
                Feed.System(text);
        }

        private static string Meters(Vector3 where)
        {
            Player me = Player.Local;
            return me == null ? "?" : Mathf.RoundToInt(Vector3.Distance(me.transform.position, where)) + " m";
        }

        // ---- Roster: joins, leaves, who is waiting in bed ----

        public static void PollRoster()
        {
            if (Player.PlayerList == null || Player.Local == null)
                return;

            var all = new List<string>();
            foreach (Player p in Player.PlayerList)
                if (p != null)
                    all.Add(p.PlayerName);
            all.Sort(StringComparer.Ordinal);
            _order = all;

            var now = new HashSet<string>();
            foreach (Player p in Others())
            {
                string name = p.PlayerName;
                now.Add(name);

                if (p.IsReadyToSleep && !Player.Local.IsReadyToSleep && WaitingInBed.Add(name))
                    Feed.Alert(string.Format(Text.Ui("bed"), name));
                else if (!p.IsReadyToSleep)
                    WaitingInBed.Remove(name);

                if (!CompassEntries.ContainsKey(name))
                {
                    CompassManager.Element el = AddToCompass(p.transform, ColorOf(name), LetterOf(name));
                    if (el != null)
                        CompassEntries[name] = el;
                }
            }

            if (_rosterReady)
            {
                foreach (string name in now)
                    if (!Present.Contains(name))
                        Feed.System(string.Format(Text.Ui("arrived"), name));
                foreach (string name in Present)
                    if (!now.Contains(name))
                    {
                        Feed.Alert(string.Format(Text.Ui("left"), name));
                        DropPlayer(name);
                    }
            }
            _rosterReady = true;
            Present.Clear();
            Present.UnionWith(now);
        }

        private static void DropPlayer(string name)
        {
            if (Rows.TryGetValue(name, out Row row))
            {
                if (row.Root != null)
                    UnityEngine.Object.Destroy(row.Root);
                Rows.Remove(name);
            }
            if (PlayerMarkers.TryGetValue(name, out Marker m))
            {
                if (m.Root != null)
                    UnityEngine.Object.Destroy(m.Root);
                PlayerMarkers.Remove(name);
            }
            if (CompassEntries.TryGetValue(name, out CompassManager.Element el))
            {
                CompassEntries.Remove(name);
                try
                {
                    Singleton<CompassManager>.Instance?.RemoveElement(el, true);
                }
                catch (Exception e)
                {
                    Mod.Log.Warning("Compass removal failed: " + e.Message);
                }
            }
        }

        // ---- Compass ----

        private static CompassManager.Element AddToCompass(Transform target, Color color, string letter)
        {
            try
            {
                CompassManager compass = Singleton<CompassManager>.Instance;
                if (compass == null)
                    return null;
                Ensure();
                RectTransform template = NewIcon(_templateHolder.transform, color, letter, IconSize, out _, out _);
                CompassManager.Element el = compass.AddElement(target, template, true);
                UnityEngine.Object.Destroy(template.gameObject);
                return el;
            }
            catch (Exception e)
            {
                Mod.Log.Warning("Compass icon failed: " + e.Message);
                return null;
            }
        }

        // ---- Icons ----

        private static Sprite Circle()
        {
            if (_circle != null)
                return _circle;
            const int size = 64;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            float r = size / 2f;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float d = Mathf.Sqrt((x + .5f - r) * (x + .5f - r) + (y + .5f - r) * (y + .5f - r));
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01(r - d)));
                }
            tex.Apply();
            tex.hideFlags = HideFlags.HideAndDontSave;
            _circle = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(.5f, .5f));
            _circle.hideFlags = HideFlags.HideAndDontSave;
            return _circle;
        }

        private static RectTransform NewIcon(Transform parent, Color color, string letter, float size, out Image image, out TextMeshProUGUI text)
        {
            var go = new GameObject("S1 Player Icon");
            go.transform.SetParent(parent, false);
            RectTransform rect = go.AddComponent<RectTransform>();
            rect.sizeDelta = new Vector2(size, size);
            image = go.AddComponent<Image>();
            image.sprite = Circle();
            image.color = color;
            image.raycastTarget = false;

            var labelGo = new GameObject("Letter");
            labelGo.transform.SetParent(go.transform, false);
            text = Feed.NewLabel(labelGo, size * 0.62f);
            text.alignment = TextAlignmentOptions.Center;
            text.color = new Color(0.08f, 0.08f, 0.08f);
            text.fontStyle = FontStyles.Bold;
            text.enableWordWrapping = false;
            RectTransform lr = labelGo.GetComponent<RectTransform>();
            lr.anchorMin = Vector2.zero;
            lr.anchorMax = Vector2.one;
            lr.offsetMin = lr.offsetMax = Vector2.zero;
            text.text = letter;
            return rect;
        }

        // ---- Per-frame UI ----

        public static void Tick()
        {
            if (Player.Local == null || Player.PlayerList == null)
                return;
            Ensure();
            UpdatePanel();
            UpdateMarkers();
        }

        private static Row RowFor(string name)
        {
            if (Rows.TryGetValue(name, out Row row) && row.Root != null)
                return row;

            var go = new GameObject("S1 Partner " + name);
            go.transform.SetParent(_panel.transform, false);
            HorizontalLayoutGroup h = go.AddComponent<HorizontalLayoutGroup>();
            h.spacing = 8f;
            h.childAlignment = TextAnchor.UpperLeft;
            h.childControlHeight = true;
            h.childControlWidth = true;
            h.childForceExpandHeight = false;
            h.childForceExpandWidth = false;

            RectTransform icon = NewIcon(go.transform, ColorOf(name), LetterOf(name), IconSize, out Image image, out TextMeshProUGUI letter);
            LayoutElement le = icon.gameObject.AddComponent<LayoutElement>();
            le.minWidth = le.preferredWidth = IconSize;
            le.minHeight = le.preferredHeight = IconSize;

            var textGo = new GameObject("Text");
            textGo.transform.SetParent(go.transform, false);
            TextMeshProUGUI t = Feed.NewLabel(textGo, 17f);
            LayoutElement te = textGo.AddComponent<LayoutElement>();
            te.flexibleWidth = 1f;

            row = new Row { Root = go, Icon = image, Letter = letter, Text = t };
            Rows[name] = row;
            return row;
        }

        private static void UpdatePanel()
        {
            int shown = 0;
            foreach (Player p in Others())
            {
                shown++;
                string name = p.PlayerName;
                Row row = RowFor(name);
                row.Icon.color = ColorOf(name);

                string distance = Mathf.RoundToInt(Vector3.Distance(Player.Local.transform.position, p.transform.position)) + " m";
                string region = Text.Display(p.CurrentRegion.ToString());
                string status = StatusOf(p, name);
                string text = $"<b>{name}</b>\n<size=85%><color=#D8D8D8>{distance} · {region}</color></size>";
                if (status.Length > 0)
                    text += $"\n<size=85%>{status}</size>";
                if (row.Text.text != text)
                    row.Text.text = text;
            }

            bool visible = shown > 0;
            if (_panel.activeSelf != visible)
                _panel.SetActive(visible);
        }

        private static string StatusOf(Player p, string name)
        {
            if (p.IsArrested)
                return Hex("#FF7A6B", Text.Ui("st_arrested"));
            if (p.IsUnconscious)
                return Hex("#FF7A6B", Text.Ui("st_down"));
            Remote r = Get(name);
            if (r.Wanted)
                return Hex("#FF9F5A", Text.Ui("st_wanted"));
            if (p.IsReadyToSleep)
                return Hex("#A9C8FF", Text.Ui("st_bed"));
            if (r.Activity.Length > 0 && Time.unscaledTime - r.ActivityAt < ActivitySeconds)
                return Hex("#8FD9FF", r.Activity);
            if (p.IsInVehicle)
                return Hex("#C8C8C8", Text.Ui("st_driving"));
            return "";
        }

        private static string Hex(string hex, string s) => $"<color={hex}>{s}</color>";

        private static void UpdateMarkers()
        {
            Camera cam = Camera.main;
            if (cam == null)
                return;

            for (int i = Pings.Count - 1; i >= 0; i--)
            {
                Ping ping = Pings[i];
                if (Time.unscaledTime > ping.Expires)
                {
                    DestroyPing(ping);
                    Pings.RemoveAt(i);
                    continue;
                }
                Place(ping.Marker, cam, ping.Position, $"{ping.Who}\n{Meters(ping.Position)}", true);
            }

            foreach (Player p in Others())
            {
                string name = p.PlayerName;
                if (!PlayerMarkers.TryGetValue(name, out Marker m))
                    PlayerMarkers[name] = m = NewMarker(ColorOf(name), 18f);

                Vector3 at = p.transform.position + Vector3.up * 2.1f;
                bool far = Vector3.Distance(cam.transform.position, at) >= MarkerMinDistance;
                if (m.Root.activeSelf != far)
                    m.Root.SetActive(far);
                if (far)
                    Place(m, cam, at, $"{name}\n{Meters(at)}", false);
            }
        }

        private static void Place(Marker m, Camera cam, Vector3 world, string label, bool clampToEdges)
        {
            Vector3 screen = cam.WorldToScreenPoint(world);
            bool behind = screen.z < 0f;
            if (behind && !clampToEdges)
            {
                m.Root.SetActive(false);
                return;
            }
            if (!m.Root.activeSelf)
                m.Root.SetActive(true);

            if (behind)
                screen = new Vector3(Screen.width - screen.x, 0f, 0f);
            if (clampToEdges)
            {
                screen.x = Mathf.Clamp(screen.x, 60f, Screen.width - 60f);
                screen.y = Mathf.Clamp(screen.y, 60f, Screen.height - 60f);
            }

            float scale = _overlay.rect.width / Mathf.Max(1f, Screen.width);
            m.Rect.anchoredPosition = new Vector2(screen.x * scale, screen.y * scale);
            if (m.Label.text != label)
                m.Label.text = label;
        }

        private static Marker NewMarker(Color color, float iconSize)
        {
            var root = new GameObject("S1 Marker");
            root.transform.SetParent(_overlay, false);
            RectTransform rect = root.AddComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = Vector2.zero;
            rect.sizeDelta = Vector2.zero;

            var icon = new GameObject("Icon");
            icon.transform.SetParent(root.transform, false);
            Image img = icon.AddComponent<Image>();
            img.color = color;
            img.raycastTarget = false;
            RectTransform ir = icon.GetComponent<RectTransform>();
            ir.sizeDelta = new Vector2(iconSize * 0.7f, iconSize * 0.7f);
            ir.localRotation = Quaternion.Euler(0f, 0f, 45f);

            var labelGo = new GameObject("Label");
            labelGo.transform.SetParent(root.transform, false);
            TextMeshProUGUI label = Feed.NewLabel(labelGo, 17f);
            label.alignment = TextAlignmentOptions.Top;
            label.outlineWidth = 0.2f;
            label.outlineColor = new Color32(0, 0, 0, 255);
            RectTransform lr = labelGo.GetComponent<RectTransform>();
            lr.sizeDelta = new Vector2(240f, 60f);
            lr.anchoredPosition = new Vector2(0f, -iconSize - 18f);

            return new Marker { Root = root, Rect = rect, Label = label };
        }

        private static void Ensure()
        {
            if (_panel != null)
                return;

            Feed.Ensure();
            var overlay = new GameObject("Markers");
            overlay.transform.SetParent(Feed.Root.transform, false);
            _overlay = overlay.AddComponent<RectTransform>();
            _overlay.anchorMin = Vector2.zero;
            _overlay.anchorMax = Vector2.one;
            _overlay.offsetMin = _overlay.offsetMax = Vector2.zero;
            overlay.transform.SetAsFirstSibling();

            _templateHolder = new GameObject("Templates");
            _templateHolder.transform.SetParent(Feed.Root.transform, false);
            _templateHolder.SetActive(false);

            _panel = new GameObject("S1 Partner Panel");
            _panel.transform.SetParent(Feed.Root.transform, false);
            RectTransform pr = _panel.AddComponent<RectTransform>();
            pr.anchorMin = pr.anchorMax = new Vector2(1f, 1f);
            pr.pivot = new Vector2(1f, 1f);
            pr.anchoredPosition = new Vector2(-20f, -200f);
            pr.sizeDelta = new Vector2(290f, 0f);
            Image bg = _panel.AddComponent<Image>();
            bg.color = new Color(0f, 0f, 0f, 0.72f);
            bg.raycastTarget = false;
            VerticalLayoutGroup v = _panel.AddComponent<VerticalLayoutGroup>();
            v.padding = new RectOffset(10, 10, 8, 8);
            v.spacing = 8f;
            v.childControlHeight = true;
            v.childControlWidth = true;
            v.childForceExpandHeight = false;
            ContentSizeFitter fit = _panel.AddComponent<ContentSizeFitter>();
            fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            _panel.SetActive(false);
        }
    }
}

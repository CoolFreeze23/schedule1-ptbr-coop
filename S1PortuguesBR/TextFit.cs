using System.Collections.Generic;
using Il2CppTMPro;

namespace S1PortuguesBR
{
    /// <summary>
    /// Portuguese often runs longer than English and many labels have fixed boxes. Translated labels
    /// that actually overflow get auto-sizing, capped at their original size, so they shrink just
    /// enough to fit. Everything else keeps its exact look.
    /// </summary>
    public static class TextFit
    {
        private const float MinScale = 0.65f;
        private const int ChecksPerFrame = 30;

        private static readonly List<TMP_Text> Tracked = new List<TMP_Text>();
        private static readonly HashSet<int> Known = new HashSet<int>();
        private static int _cursor;

        public static void Track(TMP_Text text)
        {
            if (text != null && Known.Add(text.GetInstanceID()))
                Tracked.Add(text);
        }

        public static void Tick()
        {
            if (!Translator.Enabled)
                return;

            for (int n = 0; n < ChecksPerFrame && Tracked.Count > 0; n++)
            {
                if (_cursor >= Tracked.Count)
                    _cursor = 0;
                TMP_Text t = Tracked[_cursor];
                if (t == null)
                {
                    Tracked.RemoveAt(_cursor);
                    continue;
                }
                _cursor++;

                if (!t.isActiveAndEnabled || t.enableAutoSizing)
                    continue;
                if (!Overflows(t))
                    continue;
                float height = t.rectTransform.rect.height;
                if (height < t.fontSize * 0.9f)
                    continue; // Boxes smaller than one line overflow by design.

                t.fontSizeMax = t.fontSize;
                t.fontSizeMin = t.fontSize * MinScale;
                t.enableAutoSizing = true;
                t.ForceMeshUpdate(false, false);
            }
        }

        private static bool Overflows(TMP_Text t)
        {
            if (t.isTextOverflowing || t.isTextTruncated)
                return true;

            // A word broken across lines ("Ferrament/as") doesn't count as overflow, so compare the
            // longest word with the box.
            string shown = t.GetParsedText();
            if (string.IsNullOrEmpty(shown) || !t.enableWordWrapping)
                return false;
            string longest = "";
            foreach (string word in shown.Split(' ', '\n'))
                if (word.Length > longest.Length)
                    longest = word;
            return longest.Length > 3 && t.GetPreferredValues(longest).x > t.rectTransform.rect.width + 0.5f;
        }
    }
}

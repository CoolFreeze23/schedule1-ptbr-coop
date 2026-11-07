using System.Collections.Generic;
using Il2CppTMPro;
using UnityEngine;

namespace S1PortuguesBR
{
    /// <summary>Gives every TMP font a fallback holding the Portuguese letters it lacks.</summary>
    public static class Fonts
    {
        private const string Required = "áàâãéêíóôõúüçÁÀÂÃÉÊÍÓÔÕÚÜÇ";
        private static readonly HashSet<int> Done = new HashSet<int>();
        private const string FallbackName = "LiberationSans SDF - Fallback";
        private static TMP_FontAsset _fallback;

        public static void EnsureCoverage()
        {
            foreach (TMP_FontAsset font in Resources.FindObjectsOfTypeAll<TMP_FontAsset>())
            {
                if (font == null || !Done.Add(font.GetInstanceID()))
                    continue;

                string missing = "";
                foreach (char c in Required)
                    if (!font.HasCharacter(c, false, true))
                        missing += c;

                if (missing.Length == 0)
                    continue;

                Mod.Log.Msg($"Font '{font.name}' lacks {missing.Length} letters, adding fallback");
                TMP_FontAsset fallback = GetFallback();
                if (fallback == null)
                    continue;
                if (font.fallbackFontAssetTable == null)
                    font.fallbackFontAssetTable = new Il2CppSystem.Collections.Generic.List<TMP_FontAsset>();
                if (!font.fallbackFontAssetTable.Contains(fallback))
                    font.fallbackFontAssetTable.Add(fallback);
            }
        }

        private static TMP_FontAsset GetFallback()
        {
            if (_fallback != null)
                return _fallback;
            foreach (TMP_FontAsset f in Resources.FindObjectsOfTypeAll<TMP_FontAsset>())
                if (f != null && f.name == FallbackName)
                    _fallback = f;
            if (_fallback == null)
                Mod.Log.Warning("Fallback font not found: " + FallbackName);
            return _fallback;
        }
    }
}

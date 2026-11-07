using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Il2CppScheduleOne.ScriptableObjects;
using Il2CppScheduleOne.UI;
using Il2CppScheduleOne.VoiceOver;
using Il2CppScheduleOne.UI.MainMenu;
using Il2CppScheduleOne.UI.Phone;
using Il2CppTMPro;
using UnityEngine;
using UnityEngine.UI;

namespace S1PortuguesBR
{
    /// <summary>Every path text takes to reach the screen.</summary>
    public static class Patches
    {
        // Text components the game animates character by character. Their source string is
        // translated once upstream, so the partial strings must pass through untouched.
        private static readonly HashSet<int> Typewriter = new HashSet<int>();
        private static bool _inRefresh;

        public static void Apply(HarmonyLib.Harmony h)
        {
            Prefix(h, AccessTools.PropertySetter(typeof(TMP_Text), nameof(TMP_Text.text)), nameof(TmpSetText));
            Prefix(h, AccessTools.Method(typeof(TMP_Text), nameof(TMP_Text.SetText), new[] { typeof(string), typeof(bool) }), nameof(TmpSetTextMethod));
            Prefix(h, AccessTools.Method(typeof(TextMeshProUGUI), "OnEnable"), nameof(TmpOnEnable));
            Prefix(h, AccessTools.Method(typeof(TextMeshPro), "OnEnable"), nameof(TmpOnEnable));
            Prefix(h, AccessTools.PropertySetter(typeof(Text), nameof(Text.text)), nameof(UguiSetText));
            Prefix(h, AccessTools.Method(typeof(Text), "OnEnable"), nameof(UguiOnEnable));

            MethodBase settingsEnable = AccessTools.Method(typeof(SettingsScreen), "OnEnable");
            if (settingsEnable != null)
                h.Patch(settingsEnable, postfix: new HarmonyMethod(typeof(Patches), nameof(SettingsOpened)));

            Prefix(h, AccessTools.Method(typeof(DialogueCanvas), nameof(DialogueCanvas.DisplayDialogueNode)), nameof(DialogueNode));
            MethodBase getEntry = AccessTools.Method(typeof(VODatabase), nameof(VODatabase.GetEntry));
            if (getEntry != null)
                h.Patch(getEntry, prefix: new HarmonyMethod(typeof(Patches), nameof(VoiceEntry)));
            Prefix(h, AccessTools.Method(typeof(VODatabaseEntry), nameof(VODatabaseEntry.GetRandomClip)), nameof(VoiceClip));
            Prefix(h, AccessTools.Method(typeof(CallInterface), nameof(CallInterface.StartCall)), nameof(CallStart));
            MethodBase mixName = AccessTools.Method(typeof(NewMixScreen), nameof(NewMixScreen.GenerateUniqueName));
            if (mixName != null)
                h.Patch(mixName, prefix: new HarmonyMethod(typeof(Patches), nameof(MixName)));

            foreach (string name in new[] { nameof(HintDisplay.ShowHint), nameof(HintDisplay.ShowHint_10s), nameof(HintDisplay.ShowHint_20s) })
                Prefix(h, AccessTools.Method(typeof(HintDisplay), name), nameof(HintText));
            foreach (string name in new[] { nameof(HintDisplay.QueueHint), nameof(HintDisplay.QueueHint_10s), nameof(HintDisplay.QueueHint_20s) })
                Prefix(h, AccessTools.Method(typeof(HintDisplay), name), nameof(HintMessage));
        }

        private static void Prefix(HarmonyLib.Harmony h, MethodBase target, string patch)
        {
            try
            {
                if (target == null)
                {
                    Mod.Log.Warning("Patch target missing for " + patch);
                    return;
                }
                h.Patch(target, prefix: new HarmonyMethod(typeof(Patches), patch));
            }
            catch (Exception e)
            {
                Mod.Log.Error($"Failed to patch {target?.DeclaringType?.Name}.{target?.Name}: {e.Message}");
            }
        }

        // An entry doesn't know its voice, so remember which database was just asked.
        private static string _voiceName;

        private static void VoiceEntry(VODatabase __instance) => _voiceName = __instance.name;

        private static bool VoiceClip(VODatabaseEntry __instance, ref AudioClip __result)
        {
            if (_voiceName == null)
                return true;
            AudioClip clip = Voices.Get(_voiceName, __instance.LineType.ToString());
            if (clip == null)
                return true;
            __result = clip;
            return false;
        }

        private static void SettingsOpened(SettingsScreen __instance) => LanguageSetting.Attach(__instance);

        private static bool InCasino(TMP_Text t)
        {
            Canvas c = t.canvas;
            if (c == null)
                return false;
            string root = c.rootCanvas != null ? c.rootCanvas.name : c.name;
            return root == "Blackjack" || root == "RideTheBus";
        }

        private static bool IsEditable(TMP_Text t)
        {
            Transform p = t.transform.parent;
            return p != null && (p.GetComponentInParent<TMP_InputField>() != null);
        }

        private static void TmpSetText(TMP_Text __instance, ref string value)
        {
            if (_inRefresh || !Translator.Enabled || string.IsNullOrEmpty(value))
                return;
            if (Typewriter.Contains(__instance.GetInstanceID()) || IsEditable(__instance))
                return;
            string shown = value == "Dealer" && InCasino(__instance) ? "Crupiê" : Translator.Display(value);
            if (!ReferenceEquals(shown, value) && shown != value)
                TextFit.Track(__instance);
            value = shown;
        }

        private static void TmpSetTextMethod(TMP_Text __instance, ref string __0)
        {
            TmpSetText(__instance, ref __0);
        }

        private static void TmpOnEnable(TMP_Text __instance)
        {
            if (_inRefresh || !Translator.Enabled)
                return;
            string current = __instance.m_text;
            if (string.IsNullOrEmpty(current) || Typewriter.Contains(__instance.GetInstanceID()) || IsEditable(__instance))
                return;
            string shown = current == "Dealer" && InCasino(__instance) ? "Crupiê" : Translator.Display(current);
            if (!ReferenceEquals(shown, current) && shown != current)
            {
                SetTmp(__instance, shown);
                TextFit.Track(__instance);
            }
        }

        private static void SetTmp(TMP_Text t, string s)
        {
            bool was = _inRefresh;
            _inRefresh = true;
            try
            {
                t.text = s;
            }
            finally
            {
                _inRefresh = was;
            }
        }

        private static void UguiSetText(ref string value)
        {
            if (!_inRefresh && Translator.Enabled && !string.IsNullOrEmpty(value))
                value = Translator.Display(value);
        }

        private static void UguiOnEnable(Text __instance)
        {
            if (_inRefresh || !Translator.Enabled)
                return;
            string current = __instance.m_Text;
            if (string.IsNullOrEmpty(current))
                return;
            string shown = Translator.Display(current);
            if (shown != current)
            {
                _inRefresh = true;
                try
                {
                    __instance.text = shown;
                }
                finally
                {
                    _inRefresh = false;
                }
            }
        }

        private static void DialogueNode(DialogueCanvas __instance, ref string __2)
        {
            Typewriter.Add(__instance.dialogueText.GetInstanceID());
            __2 = Translator.Display(__2);
        }

        // The stage text is processed inside a coroutine, so the call data itself is switched.
        private static void CallStart(CallInterface __instance, PhoneCallData data)
        {
            Typewriter.Add(__instance.MainText.GetInstanceID());
            if (data == null || data.Stages == null)
                return;
            foreach (PhoneCallData.Stage stage in data.Stages)
                stage.Text = Translator.Enabled ? Translator.Display(stage.Text) : Translator.ToEnglish(stage.Text);
        }

        private static void MixName(NewMixScreen __instance) => MixNames.Apply(__instance);

        private static void HintText(ref string __0)
        {
            __0 = Translator.Display(__0);
        }

        private static void HintMessage(ref string __0)
        {
            __0 = Translator.Display(__0);
        }

        /// <summary>Re-applies the current language to every text object that exists right now.</summary>
        public static void RefreshAll()
        {
            bool toPortuguese = Translator.Enabled;
            foreach (TMP_Text t in Resources.FindObjectsOfTypeAll<TMP_Text>())
            {
                if (t == null || !t.gameObject.scene.IsValid() || Typewriter.Contains(t.GetInstanceID()))
                    continue;
                string current = t.m_text;
                if (string.IsNullOrEmpty(current) || IsEditable(t))
                    continue;
                string next = toPortuguese ? Translator.Display(current) : Translator.ToEnglish(current);
                if (next != current)
                    SetTmp(t, next);
                if (toPortuguese)
                    TextFit.Track(t);
            }
            foreach (Text t in Resources.FindObjectsOfTypeAll<Text>())
            {
                if (t == null || !t.gameObject.scene.IsValid())
                    continue;
                string current = t.m_Text;
                if (string.IsNullOrEmpty(current))
                    continue;
                string next = toPortuguese ? Translator.Display(current) : Translator.ToEnglish(current);
                if (next != current)
                {
                    _inRefresh = true;
                    try
                    {
                        t.text = next;
                    }
                    finally
                    {
                        _inRefresh = false;
                    }
                }
            }
        }
    }
}

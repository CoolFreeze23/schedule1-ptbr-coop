using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using Il2CppTMPro;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;
using UnityEngine.UI;

[assembly: MelonInfo(typeof(S1PortuguesBR.Mod), "Schedule I em Portugues (Brasil)", "1.0.0", "Alvin")]
[assembly: MelonGame("TVGS", "Schedule I")]

namespace S1PortuguesBR
{
    public class Mod : MelonMod
    {
        public const string English = "en";
        public const string Portuguese = "pt-BR";

        public static string DataDir { get; private set; }
        public static MelonLogger.Instance Log { get; private set; }

        private static MelonPreferences_Entry<string> _language;
        private static MelonPreferences_Entry<bool> _logMissing;
        private static readonly ConcurrentQueue<string> MissingQueue = new ConcurrentQueue<string>();
        private float _nextFlush;
        private int _textureTick;

        public static string Language => _language.Value;

        public override void OnInitializeMelon()
        {
            Log = LoggerInstance;
            DataDir = Path.Combine(MelonEnvironment.UserDataDirectory, "S1PortuguesBR");
            Directory.CreateDirectory(DataDir);

            MelonPreferences_Category cat = MelonPreferences.CreateCategory("S1PortuguesBR");
            _language = cat.CreateEntry("Language", Portuguese, "Language", "pt-BR or en. Also changeable in the game's Settings menu.");
            _logMissing = cat.CreateEntry("LogMissing", false, "Log untranslated text", "Writes text with no translation to UserData/S1PortuguesBR/missing.txt.");

            Translator.Load();
            Translator.LogMissing = _logMissing.Value;
            Translator.Enabled = _language.Value == Portuguese;
            LoggerInstance.Msg($"{Translator.EntryCount} strings, {Translator.TemplateCount} templates, language {_language.Value}");

            Patches.Apply(HarmonyInstance);
            if (File.Exists(Path.Combine(MelonEnvironment.ModsDirectory, "Polyglot_Il2Cpp.dll")))
                LoggerInstance.Warning("Polyglot is installed too: two translation mods will fight over the same text. Remove one.");
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            Fonts.EnsureCoverage();
            Patches.RefreshAll();
            Textures.Apply();
            Signs.Apply();
        }

        public override void OnUpdate()
        {
            TextFit.Tick();
            if (Time.unscaledTime < _nextFlush)
                return;
            _nextFlush = Time.unscaledTime + 5f;
            FlushMissing();
            if (++_textureTick % 4 == 0)
                Textures.Apply(); // Props load their textures later than the scene does.
        }

        public static void QueueMissing(string s) => MissingQueue.Enqueue(s);

        private static void FlushMissing()
        {
            if (MissingQueue.IsEmpty)
                return;
            var lines = new List<string>();
            while (MissingQueue.TryDequeue(out string s))
                lines.Add(s.Replace("\r", "").Replace("\n", "\\n"));
            File.AppendAllLines(Path.Combine(DataDir, "missing.txt"), lines);
        }

        public static void SetLanguage(string language)
        {
            if (_language.Value == language)
                return;
            _language.Value = language;
            MelonPreferences.Save();
            Translator.Enabled = language == Portuguese;
            Translator.ClearMemo();
            Patches.RefreshAll();
            Textures.Apply();
            Signs.Apply();
            Log.Msg("Language set to " + language);
        }
    }
}

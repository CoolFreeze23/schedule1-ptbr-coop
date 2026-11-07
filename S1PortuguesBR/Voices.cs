using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppScheduleOne.VoiceOver;
using UnityEngine;

namespace S1PortuguesBR
{
    /// <summary>
    /// Portuguese NPC voice lines. The pack holds mono 16-bit audio per voice and line type. Lines without
    /// words (pain, snoring, grunts) are absent from it, so those keep the original sound.
    /// </summary>
    public static class Voices
    {
        private sealed class Line
        {
            public long Offset;
            public int Samples;
            public AudioClip Clip;
        }

        private const string PackName = "voices.pak";
        private static readonly Dictionary<string, List<Line>> Table = new Dictionary<string, List<Line>>(StringComparer.Ordinal);
        private static readonly System.Random Rng = new System.Random();
        private static readonly Dictionary<string, int> Last = new Dictionary<string, int>();
        private static string _path;
        private static int _rate;
        private static long _dataStart;
        private static bool _loaded;

        public static void Load()
        {
            _loaded = true;
            _path = Path.Combine(Mod.DataDir, PackName);
            if (!File.Exists(_path))
            {
                Mod.Log.Warning("Voice pack not found, NPCs keep their English voices");
                return;
            }

            using var fs = File.OpenRead(_path);
            using var br = new BinaryReader(fs);
            int headerLength = br.ReadInt32();
            using JsonDocument doc = JsonDocument.Parse(Encoding.UTF8.GetString(br.ReadBytes(headerLength)));
            _dataStart = fs.Position;
            _rate = doc.RootElement.GetProperty("rate").GetInt32();
            foreach (JsonProperty entry in doc.RootElement.GetProperty("lines").EnumerateObject())
            {
                var list = new List<Line>();
                foreach (JsonElement e in entry.Value.EnumerateArray())
                    list.Add(new Line { Offset = e[0].GetInt64(), Samples = e[1].GetInt32() });
                Table[entry.Name] = list;
            }
            Mod.Log.Msg($"Voice pack: {Table.Count} line groups");
        }

        /// <summary>The Portuguese clip for a voice and line type, or null to use the game's own.</summary>
        public static AudioClip Get(string voice, string lineType)
        {
            if (!Translator.Enabled)
                return null;
            if (!_loaded)
                Load();

            // Radio chatter is one shared set of lines, whichever voice carries it.
            string key = lineType == "PoliceChatter" ? "Police/PoliceChatter" : voice + "/" + lineType;
            if (!Table.TryGetValue(key, out List<Line> lines) || lines.Count == 0)
                return null;

            // Never the same line twice in a row.
            int index = Rng.Next(lines.Count);
            if (lines.Count > 1 && Last.TryGetValue(key, out int previous) && previous == index)
                index = (index + 1) % lines.Count;
            Last[key] = index;
            return Materialize(key, lines[index]);
        }

        private static AudioClip Materialize(string key, Line line)
        {
            if (line.Clip != null)
                return line.Clip;

            byte[] raw = new byte[line.Samples * 2];
            using (var fs = File.OpenRead(_path))
            {
                fs.Seek(_dataStart + line.Offset, SeekOrigin.Begin);
                int read = 0;
                while (read < raw.Length)
                {
                    int n = fs.Read(raw, read, raw.Length - read);
                    if (n <= 0)
                        break;
                    read += n;
                }
            }

            var samples = new Il2CppStructArray<float>(line.Samples);
            for (int i = 0; i < line.Samples; i++)
                samples[i] = BitConverter.ToInt16(raw, i * 2) / 32768f;

            AudioClip clip = AudioClip.Create(key, line.Samples, 1, _rate, false);
            clip.SetData(samples, 0);
            clip.hideFlags = HideFlags.DontUnloadUnusedAsset;
            line.Clip = clip;
            return clip;
        }
    }
}

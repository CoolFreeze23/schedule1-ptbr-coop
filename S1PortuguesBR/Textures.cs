using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace S1PortuguesBR
{
    /// <summary>Swaps signs, product labels and graffiti for the Portuguese versions.</summary>
    public static class Textures
    {
        private sealed class Entry
        {
            public string File;
            public int[] Avg;
        }

        private static readonly Dictionary<string, List<Entry>> ByName = new Dictionary<string, List<Entry>>();
        private static readonly HashSet<int> Done = new HashSet<int>();
        private static readonly Dictionary<int, Texture2D> Swapped = new Dictionary<int, Texture2D>();
        private static string OriginalsDir => Path.Combine(Mod.DataDir, "originals");
        private static bool _loaded;

        private static void Load()
        {
            _loaded = true;
            Assembly asm = Assembly.GetExecutingAssembly();
            using Stream s = asm.GetManifestResourceStream("textures.manifest.json");
            if (s == null)
                return;
            using JsonDocument doc = JsonDocument.Parse(s);
            foreach (JsonElement e in doc.RootElement.EnumerateArray())
            {
                string name = e.GetProperty("name").GetString();
                if (!ByName.TryGetValue(name, out List<Entry> list))
                    ByName[name] = list = new List<Entry>();
                JsonElement avg = e.GetProperty("avg");
                list.Add(new Entry
                {
                    File = e.GetProperty("file").GetString(),
                    Avg = avg.ValueKind == JsonValueKind.Array ? avg.EnumerateArray().Select(x => x.GetInt32()).ToArray() : null
                });
            }
        }

        /// <summary>Replaces any listed texture that has been loaded since the last call.</summary>
        public static void Apply()
        {
            if (!Translator.Enabled)
            {
                Restore();
                return;
            }
            if (!_loaded)
                Load();

            foreach (Texture2D tex in Resources.FindObjectsOfTypeAll<Texture2D>())
            {
                if (tex == null || !ByName.TryGetValue(tex.name, out List<Entry> options) || !Done.Add(tex.GetInstanceID()))
                    continue;
                try
                {
                    Entry entry = Pick(tex, options);
                    KeepOriginal(tex, entry);
                    Replace(tex, entry);
                    Swapped[tex.GetInstanceID()] = tex;
                }
                catch (Exception e)
                {
                    Mod.Log.Warning($"Texture {tex.name}: {e.Message}");
                }
            }
        }

        // The first time a texture is swapped its original is written out, so switching back to English
        // works without restarting. Later runs find the file already there and skip the work.
        private static string OriginalPath(Entry entry) => Path.Combine(OriginalsDir, entry.File);

        private static readonly Dictionary<int, string> SwappedFile = new Dictionary<int, string>();

        private static void KeepOriginal(Texture2D tex, Entry entry)
        {
            string path = OriginalPath(entry);
            SwappedFile[tex.GetInstanceID()] = path;
            if (File.Exists(path))
                return;
            Directory.CreateDirectory(OriginalsDir);

            RenderTexture rt = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            RenderTexture previous = RenderTexture.active;
            Graphics.Blit(tex, rt);
            RenderTexture.active = rt;
            var read = new Texture2D(tex.width, tex.height, TextureFormat.RGBA32, false);
            read.ReadPixels(new Rect(0, 0, tex.width, tex.height), 0, 0);
            read.Apply();
            File.WriteAllBytes(path, ImageConversion.EncodeToPNG(read).ToArray());
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(rt);
            UnityEngine.Object.Destroy(read);
        }

        private static void Restore()
        {
            foreach (KeyValuePair<int, Texture2D> pair in Swapped)
            {
                if (pair.Value == null || !SwappedFile.TryGetValue(pair.Key, out string path) || !File.Exists(path))
                    continue;
                ImageConversion.LoadImage(pair.Value, new Il2CppStructArray<byte>(File.ReadAllBytes(path)), false);
                Done.Remove(pair.Key);
            }
            Swapped.Clear();
        }

        // A few textures share a name; the one meant is told apart by its average color.
        private static Entry Pick(Texture2D tex, List<Entry> options)
        {
            if (options.Count == 1)
                return options[0];

            Color32 avg = Average(tex);
            return options.OrderBy(o => o.Avg == null ? int.MaxValue
                : Math.Abs(o.Avg[0] - avg.r) + Math.Abs(o.Avg[1] - avg.g) + Math.Abs(o.Avg[2] - avg.b)).First();
        }

        private static Color32 Average(Texture2D tex)
        {
            RenderTexture rt = RenderTexture.GetTemporary(1, 1, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            RenderTexture previous = RenderTexture.active;
            Graphics.Blit(tex, rt);
            RenderTexture.active = rt;
            var read = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            read.ReadPixels(new Rect(0, 0, 1, 1), 0, 0);
            read.Apply();
            Color32 c = read.GetPixel(0, 0);
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(rt);
            UnityEngine.Object.Destroy(read);
            return c;
        }

        private static void Replace(Texture2D tex, Entry entry)
        {
            using Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("textures." + entry.File);
            if (s == null)
                return;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            byte[] raw = ms.ToArray();
            ImageConversion.LoadImage(tex, new Il2CppStructArray<byte>(raw), false);
        }
    }
}

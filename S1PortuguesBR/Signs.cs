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
    /// <summary>3D signs whose English lettering is geometry: swapped for meshes rebuilt with Portuguese letters.</summary>
    public static class Signs
    {
        private sealed class Swap
        {
            public string MeshName, Where;
            public Mesh Mesh;
            public string Resource;
        }

        private static readonly List<Swap> Swaps = new List<Swap>();
        private static readonly List<(Renderer hidden, GameObject replacement)> Replaced = new List<(Renderer, GameObject)>();
        private static readonly Dictionary<int, Mesh> Originals = new Dictionary<int, Mesh>();
        private static readonly Dictionary<int, MeshFilter> Filters = new Dictionary<int, MeshFilter>();
        private static bool _loaded;

        private static void Load()
        {
            _loaded = true;
            Assembly asm = Assembly.GetExecutingAssembly();
            foreach (string res in asm.GetManifestResourceNames().Where(r => r.StartsWith("signs.")))
            {
                using Stream s = asm.GetManifestResourceStream(res);
                using JsonDocument doc = JsonDocument.Parse(s);
                Swaps.Add(new Swap
                {
                    Resource = res,
                    MeshName = doc.RootElement.GetProperty("mesh").GetString(),
                    Where = doc.RootElement.GetProperty("where").GetString()
                });
            }
        }

        private static Mesh Build(Swap swap)
        {
            if (swap.Mesh != null)
                return swap.Mesh;

            using Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(swap.Resource);
            using JsonDocument doc = JsonDocument.Parse(s);
            JsonElement root = doc.RootElement;

            JsonElement verts = root.GetProperty("verts"), normals = root.GetProperty("normals"), uvs = root.GetProperty("uvs");
            int n = verts.GetArrayLength();
            var v = new Il2CppStructArray<Vector3>(n);
            var no = new Il2CppStructArray<Vector3>(n);
            var uv = new Il2CppStructArray<Vector2>(n);
            int i = 0;
            foreach (JsonElement e in verts.EnumerateArray())
                v[i++] = new Vector3(e[0].GetSingle(), e[1].GetSingle(), e[2].GetSingle());
            i = 0;
            foreach (JsonElement e in normals.EnumerateArray())
                no[i++] = new Vector3(e[0].GetSingle(), e[1].GetSingle(), e[2].GetSingle());
            i = 0;
            foreach (JsonElement e in uvs.EnumerateArray())
                uv[i++] = new Vector2(e[0].GetSingle(), e[1].GetSingle());

            var mesh = new Mesh { name = swap.MeshName + " (pt-BR)", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.vertices = v;
            mesh.normals = no;
            mesh.uv = uv;
            JsonElement subs = root.GetProperty("subs");
            mesh.subMeshCount = subs.GetArrayLength();
            int index = 0;
            foreach (JsonElement sub in subs.EnumerateArray())
            {
                var tris = new Il2CppStructArray<int>(sub.GetArrayLength());
                int k = 0;
                foreach (JsonElement t in sub.EnumerateArray())
                    tris[k++] = t.GetInt32();
                mesh.SetTriangles(tris, index++);
            }
            mesh.RecalculateBounds();
            mesh.RecalculateTangents();
            mesh.hideFlags = HideFlags.DontUnloadUnusedAsset;
            swap.Mesh = mesh;
            return mesh;
        }

        private static string PathOf(Transform t)
        {
            var parts = new List<string>();
            for (; t != null; t = t.parent)
                parts.Add(t.name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        public static void Apply()
        {
            if (!_loaded)
                Load();
            if (Swaps.Count == 0)
                return;

            // Letters inside a combined mesh can't be swapped: hide that renderer and add a new object.
            foreach (Swap swap in Swaps)
            {
                if (swap.MeshName.Length != 0)
                    continue;
                bool exists = false;
                foreach ((Renderer hidden, GameObject replacement) in Replaced)
                    if (hidden != null && replacement != null && PathOf(hidden.transform).EndsWith(swap.Where))
                        exists = true;
                if (exists)
                    continue;
                foreach (Renderer r in Resources.FindObjectsOfTypeAll<Renderer>())
                {
                    if (r == null || !r.gameObject.scene.IsValid() || !PathOf(r.transform).EndsWith(swap.Where))
                        continue;
                    var go = new GameObject("S1 Sign " + swap.Where);
                    go.AddComponent<MeshFilter>().sharedMesh = Build(swap);
                    go.AddComponent<MeshRenderer>().sharedMaterial = r.sharedMaterial;
                    UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(go, r.gameObject.scene);
                    Replaced.Add((r, go));
                    break;
                }
            }
            foreach ((Renderer hidden, GameObject replacement) in Replaced)
            {
                if (hidden != null)
                    hidden.enabled = !Translator.Enabled;
                if (replacement != null)
                    replacement.SetActive(Translator.Enabled);
            }

            foreach (MeshFilter f in Resources.FindObjectsOfTypeAll<MeshFilter>())
            {
                if (f == null || f.sharedMesh == null || !f.gameObject.scene.IsValid())
                    continue;
                string meshName = f.sharedMesh.name;
                foreach (Swap swap in Swaps)
                {
                    if (swap.MeshName.Length == 0 || meshName != swap.MeshName || !PathOf(f.transform).Contains(swap.Where))
                        continue;
                    int id = f.GetInstanceID();
                    if (!Originals.ContainsKey(id))
                    {
                        Originals[id] = f.sharedMesh;
                        Filters[id] = f;
                    }
                    if (Translator.Enabled)
                        f.sharedMesh = Build(swap);
                }
            }

            if (!Translator.Enabled)
                foreach (KeyValuePair<int, Mesh> o in Originals)
                    if (Filters[o.Key] != null)
                        Filters[o.Key].sharedMesh = o.Value;
        }
    }
}

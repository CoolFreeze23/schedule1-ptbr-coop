using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MelonLoader;

namespace S1CoopTalk
{
    /// <summary>
    /// Shared setup for the multiplayer repairs. Each patch installs on its own, so one that cannot attach after a game
    /// update does not take the others with it, and any that failed are named in the log.
    /// </summary>
    public static class Repairs
    {
        /// <summary>The host announced this mod, so messages only it understands are safe to send.</summary>
        public static bool HostHasMod;

        public const string MinHostVersion = "1.1.0";

        private static HarmonyLib.Harmony _harmony;
        private static int _active;
        private static readonly List<string> Failed = new List<string>();

        public static void Install(HarmonyLib.Harmony h, MelonPreferences_Category prefs)
        {
            _harmony = h;
            Lifecycle.Install();
            World.Install();
            if (prefs.CreateEntry("ItemRepairs", true, "Item repairs", "Fixes for duplicated and lost items. Every player needs the same setting.").Value)
                Items.Install();
            Mod.Log.Msg($"Multiplayer repairs active: {_active}" + (Failed.Count > 0 ? ", could not attach: " + string.Join(", ", Failed) : ""));
        }

        public static void Tick()
        {
            Lifecycle.Tick();
        }

        public static void OnSceneChanged()
        {
            HostHasMod = false;
            Lifecycle.Reset();
            World.Reset();
            Items.Reset();
        }

        /// <summary>Methods named exactly, or generated ones named name_hash.</summary>
        public static List<MethodInfo> Find(Type type, string name)
        {
            var found = new List<MethodInfo>();
            foreach (MethodInfo m in AccessTools.GetDeclaredMethods(type))
                if (m.Name == name || m.Name.StartsWith(name + "_"))
                    found.Add(m);
            return found;
        }

        public static void Patch(Type type, string name, Type holder, string prefix = null, string postfix = null, string finalizer = null)
        {
            List<MethodInfo> methods = Find(type, name);
            if (methods.Count == 0)
            {
                Failed.Add(type.Name + "." + name);
                return;
            }
            foreach (MethodInfo m in methods)
                PatchMethod(m, holder, prefix, postfix, finalizer);
        }

        public static void PatchMethod(MethodBase method, Type holder, string prefix = null, string postfix = null, string finalizer = null)
        {
            try
            {
                _harmony.Patch(method,
                    prefix: prefix != null ? new HarmonyMethod(holder, prefix) : null,
                    postfix: postfix != null ? new HarmonyMethod(holder, postfix) : null,
                    finalizer: finalizer != null ? new HarmonyMethod(holder, finalizer) : null);
                _active++;
            }
            catch (Exception e)
            {
                Failed.Add(method.DeclaringType?.Name + "." + method.Name);
                Mod.Log.Warning($"Could not attach to {method.Name}: {e.Message}");
            }
        }

        public static bool VersionAtLeast(string tag, string minimum)
        {
            string number = tag.Split('/')[0];
            return Version.TryParse(number, out Version theirs) && Version.TryParse(minimum, out Version needed) && theirs >= needed;
        }
    }
}

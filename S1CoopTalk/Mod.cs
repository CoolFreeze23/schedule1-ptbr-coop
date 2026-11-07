using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Il2CppScheduleOne.Dialogue;
using Il2CppScheduleOne.Economy;
using Il2CppScheduleOne.Networking;
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.Persistence;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.Quests;
using Il2CppScheduleOne.ScriptableObjects;
using Il2CppScheduleOne.UI;
using Il2CppScheduleOne.UI.Phone;
using MelonLoader;
using UnityEngine;
using UnityEngine.InputSystem;

[assembly: MelonInfo(typeof(S1CoopTalk.Mod), "S1 Co-op Talk", "1.1.0", "Alvin")]
[assembly: MelonGame("TVGS", "Schedule I")]

namespace S1CoopTalk
{
    /// <summary>Shares what each player sees privately: NPC conversations, phone calls, sales and trouble.</summary>
    public class Mod : MelonMod
    {
        public static MelonLogger.Instance Log;
        public static MelonPreferences_Entry<bool> Echo;
        private static MelonPreferences_Entry<bool> _bubbles;
        private static MelonPreferences_Entry<string> _historyKey;
        private static MelonPreferences_Entry<string> _pingKey;
        private static MelonPreferences_Entry<string> _chatKey;
        public static string HistoryKeyName => _historyKey.Value;
        private const string ModVersion = "1.1.0";

        // Set while showing a line that came from another player, so it is not sent back out.
        private static bool _receiving;
        private static readonly Dictionary<string, float> LastBubble = new Dictionary<string, float>();
        private static readonly HashSet<string> ModdedPlayers = new HashSet<string>();
        private static float _nextPoll;
        private static bool _wasArrested, _announced, _warned;
        private static float _inGameSince = -1f;

        public override void OnInitializeMelon()
        {
            Log = LoggerInstance;
            MelonPreferences_Category cat = MelonPreferences.CreateCategory("S1CoopTalk");
            _bubbles = cat.CreateEntry("ShowBubbles", true, "Speech bubbles", "Show what the NPC said to the other player above the NPC's head.");
            _historyKey = cat.CreateEntry("HistoryKey", "F8", "History key", "Key that opens the shared conversation history.");
            _pingKey = cat.CreateEntry("PingKey", "F7", "Ping key", "Marks the spot you are looking at for the other players.");
            _chatKey = cat.CreateEntry("ChatKey", "Y", "Chat key", "Opens the text chat. Enter sends, Esc cancels.");
            Echo = cat.CreateEntry("Echo", false, "Echo (testing)", "Also shows your own lines in the feed.");

            HarmonyLib.Harmony h = HarmonyInstance;
            Patch(h, typeof(DialogueCanvas), nameof(DialogueCanvas.DisplayDialogueNode), nameof(OnDialogueNode));
            Patch(h, typeof(DialogueHandler), nameof(DialogueHandler.ChoiceSelected), nameof(OnChoice));
            Patch(h, typeof(WorldspaceDialogueRenderer), nameof(WorldspaceDialogueRenderer.ShowText), nameof(OnBubble));
            Patch(h, typeof(CallInterface), nameof(CallInterface.StartCall), nameof(OnCallStart));
            Patch(h, typeof(CallInterface), "ShowStage", nameof(OnCallStage));
            Patch(h, typeof(Customer), nameof(Customer.ProcessHandover), nameof(OnHandover));
            MethodInfo save = AccessTools.Method(typeof(SaveManager), nameof(SaveManager.Save), new[] { typeof(string) });
            if (save != null)
                h.Patch(save, prefix: new HarmonyMethod(typeof(Mod), nameof(OnSave)));
            Patch(h, typeof(DailySummary), nameof(DailySummary.Open), nameof(OnDayEnd));
            Net.Install(h);
            Sync.Install(h);
            Repairs.Install(h, MelonPreferences.GetCategory("S1CoopTalk"));
#if DEVUDP
            DevLink.Start();
#endif
        }

        public override void OnLateInitializeMelon() => Text.Connect();

        private static void Patch(HarmonyLib.Harmony h, Type type, string method, string patch)
        {
            try
            {
                MethodInfo target = AccessTools.Method(type, method);
                if (target == null)
                {
                    Log.Warning($"{type.Name}.{method} not found, that part of the sync is off");
                    return;
                }
                h.Patch(target, prefix: new HarmonyMethod(typeof(Mod), patch));
            }
            catch (Exception e)
            {
                Log.Error($"Failed to patch {type.Name}.{method}: {e.Message}");
            }
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            bool inGame = sceneName == "Main" || sceneName == "Tutorial";
            _inGameSince = inGame ? Time.unscaledTime : -1f;
            _announced = _warned = false;
            ModdedPlayers.Clear();
            Team.Reset();
            Sync.OnSceneChanged(inGame);
            Repairs.OnSceneChanged();
            if (inGame)
                Feed.Ensure();
        }

        public override void OnUpdate()
        {
            Feed.Tick();
            Repairs.Tick();
            if (_inGameSince < 0f)
                return;

            Chat.Tick();
            Team.Tick();
            Sync.Tick();
            World.Tick();
            Items.Tick();
#if DEVUDP
            DevLink.Tick();
#endif
            if (Chat.IsOpen || Il2CppScheduleOne.GameInput.IsTyping)
                return;

            if (WasPressed(_historyKey.Value))
                Feed.ToggleHistory();
            if (WasPressed(_pingKey.Value))
                Team.SendPing(IsShiftHeld());
            if (WasPressed(_chatKey.Value))
                Chat.Toggle();

            if (Time.unscaledTime >= _nextPoll)
            {
                _nextPoll = Time.unscaledTime + 1f;
                PollLocalPlayer();
                Team.PollLocal();
                Team.PollRoster();
            }
        }

        private static bool IsShiftHeld()
        {
            Keyboard kb = Keyboard.current;
            return kb != null && kb.shiftKey.isPressed;
        }

        private static bool WasPressed(string keyName)
        {
            Keyboard kb = Keyboard.current;
            if (kb == null || !Enum.TryParse(keyName, true, out Key key) || key == Key.None)
                return false;
            return kb[key].wasPressedThisFrame;
        }

        // ---- Things this player does ----

        private static string NpcId(NPC npc) => npc != null ? npc.ID : "";
        private static string NpcName(NPC npc) => npc != null ? npc.FullName : "";

        private static void OnDialogueNode(DialogueHandler diag, string dialogueText)
        {
            if (string.IsNullOrWhiteSpace(dialogueText) || diag == null)
                return;
            NPC npc = diag.NPC;
            Net.Send(Net.Line, Net.LocalName, NpcId(npc), NpcName(npc), Text.ToEnglish(dialogueText));
        }

        private static void OnChoice(DialogueHandler __instance, int choiceIndex)
        {
            var choices = __instance.CurrentChoices;
            if (choices == null || choiceIndex < 0 || choiceIndex >= choices.Count)
                return;
            NPC npc = __instance.NPC;
            Net.Send(Net.Choice, Net.LocalName, NpcId(npc), NpcName(npc), Text.ToEnglish(choices[choiceIndex].ChoiceText));
        }

        private static void OnBubble(WorldspaceDialogueRenderer __instance, string text)
        {
            if (_receiving || string.IsNullOrWhiteSpace(text) || Player.Local == null)
                return;
            NPC npc = __instance.GetComponentInParent<NPC>();
            if (npc == null || Vector3.Distance(npc.transform.position, Player.Local.transform.position) > 12f)
                return;

            string key = npc.ID + text;
            if (LastBubble.TryGetValue(key, out float at) && Time.unscaledTime - at < 4f)
                return;
            LastBubble[key] = Time.unscaledTime;
            if (LastBubble.Count > 200)
                LastBubble.Clear();
            Net.Send(Net.Bubble, Net.LocalName, npc.ID, npc.FullName, Text.ToEnglish(text));
        }

        private static void OnCallStart(PhoneCallData data, CallerID caller)
        {
            string name = caller != null ? caller.Name : data?.CallerID?.Name;
            Net.Send(Net.CallStart, Net.LocalName, "", name ?? "", "");
        }

        private static void OnCallStage(CallInterface __instance, int stageIndex)
        {
            PhoneCallData data = __instance.ActiveCallData;
            if (data == null || data.Stages == null || stageIndex < 0 || stageIndex >= data.Stages.Length)
                return;
            string name = data.CallerID != null ? data.CallerID.Name : "";
            Net.Send(Net.CallLine, Net.LocalName, "", name, Text.ToEnglish(data.Stages[stageIndex].Text));
        }

        private static void OnHandover(Customer __instance, Contract contract, bool handoverByPlayer)
        {
            if (!handoverByPlayer || contract == null)
                return;
            NPC npc = __instance.NPC;
            string payment = contract.Payment.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Net.Send(Net.Sale, Net.LocalName, NpcId(npc), NpcName(npc), payment);
            if (!Echo.Value)
                Chat.CountSale(Net.LocalName, payment);
        }

        private static void PollLocalPlayer()
        {
            Player p = Player.Local;
            if (p == null)
                return;

            if (!_announced)
            {
                _announced = true;
                Net.Send(Net.Hello, Net.LocalName, HostMark, "", VersionTag);
            }

            bool arrested = p.IsArrested;
            if (arrested && !_wasArrested)
                Net.Send(Net.Arrested, Net.LocalName, "", "", "");
            _wasArrested = arrested;

            if (!_warned && _inGameSince >= 0f && Time.unscaledTime - _inGameSince > 45f)
            {
                _warned = true;
                int others = Player.PlayerList != null ? Player.PlayerList.Count - 1 : 0;
                if (others > ModdedPlayers.Count)
                    Feed.System(Text.Ui("missing_mod"));
            }
        }

        // ---- Things another player did ----

        public static void Receive(string[] f)
        {
            string type = f[0], who = f[1], npcId = f[2], npcName = f[3], body = f[4];
            string npcShown = Text.Display(npcName);

            switch (type)
            {
                case Net.SupplierDone:
                    World.HandleSupplierDone(npcId);
                    break;
                case Net.ItemBack:
                    Items.HandleItemBack(who, body);
                    break;
                case Net.Hello:
                    NoteHost(npcId, body);
                    CheckVersion(who, body);
                    bool isNew = ModdedPlayers.Add(who);
                    if (isNew)
                        Feed.System(string.Format(Text.Ui("joined"), who));
                    // Late joiners also need to hear about the players already here. Someone rejoining is not new to
                    // the host, but has forgotten everything, including that the host has this mod.
                    if (isNew || Il2CppFishNet.InstanceFinder.IsServer)
                        Net.Send(Net.HelloBack, Net.LocalName, HostMark, "", VersionTag);
                    break;
                case Net.HelloBack:
                    NoteHost(npcId, body);
                    CheckVersion(who, body);
                    ModdedPlayers.Add(who);
                    break;
                case Net.ActivityEnd:
                    Team.ClearActivity(who);
                    break;
                case Net.WantedOn:
                    Team.SetWanted(who, true);
                    Feed.Alert(string.Format(Text.Ui("wanted"), who));
                    break;
                case Net.WantedOff:
                    Team.SetWanted(who, false);
                    Feed.System(string.Format(Text.Ui("escaped"), who));
                    break;
                case Net.PingMark:
                    Team.ReceivePing(who, body);
                    break;
                case Net.ResyncAsk:
                    Sync.HandleAsk(who, body);
                    break;
                case Net.ChatLine:
                    Chat.ShowChat(who, body);
                    break;
                case Net.CashSend:
                    Chat.ReceiveCash(who, npcId, npcName, body);
                    break;
                case Net.CashAck:
                    Chat.ReceiveAck(npcId, npcName);
                    break;
                case Net.Saved:
                    Feed.System(Text.Ui("saved"));
                    break;
                case Net.Line:
                    Team.SetActivity(who, string.Format(Text.Ui("st_talking"), npcShown));
                    Feed.Speech(string.Format(Text.Ui("npc_to"), npcShown, who), Text.Display(body), Feed.NpcColor);
                    ShowBubble(npcId, body);
                    break;
                case Net.Choice:
                    Feed.Speech(who, Text.Display(body), Team.HexOf(who));
                    break;
                case Net.Bubble:
                    ShowBubble(npcId, body);
                    break;
                case Net.CallStart:
                    Team.SetActivity(who, string.Format(Text.Ui("st_phone"), npcShown));
                    Feed.System(string.Format(Text.Ui("call_start"), who, npcShown));
                    break;
                case Net.CallLine:
                    Team.SetActivity(who, string.Format(Text.Ui("st_phone"), npcShown));
                    Feed.Speech(string.Format(Text.Ui("call_line"), npcShown, who), Text.Display(body), Feed.PhoneColor);
                    break;
                case Net.Sale:
                    Chat.CountSale(who, body);
                    Feed.System(string.Format(Text.Ui("sale"), who, npcShown, Text.Display("$" + FormatMoney(body))));
                    break;
                case Net.Arrested:
                    Feed.Alert(string.Format(Text.Ui("arrested"), who));
                    break;
                case Net.Died:
                    Feed.Alert(string.Format(Text.Ui("died"), who));
                    break;
            }
        }

        private static void OnDayEnd() => Chat.ShowScoreboard();

        private static string VersionTag => ModVersion + "/" + Application.version;
        private static string HostMark => Il2CppFishNet.InstanceFinder.IsServer ? "host" : "";

        private static void NoteHost(string mark, string tag)
        {
            if (mark == "host" && Repairs.VersionAtLeast(tag, Repairs.MinHostVersion))
                Repairs.HostHasMod = true;
        }

        private static readonly HashSet<string> VersionWarned = new HashSet<string>();

        private static void CheckVersion(string who, string theirs)
        {
            if (!string.IsNullOrEmpty(theirs) && theirs != VersionTag && VersionWarned.Add(who))
                Feed.Alert(string.Format(Text.Ui("version"), who, VersionTag, theirs));
        }

        private static void OnSave()
        {
            if (PersistentSingleton<Lobby>.Instance != null && PersistentSingleton<Lobby>.Instance.IsHost)
                Net.Send(Net.Saved, Net.LocalName, "", "", "");
        }

        private static string FormatMoney(string amount)
        {
            return float.TryParse(amount, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v)
                ? Mathf.RoundToInt(v).ToString("#,0", System.Globalization.CultureInfo.InvariantCulture)
                : amount;
        }

        private static void ShowBubble(string npcId, string body)
        {
            if (!_bubbles.Value || string.IsNullOrEmpty(npcId))
                return;
            try
            {
                NPC npc = NPCManager.GetNPC(npcId);
                WorldspaceDialogueRenderer rend = npc?.DialogueHandler?.WorldspaceRend;
                if (rend == null || npc.DialogueHandler.IsDialogueInProgress)
                    return;
                _receiving = true;
                rend.ShowText(Text.Display(Text.StripForBubble(body)), Mathf.Clamp(body.Length * 0.06f, 3f, 8f));
            }
            catch (Exception e)
            {
                Log.Warning("Bubble failed: " + e.Message);
            }
            finally
            {
                _receiving = false;
            }
        }
    }
}

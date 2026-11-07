using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using Il2CppFishNet;
using Il2CppFishNet.Connection;
using Il2CppFishNet.Object;
using Il2CppInterop.Runtime;
using Il2CppScheduleOne;
using Il2CppScheduleOne.Delivery;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Economy;
using Il2CppScheduleOne.EntityFramework;
using Il2CppScheduleOne.ItemFramework;
using Il2CppScheduleOne.Levelling;
using Il2CppScheduleOne.Management;
using Il2CppScheduleOne.Messaging;
using Il2CppScheduleOne.Networking;
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.ObjectScripts;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.StationFramework;
using Il2CppScheduleOne.Storage;
using Il2CppScheduleOne.UI.Phone.Messages;
using MelonLoader.Utils;
using UnityEngine;

namespace S1CoopTalk
{
    /// <summary>
    /// Repairs the state a joining player can lose. FishNet's client parser drops the rest of a packet when
    /// anything in it throws, and that data is never sent again, which leaves placed items uninitialized (missing
    /// or misplaced machines, stations nobody can configure). So: stop the known throws on clients, re-send item
    /// setup from the host (clients ignore what they already have), and clear management locks nobody holds.
    /// </summary>
    public static class Sync
    {
        private const string ParseError = "encountered an error while parsing data";
        private const float AuditDelay = 20f, AuditInterval = 60f, HostInterval = 3f, ResyncCooldown = 45f;
        private const int ApproxBytes = 256;

        private static readonly Dictionary<string, float> LastResync = new Dictionary<string, float>();
        private static readonly HashSet<string> Served = new HashSet<string>();
        private static string _logPath;
        private static float _inGameSince = -1f, _nextHost, _nextAudit;
        private static int _parseErrors, _parseErrorsAnswered, _guardedGrid, _guardedConfig, _lastReported = -1;
        private static bool _hooked;

        public static void Install(HarmonyLib.Harmony h)
        {
            _logPath = Path.Combine(MelonEnvironment.UserDataDirectory, "S1CoopTalk_sync.log");
            GuardAll(h, typeof(GridItem), "RpcLogic___InitializeGridItem_Client", nameof(GuardGrid));
            GuardAll(h, typeof(ConfigurationReplicator), "RpcLogic___Receive", nameof(GuardConfig));
            InstallTargetedSettings(h);
        }

        // ---- Host: settings for one player go to that player ----
        //
        // ConfigurationReplicator.ReplicateField takes a connection and ignores it: every field goes through a ServerRpc
        // that broadcasts to all observers. Each join therefore re-sent every setting of every station to everyone,
        // overwriting edits in progress. While a targeted call runs, its ServerRpc is turned into the matching
        // ObserversRpc (registered right after it, with the same payload) addressed to that connection alone.

        private static NetworkConnection _settingsTarget;
        private static int _targetedFields, _configReceived;

        private static void InstallTargetedSettings(HarmonyLib.Harmony h)
        {
            try
            {
                h.Patch(AccessTools.Method(typeof(ConfigurationReplicator), nameof(ConfigurationReplicator.ReplicateField)),
                    prefix: new HarmonyMethod(typeof(Sync), nameof(BeginTargetedField)),
                    finalizer: new HarmonyMethod(typeof(Sync), nameof(EndTargetedField)));
                h.Patch(AccessTools.Method(typeof(NetworkBehaviour), nameof(NetworkBehaviour.SendServerRpc)),
                    prefix: new HarmonyMethod(typeof(Sync), nameof(RedirectServerRpc)));
            }
            catch (Exception e)
            {
                Mod.Log.Warning($"Settings still broadcast on join: {e.Message}");
            }
        }

        private static void BeginTargetedField(NetworkConnection conn)
        {
            if (conn != null && !conn.IsLocalClient && InstanceFinder.IsServer)
                _settingsTarget = conn;
        }

        private static Exception EndTargetedField(Exception __exception)
        {
            _settingsTarget = null;
            return __exception;
        }

        private static bool RedirectServerRpc(NetworkBehaviour __instance, uint hash, Il2CppFishNet.Serializing.PooledWriter methodWriter,
            Il2CppFishNet.Transporting.Channel channel, DataOrderType orderType)
        {
            if (_settingsTarget == null)
                return true;
            NetworkObject networkObject = __instance.NetworkObject;
            var everyone = networkObject.Observers;
            var only = new Il2CppSystem.Collections.Generic.HashSet<NetworkConnection>();
            only.Add(_settingsTarget);
            networkObject.Observers = only;
            try
            {
                __instance.SendObserversRpc(hash + 1, methodWriter, channel, orderType, false, false, false);
                _targetedFields++;
            }
            finally
            {
                networkObject.Observers = everyone;
            }
            return false;
        }

        private static void GuardAll(HarmonyLib.Harmony h, Type type, string prefix, string patch)
        {
            int n = 0;
            foreach (MethodInfo m in AccessTools.GetDeclaredMethods(type))
            {
                if (!m.Name.StartsWith(prefix))
                    continue;
                try
                {
                    h.Patch(m, prefix: new HarmonyMethod(typeof(Sync), patch));
                    n++;
                }
                catch (Exception e)
                {
                    Mod.Log.Warning($"Could not guard {m.Name}: {e.Message}");
                }
            }
            if (n == 0)
                Mod.Log.Warning($"No {prefix} methods found on {type.Name}");
        }

        public static void OnSceneChanged(bool inGame)
        {
            _inGameSince = inGame ? Time.unscaledTime : -1f;
            _nextAudit = Time.unscaledTime + AuditDelay;
            _lastReported = -1;
            _parseErrors = _parseErrorsAnswered = _guardedGrid = _guardedConfig = 0;
            Served.Clear();
            LastResync.Clear();
            if (inGame && !_hooked)
            {
                _hooked = true;
                Application.add_logMessageReceived(DelegateSupport.ConvertDelegate<Application.LogCallback>(new Action<string, string, LogType>(OnUnityLog)));
            }
        }

        public static void Note(string line)
        {
            try
            {
                File.AppendAllText(_logPath, $"{DateTime.Now:HH:mm:ss} {line}\n");
            }
            catch
            {
                // Diagnostics only.
            }
        }

        private static void OnUnityLog(string message, string stack, LogType type)
        {
            if (message == null || !message.Contains(ParseError))
                return;
            _parseErrors++;
            Note("PARSE ERROR: " + message);
            // Something was just lost; look for it soon rather than waiting for the next audit.
            _nextAudit = Mathf.Min(_nextAudit, Time.unscaledTime + 5f);
        }

        // ---- Client guards: no throw, so the rest of the packet survives ----

        private static bool GuardGrid(string __2)
        {
            try
            {
                if (global::Il2Cpp.GUIDManager.GetObject<Il2CppScheduleOne.Tiles.Grid>(new Il2CppSystem.Guid(__2)) != null)
                    return true;
            }
            catch (Exception e)
            {
                Note("Grid lookup failed for " + __2 + ": " + e.Message);
            }
            _guardedGrid++;
            Note("Skipped item setup, unknown grid " + __2);
            return false;
        }

        private static bool GuardConfig(ConfigurationReplicator __instance)
        {
            _configReceived++;
            if (__instance.Configuration != null)
                return true;
            _guardedConfig++;
            return false;
        }

        // ---- Regular work ----

        public static void Tick()
        {
            if (_inGameSince < 0f || Player.Local == null)
                return;

            if (InstanceFinder.IsServer)
            {
                if (Time.unscaledTime >= _nextHost)
                {
                    _nextHost = Time.unscaledTime + HostInterval;
                    ServeJoiners();
                    ClearStaleConfigurers();
                    if (_targetedFields > 0)
                    {
                        Note($"Sent {_targetedFields} settings to joining players only; settings received here meanwhile: {_configReceived}");
                        _targetedFields = _configReceived = 0;
                    }
                }
            }
            else if (Time.unscaledTime >= _nextAudit)
            {
                _nextAudit = Time.unscaledTime + AuditInterval;
                Audit();
            }
        }

        // ---- Client: look for items whose setup never arrived ----

        private static int CountUninitialized()
        {
            int n = 0;
            foreach (BuildableItem item in Resources.FindObjectsOfTypeAll<BuildableItem>())
                if (item != null && item.gameObject.scene.IsValid() && !item.IsDestroyed && !item.Initialized && item.IsSpawned)
                    n++;
            return n;
        }

        private static void Audit()
        {
            int missing = CountUninitialized();
            bool lostData = _parseErrors > _parseErrorsAnswered;
            _parseErrorsAnswered = _parseErrors;
            bool worthAsking = missing > 0 || _guardedConfig > 0 || lostData;
            if (missing != _lastReported || _guardedConfig > 0)
                Note($"Audit: uninitialized={missing} parseErrors={_parseErrors} guardedGrid={_guardedGrid} guardedConfig={_guardedConfig}");
            if (worthAsking)
            {
                Net.Send(Net.ResyncAsk, Net.LocalName, "", "", lostData ? "all" : _guardedConfig > 0 ? "cfg" : "");
                if (missing != _lastReported)
                    Feed.System(string.Format(Text.Ui("sync_asked"), missing));
            }
            else if (_lastReported > 0)
            {
                Feed.System(Text.Ui("sync_done"));
            }
            _lastReported = missing;
            _guardedConfig = 0;
        }

        /// <summary>"/sync" in the chat: ask right away.</summary>
        public static void RequestNow()
        {
            if (InstanceFinder.IsServer)
            {
                foreach (Player p in Player.PlayerList)
                    if (p != null && !p.IsLocalPlayer)
                        Resync(p, true, true);
                return;
            }
            Net.Send(Net.ResyncAsk, Net.LocalName, "", "", "all");
            Feed.System(string.Format(Text.Ui("sync_asked"), CountUninitialized()));
        }

        // ---- Host ----

        private static void ServeJoiners()
        {
            foreach (Player p in Player.PlayerList)
            {
                if (p == null || p.IsLocalPlayer || !p.PlayerLoaded)
                    continue;
                if (Served.Add(p.PlayerName))
                {
                    Resync(p, false);
                    SendJoinState(p);
                }
            }
        }

        // Values the game sends a joiner once, or never: a dealer's recommendation, and relationships of locked NPCs.
        private static void SendJoinState(Player player)
        {
            NetworkConnection conn = player.Connection;
            if (conn == null)
                return;
            foreach (Dealer dealer in Dealer.AllPlayerDealers)
                if (dealer != null && dealer.HasBeenRecommended)
                    dealer.SetRecommended();
            foreach (NPC npc in NPCManager.NPCRegistry)
                if (npc != null && npc.RelationData != null && !npc.RelationData.Unlocked)
                    npc.ReceiveRelationshipData(conn, npc.RelationData.RelationDelta, false);
        }

        // Everything else that is sent once at join. Safe to repeat: unlocks only add, known deliveries are skipped, and
        // conversations and slots rebuild from the host's values.
        private static void ResendEverything(NetworkConnection conn)
        {
            int conversations = 0, slots = 0;
            void Queue(string task, Action<NetworkConnection> send, int bytes) =>
                ReplicationQueue.Enqueue(task, conn, DelegateSupport.ConvertDelegate<Il2CppSystem.Action<NetworkConnection>>(send), bytes);

            LevelManager level = NetworkSingleton<LevelManager>.InstanceExists ? NetworkSingleton<LevelManager>.Instance : null;
            if (level != null)
                Queue("S1 rank", c =>
                {
                    level.SetData(c, level.Rank, level.Tier, level.XP, level.TotalXP);
                    level.SetUnlockedRegions(c, Singleton<Il2CppScheduleOne.Map.Map>.Instance.GetUnlockedRegions());
                }, ApproxBytes);

            foreach (NPC npc in NPCManager.NPCRegistry)
            {
                NPC n = npc;
                if (n == null || n.RelationData == null)
                    continue;
                Queue("S1 relationship", c => n.ReceiveRelationshipData(c, n.RelationData.RelationDelta, n.RelationData.Unlocked), 32);
            }

            foreach (Customer customer in Resources.FindObjectsOfTypeAll<Customer>())
            {
                Customer cu = customer;
                if (cu == null || !cu.gameObject.scene.IsValid())
                    continue;
                Queue("S1 customer", c => cu.ReceiveCustomerData(c, cu.GetCustomerData()), ApproxBytes);
            }

            MessagingManager messaging = NetworkSingleton<MessagingManager>.InstanceExists ? NetworkSingleton<MessagingManager>.Instance : null;
            if (messaging != null)
                foreach (MSGConversation conversation in MessagesApp.Conversations)
                {
                    MSGConversation m = conversation;
                    if (m == null || !m.ShouldReplicate())
                        continue;
                    conversations++;
                    Queue("S1 conversation", c => messaging.ReceiveMSGConversationData(c, m.ConversationId, m.GetSaveData()), 2048);
                }

            if (NetworkSingleton<DeliveryManager>.InstanceExists)
            {
                DeliveryManager deliveries = NetworkSingleton<DeliveryManager>.Instance;
                foreach (DeliveryInstance delivery in deliveries.Deliveries)
                {
                    DeliveryInstance d = delivery;
                    Queue("S1 delivery", c => deliveries.ReceiveDelivery(c, d), ApproxBytes);
                }
            }

            foreach (Il2CppSystem.Type type in new[]
            {
                Il2CppType.Of<StorageEntity>(), Il2CppType.Of<MixingStation>(), Il2CppType.Of<DryingRack>(), Il2CppType.Of<PackagingStation>(),
                Il2CppType.Of<BrickPress>(), Il2CppType.Of<Cauldron>(), Il2CppType.Of<ChemistryStation>(), Il2CppType.Of<LabOven>(),
                Il2CppType.Of<MushroomSpawnStation>(),
            })
                foreach (UnityEngine.Object found in Resources.FindObjectsOfTypeAll(type))
                {
                    NetworkBehaviour behaviour = found.TryCast<NetworkBehaviour>();
                    IItemSlotOwner owner = behaviour != null && behaviour.IsSpawned ? behaviour.TryCast<IItemSlotOwner>() : null;
                    if (owner == null || owner.ItemSlots == null)
                        continue;
                    for (int i = 0; i < owner.ItemSlots.Count; i++)
                    {
                        int index = i;
                        ItemSlot slot = owner.ItemSlots[i];
                        slots++;
                        Queue("S1 slot", c => owner.SetStoredInstance(c, index, slot.ItemInstance), 128);
                    }
                }

            Note($"Re-sent rank, NPCs, {conversations} conversations and {slots} slots");
        }

        public static void HandleAsk(string who, string body)
        {
            if (!InstanceFinder.IsServer)
                return;
            foreach (Player p in Player.PlayerList)
                if (p != null && p.PlayerName == who && !p.IsLocalPlayer)
                    Resync(p, body == "cfg" || body == "all", body == "all");
        }

        private static void Resync(Player player, bool configuration, bool everything = false)
        {
            string name = player.PlayerName;
            if (LastResync.TryGetValue(name, out float at) && Time.unscaledTime - at < ResyncCooldown)
                return;
            LastResync[name] = Time.unscaledTime;

            NetworkConnection conn = player.Connection;
            if (conn == null)
                return;

            int items = 0, configs = 0;
            foreach (BuildableItem item in Resources.FindObjectsOfTypeAll<BuildableItem>())
            {
                if (item == null || !item.gameObject.scene.IsValid() || !item.Initialized || item.IsDestroyed || !item.IsSpawned)
                    continue;
                BuildableItem captured = item;
                ReplicationQueue.Enqueue("S1 resync", conn, DelegateSupport.ConvertDelegate<Il2CppSystem.Action<NetworkConnection>>(
                    new Action<NetworkConnection>(c =>
                    {
                        if (captured != null)
                            captured.SendInitializationToClient(c);
                    })), ApproxBytes);
                items++;
            }

            if (configuration)
                foreach (Il2CppScheduleOne.Property.Property property in Resources.FindObjectsOfTypeAll<Il2CppScheduleOne.Property.Property>())
                {
                    if (property == null || property.Configurables == null)
                        continue;
                    foreach (IConfigurable configurable in property.Configurables)
                    {
                        configurable.SendConfigurationToClient(conn);
                        configs++;
                    }
                }

            if (everything)
                ResendEverything(conn);
            Note($"Resync for {name}: items={items} configs={configs} everything={everything}");
        }

        // A player who left, or whose "done" got lost, must not keep a station locked for everyone.
        private static void ClearStaleConfigurers()
        {
            foreach (Il2CppScheduleOne.Property.Property property in Resources.FindObjectsOfTypeAll<Il2CppScheduleOne.Property.Property>())
            {
                if (property == null || property.Configurables == null)
                    continue;
                foreach (IConfigurable configurable in property.Configurables)
                {
                    NetworkObject holder = configurable.CurrentPlayerConfigurer;
                    if (holder == null)
                        continue;
                    bool present = false;
                    foreach (Player p in Player.PlayerList)
                        if (p != null && p.NetworkObject == holder)
                            present = true;
                    if (present)
                        continue;
                    configurable.SetConfigurer(null);
                    Note("Cleared a management lock left by a player who is gone");
                }
            }
        }
    }
}

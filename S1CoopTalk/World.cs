using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using Il2CppFishNet;
using Il2CppFishNet.Connection;
using Il2CppFishNet.Object;
using Il2CppScheduleOne.Delivery;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Economy;
using Il2CppScheduleOne.Employees;
using Il2CppScheduleOne.Management;
using Il2CppScheduleOne.Messaging;
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.NPCs.Behaviour;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.UI.Phone.Delivery;
using Il2CppScheduleOne.Vehicles;
using Il2CppSteamworks;
using UnityEngine;

namespace S1CoopTalk
{
    /// <summary>NPCs, deliveries, phones and vehicles: shared state that one lost message would leave wrong for good.</summary>
    internal static class World
    {
        private const float HostInterval = 5f, ClientInterval = 10f, RaceWindow = 2f, EmptyOwnerLimit = 3f;
        private const int SendBufferBytes = 8 * 1024 * 1024;

        private static float _nextHost, _nextClient, _enteredAt = -1f;
        private static LandVehicle _entered;
        private static bool _loadingConversation, _bufferSet;
        private static int _sendFailures;
        private static readonly HashSet<string> DialogueSeen = new HashSet<string>();
        private static readonly Dictionary<IntPtr, float> EmptyOwnedSince = new Dictionary<IntPtr, float>();

        public static void Install()
        {
            Type self = typeof(World);
            Repairs.Patch(typeof(DeliveryApp), "DeliveryCompleted", self, prefix: nameof(IgnoreUnknownDelivery));
            foreach (Type t in new[] { typeof(Employee), typeof(Botanist), typeof(Chemist), typeof(Cleaner), typeof(Packager) })
                Repairs.Patch(t, nameof(Employee.OnSpawnServer), self, prefix: nameof(SkipUnassignedEmployee));
            Repairs.Patch(typeof(MSGConversation), nameof(MSGConversation.Load), self, prefix: nameof(BeginConversationLoad), finalizer: nameof(EndConversationLoad));
            Repairs.Patch(typeof(MSGConversation), nameof(MSGConversation.ShowResponses), self, prefix: nameof(KeepResponsesLocalWhileLoading));
            Repairs.Patch(typeof(Supplier), "MinPass", self, prefix: nameof(BeforeSupplierMinute), postfix: nameof(AfterSupplierMinute));
            Repairs.Patch(typeof(LandVehicle), "OnLocalPlayerEnter", self, postfix: nameof(AfterVehicleEnter));
            GuardRecipeClosure();
            WatchSocketSends();
        }

        public static void Reset()
        {
            DialogueSeen.Clear();
            EmptyOwnedSince.Clear();
            _entered = null;
            _enteredAt = -1f;
        }

        public static void Tick()
        {
            try
            {
                RaiseSendBuffer();
                CheckSeatRace();
                if (InstanceFinder.IsServer)
                {
                    if (Time.unscaledTime >= _nextHost)
                    {
                        _nextHost = Time.unscaledTime + HostInterval;
                        ReleaseAbandonedDialogue();
                        ReleaseEmptyOwnedVehicles();
                    }
                }
                else if (Time.unscaledTime >= _nextClient)
                {
                    _nextClient = Time.unscaledTime + ClientInterval;
                    RestoreArrivedVans();
                    EnsureDealerEntries();
                }
            }
            catch (Exception e)
            {
                Sync.Note("World repairs: " + e.Message);
            }
        }

        // ---- Client guards ----

        // A completion for a delivery this client never got throws, and the throw loses the rest of the packet.
        private static bool IgnoreUnknownDelivery(DeliveryInstance __0)
        {
            if (__0 != null)
                return true;
            Sync.Note("Ignored completion of an unknown delivery");
            return false;
        }

        // ReceiveRecipeField is the one settings message compiled into its reader, so the guard on its logic never runs.
        private static void GuardRecipeClosure()
        {
            foreach (Type nested in typeof(ConfigurationReplicator).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                foreach (MethodInfo m in AccessTools.GetDeclaredMethods(nested))
                    if (m.Name.Contains("ReceiveRecipeField") && m.Name.Contains("Do"))
                        Repairs.PatchMethod(m, typeof(World), prefix: nameof(GuardRecipeField));
        }

        private static bool GuardRecipeField(object __instance)
        {
            var field = AccessTools.Property(__instance.GetType(), "__4__this");
            var replicator = field?.GetValue(__instance) as ConfigurationReplicator;
            return replicator == null || replicator.Configuration != null;
        }

        // Loading a conversation on a joiner re-sent its pending replies to everyone, reviving offers already gone.
        private static void BeginConversationLoad() => _loadingConversation = true;

        private static Exception EndConversationLoad(Exception __exception)
        {
            _loadingConversation = false;
            return __exception;
        }

        private static void KeepResponsesLocalWhileLoading(ref bool __2)
        {
            if (_loadingConversation && !InstanceFinder.IsServer)
                __2 = false;
        }

        // ---- Host guards ----

        // Someone joining while a fired employee walks out hit a null property here, which kicked the joiner.
        private static bool SkipUnassignedEmployee(Employee __instance) => __instance.AssignedProperty != null;

        // A conversation's end runs on the talking player's machine, so a disconnect left the NPC talking to nobody,
        // unable to talk to anyone again, including people who join later.
        private static void ReleaseAbandonedDialogue()
        {
            foreach (NPC npc in NPCManager.NPCRegistry)
            {
                GenericDialogueBehaviour talk = npc != null && npc.Behaviour != null ? npc.Behaviour.GenericDialogueBehaviour : null;
                if (talk == null)
                    continue;
                string id = npc.ID;
                if (!talk.Enabled)
                {
                    DialogueSeen.Remove(id);
                    continue;
                }
                Player target = talk.targetPlayer;
                bool present = target != null && Player.PlayerList.Contains(target);
                if (present)
                    DialogueSeen.Add(id);
                else if (DialogueSeen.Remove(id))
                {
                    talk.Disable_Server();
                    Sync.Note("Released " + id + " from a conversation with someone who left");
                }
            }
        }

        // A fast exit and re-enter can leave a client owning an empty car: nobody simulates it, and it despawns for
        // everyone when that client leaves.
        private static void ReleaseEmptyOwnedVehicles()
        {
            VehicleManager manager = NetworkSingleton<VehicleManager>.InstanceExists ? NetworkSingleton<VehicleManager>.Instance : null;
            if (manager == null)
                return;
            foreach (LandVehicle v in manager.PlayerOwnedVehicles)
            {
                if (v == null)
                    continue;
                NetworkConnection owner = v.Owner;
                bool abandoned = owner != null && owner.IsValid && !owner.IsLocalClient && v.DriverPlayer == null;
                if (!abandoned)
                {
                    EmptyOwnedSince.Remove(v.Pointer);
                    continue;
                }
                if (!EmptyOwnedSince.TryGetValue(v.Pointer, out float since))
                    EmptyOwnedSince[v.Pointer] = Time.unscaledTime;
                else if (Time.unscaledTime - since >= EmptyOwnerLimit)
                {
                    EmptyOwnedSince.Remove(v.Pointer);
                    v.NetworkObject.RemoveOwnership();
                    Sync.Note("Released a vehicle owned by a player not driving it");
                }
            }
        }

        // A guest's supplier meeting ended only on their own clock, which restarted when they joined.
        private static void BeforeSupplierMinute(Supplier __instance, out bool __state) =>
            __state = InstanceFinder.IsServer && __instance.Status == Supplier.ESupplierStatus.Meeting;

        private static void AfterSupplierMinute(Supplier __instance, bool __state)
        {
            if (__state && __instance.Status != Supplier.ESupplierStatus.Meeting)
                Net.Send(Net.SupplierDone, Net.LocalName, __instance.ID, "", "");
        }

        public static void HandleSupplierDone(string id)
        {
            if (InstanceFinder.IsServer)
                return;
            Supplier supplier = NPCManager.GetNPC(id)?.TryCast<Supplier>();
            if (supplier != null && supplier.Status == Supplier.ESupplierStatus.Meeting)
                supplier.EndMeeting();
        }

        // ---- Client repairs ----

        // The van only appears through one message, so a lost arrival meant no van for this player.
        private static void RestoreArrivedVans()
        {
            if (!NetworkSingleton<DeliveryManager>.InstanceExists)
                return;
            foreach (DeliveryInstance d in NetworkSingleton<DeliveryManager>.Instance.Deliveries)
            {
                if (d == null || d.Status != EDeliveryStatus.Arrived)
                    continue;
                if (d.ActiveVehicle == null || !d.ActiveVehicle.Vehicle.IsVisible)
                {
                    d.SetStatus(EDeliveryStatus.Arrived);
                    Sync.Note("Restored the van of a delivery that had arrived");
                }
            }
        }

        // A conversation has no phone entry until something creates one. Suppliers do this on unlock; dealers do not.
        private static void EnsureDealerEntries()
        {
            foreach (Dealer dealer in Dealer.AllPlayerDealers)
                if (dealer != null && dealer.IsRecruited && dealer.MSGConversation != null)
                    dealer.MSGConversation.EnsureUIExists();
        }

        // ---- Seats ----

        // Seats are chosen from local state and the server never checks, so two players entering together both drove.
        // The loser's car shook against the winner's, and their exit then emptied the winner's seat.
        private static void AfterVehicleEnter(LandVehicle __instance)
        {
            if (InstanceFinder.IsServer)
                return;
            _entered = __instance;
            _enteredAt = Time.unscaledTime;
        }

        private static void CheckSeatRace()
        {
            if (_enteredAt < 0f)
                return;
            if (_entered == null || Time.unscaledTime - _enteredAt > RaceWindow)
            {
                _enteredAt = -1f;
                return;
            }
            VehicleSeat seat = _entered.localPlayerSeat;
            if (seat == null || seat.Occupant == null || seat.Occupant == Player.Local || _entered.IsOwner)
                return;
            _enteredAt = -1f;
            _entered.localPlayerSeat = null;
            _entered.ExitVehicle();
            Feed.System(Text.Ui("seat_taken"));
            Sync.Note("Lost a race for a vehicle seat, got out");
        }

        // ---- Steam sends ----

        // Steam refuses a reliable send when its buffer is full, and the transport drops the data with no retry.
        private static void RaiseSendBuffer()
        {
            if (_bufferSet || !global::Il2Cpp.SteamManager.Initialized)
                return;
            _bufferSet = true;
            int size = SendBufferBytes;
            GCHandle pin = GCHandle.Alloc(size, GCHandleType.Pinned);
            try
            {
                bool ok = SteamNetworkingUtils.SetConfigValue(
                    ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendBufferSize,
                    ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Global, IntPtr.Zero,
                    ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32, pin.AddrOfPinnedObject());
                Sync.Note($"Steam send buffer raised to {SendBufferBytes / 1024 / 1024} MB: {ok}");
            }
            finally
            {
                pin.Free();
            }
        }

        private static void WatchSocketSends()
        {
            Type socket = AccessTools.TypeByName("Il2CppFishySteamworks.CommonSocket");
            MethodInfo send = socket != null ? AccessTools.Method(socket, "Send") : null;
            if (send != null)
                Repairs.PatchMethod(send, typeof(World), postfix: nameof(AfterSocketSend));
        }

        private static void AfterSocketSend(EResult __result)
        {
            if (__result == EResult.k_EResultOK)
                return;
            _sendFailures++;
            if (_sendFailures <= 20 || _sendFailures % 100 == 0)
                Sync.Note($"Steam refused a send: {__result} (total {_sendFailures})");
        }
    }
}

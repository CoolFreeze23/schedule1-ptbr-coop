using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Il2CppFishNet;
using Il2CppFishNet.Connection;
using Il2CppFishNet.Object;
using Il2CppInterop.Runtime;
using Il2CppScheduleOne;
using Il2CppScheduleOne.Clothing;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Economy;
using Il2CppScheduleOne.ItemFramework;
using Il2CppScheduleOne.Management;
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.NPCs.Behaviour;
using Il2CppScheduleOne.ObjectScripts;
using Il2CppScheduleOne.Persistence;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.Product;
using Il2CppScheduleOne.StationFramework;
using Il2CppScheduleOne.Storage;
using Il2CppScheduleOne.UI.Items;
using UnityEngine;

namespace S1CoopTalk
{
    /// <summary>Items must not be duplicated or destroyed when two players, or a player and an employee, touch one slot.</summary>
    internal static class Items
    {
        // A slot change travels as the new total, worked out from what the sender can see, and the host applies it
        // blindly. Two players taking from one stack duplicate items, and a stale total wipes someone else's addition.
        // Guests send the change instead, marked with this bit; the host turns it into a total before broadcasting, and
        // only ever does so for a host that announced this mod.
        private const int DeltaFlag = 0x40000000;
        private const int DeltaMask = 0xFFFF;

        private static NetworkConnection _placingSender;
        private static int _decoded;
        private static readonly List<(DryingRack rack, MethodInfo receive, NetworkConnection conn, DryingOperation op)> HeldOps =
            new List<(DryingRack, MethodInfo, NetworkConnection, DryingOperation)>();
        private static float _nextRetry;

        private static readonly Type[] SlotOwners =
        {
            typeof(StorageEntity), typeof(MixingStation), typeof(DryingRack), typeof(PackagingStation), typeof(BrickPress),
            typeof(Cauldron), typeof(ChemistryStation), typeof(LabOven), typeof(MushroomSpawnStation), typeof(NPCInventory),
            typeof(Dealer), typeof(PlayerClothing),
        };

        public static void Install()
        {
            Type self = typeof(Items);
            Repairs.Patch(typeof(ItemUIManager), "EndDrag", self, prefix: nameof(ClampDragAmount));
            Repairs.Patch(typeof(ItemSlot), nameof(ItemSlot.ChangeQuantity), self, prefix: nameof(SendChangeNotTotal));
            Repairs.Patch(typeof(ItemSlot), nameof(ItemSlot.SetQuantity), self, prefix: nameof(ApplyQuantity));
            Repairs.Patch(typeof(ItemSlot), nameof(ItemSlot.SetStoredItem), self, prefix: nameof(ClearBeforeSwap));
            foreach (Type owner in SlotOwners)
            {
                Repairs.Patch(owner, "SetItemSlotQuantity_Internal", self, prefix: nameof(DecodeChange));
                Repairs.Patch(owner, "RpcReader___Server_SetStoredInstance", self, prefix: nameof(NoteSender), finalizer: nameof(ClearSender));
                Repairs.Patch(owner, "SetStoredInstance_Internal", self, prefix: nameof(ProtectOccupiedSlot));
            }
            Repairs.Patch(typeof(MixingStation), "RpcLogic___TryCreateOutputItems", self, prefix: nameof(OnlyFinishedMixWithRoom));
            Repairs.Patch(typeof(MixingStation), "RpcLogic___SendMixingOperation", self, prefix: nameof(RefuseSecondMix));
            Repairs.Patch(typeof(DryingRack), "RpcLogic___PleaseReceiveOp", self, prefix: nameof(HoldUnknownDryingOp));
            Repairs.Patch(typeof(MoveItemBehaviour), "TakeItem", self, prefix: nameof(LimitToEmployeeRoom));
            GuardPlacement();
        }

        public static void Reset()
        {
            HeldOps.Clear();
            _placingSender = null;
        }

        public static void Tick()
        {
            if (HeldOps.Count == 0 || Time.unscaledTime < _nextRetry)
                return;
            _nextRetry = Time.unscaledTime + 5f;
            for (int i = HeldOps.Count - 1; i >= 0; i--)
            {
                var held = HeldOps[i];
                if (held.rack == null)
                {
                    HeldOps.RemoveAt(i);
                }
                else if (Registry.ItemExists(held.op.ItemID))
                {
                    HeldOps.RemoveAt(i);
                    held.receive.Invoke(held.rack, new object[] { held.conn, held.op });
                }
            }
        }

        // ---- Drags ----

        // A drag kept the amount it started with while someone else emptied the stack, so dropping it duplicated the
        // difference. This was the easiest duplication to hit: the window lasts as long as the drag.
        private static void ClampDragAmount(ItemUIManager __instance)
        {
            ItemSlot slot = __instance.draggedSlot != null ? __instance.draggedSlot.assignedSlot : null;
            if (slot != null && slot.ItemInstance != null)
                __instance.draggedAmount = Mathf.Min(__instance.draggedAmount, slot.Quantity);
        }

        // ---- Quantities ----

        private static bool SendChangeNotTotal(ItemSlot __instance, int __0, bool __1)
        {
            if (__1 || __0 == 0 || !Repairs.HostHasMod || InstanceFinder.IsServer || __instance.SlotOwner == null)
                return true;
            // Same refusals as the game's method, which still logs them.
            if (__instance.ItemInstance == null || (__0 > 0 && __instance.IsLocked) || (__0 < 0 && __instance.IsRemovalLocked))
                return true;
            int index = __instance.SlotOwner.ItemSlots.IndexOf(__instance);
            if (index < 0)
                return true;
            __instance.SlotOwner.SetItemSlotQuantity(index, DeltaFlag | (__0 & DeltaMask));
            return false;
        }

        private static int Decode(int value, int current) => Math.Max(0, current + (short)(value & DeltaMask));

        private static void DecodeChange(NetworkBehaviour __instance, int __0, ref int __1)
        {
            if ((__1 & DeltaFlag) == 0)
                return;
            IItemSlotOwner owner = __instance.TryCast<IItemSlotOwner>();
            ItemSlot slot = owner != null && __0 >= 0 && __0 < owner.ItemSlots.Count ? owner.ItemSlots[__0] : null;
            __1 = Decode(__1, slot != null ? slot.Quantity : 0);
            _decoded++;
        }

        // A slot reserved by an employee refused the increase even though the broadcast already went out, so one
        // player kept seeing items the host had refused. Every peer now applies what was broadcast.
        private static bool ApplyQuantity(ItemSlot __instance, ref int __0, bool __1)
        {
            if ((__0 & DeltaFlag) != 0)
            {
                __0 = Decode(__0, __instance.Quantity);
                Sync.Note("A slot change reached the last step undecoded");
            }
            if (__1 && __instance.IsLocked && __instance.ItemInstance != null && __0 > __instance.Quantity)
            {
                __instance.ItemInstance.SetQuantity(__0);
                return false;
            }
            return true;
        }

        // ---- Placing into a slot ----

        // With the swap sending its own clear first, a placement that lands on an occupied slot on the host is always a
        // race, never intended, and must not destroy what is already there.
        private static void ClearBeforeSwap(ItemSlot __instance, ItemInstance __0, bool __1)
        {
            if (!__1 && __0 != null && Repairs.HostHasMod && !InstanceFinder.IsServer && __instance.ItemInstance != null)
                __instance.ClearStoredInstance(false);
        }

        private static void NoteSender(NetworkConnection __2) => _placingSender = __2;

        private static Exception ClearSender(Exception __exception)
        {
            _placingSender = null;
            return __exception;
        }

        private static bool ProtectOccupiedSlot(NetworkBehaviour __instance, NetworkConnection __0, int __1, ItemInstance __2)
        {
            // Only a player's own placement counts; a reply addressed to one player is the host correcting its view.
            if (_placingSender == null || __0 != null || __2 == null || _placingSender.IsLocalClient || !InstanceFinder.IsServer)
                return true;
            IItemSlotOwner owner = __instance.TryCast<IItemSlotOwner>();
            if (owner == null || __1 < 0 || __1 >= owner.ItemSlots.Count)
                return true;
            ItemSlot slot = owner.ItemSlots[__1];
            if (slot.ItemInstance == null)
                return true;

            // Everything below sends slot updates of its own, which come back through this method.
            NetworkConnection sender = _placingSender;
            _placingSender = null;
            int amount = __2.Quantity;
            if (slot.ItemInstance.CanStackWith(__2, false) && slot.ItemInstance.Quantity + amount <= slot.ItemInstance.StackLimit)
            {
                slot.ChangeQuantity(amount);
                Sync.Note("Two players placed into one slot: merged");
            }
            else if (!TryPlaceElsewhere(owner, __2))
            {
                Player from = PlayerByConnection(sender);
                Net.Send(Net.ItemBack, from != null ? from.PlayerName : "", "", "", __2.GetItemData().GetJson(false));
                Sync.Note("Two players placed into one slot: returned the item");
            }
            else
            {
                Sync.Note("Two players placed into one slot: used another slot");
            }
            owner.SetStoredInstance(sender, __1, slot.ItemInstance);
            return false;
        }

        private static bool TryPlaceElsewhere(IItemSlotOwner owner, ItemInstance item)
        {
            foreach (ItemSlot other in owner.ItemSlots)
            {
                if (other.ItemInstance == null && !other.IsLocked && other.DoesItemMatchHardFilters(item))
                {
                    other.SetStoredItem(item);
                    return true;
                }
            }
            return false;
        }

        private static Player PlayerByConnection(NetworkConnection conn)
        {
            foreach (Player p in Player.PlayerList)
                if (p != null && p.Connection != null && p.Connection.ClientId == conn.ClientId)
                    return p;
            return null;
        }

        public static void HandleItemBack(string target, string json)
        {
            if (target != Net.LocalName || string.IsNullOrEmpty(json))
                return;
            ItemInstance item = ItemDeserializer.LoadItem(json);
            if (item != null)
            {
                PlayerSingleton<PlayerInventory>.Instance.AddItemToInventory(item);
                Feed.System(Text.Ui("item_back"));
            }
        }

        // ---- Mixing ----

        // Finishing cleared the mix even when the output slot refused the result, deleting it. A late finish from a
        // lagging player could also complete a newer mix instantly.
        private static bool OnlyFinishedMixWithRoom(MixingStation __instance)
        {
            if (!InstanceFinder.IsServer)
                return true;
            MixOperation op = __instance.CurrentMixOperation;
            if (op == null || __instance.CurrentMixTime + 1 < __instance.GetMixTimeForCurrentOperation())
                return false;
            if (op.IsOutputKnown(out ProductDefinition known))
            {
                ItemInstance output = known.GetDefaultInstance(op.Quantity);
                if (__instance.OutputSlot.GetCapacityForItem(output, false) < op.Quantity)
                    return false;
            }
            return true;
        }

        // Ingredients leave the slots before this message, so a second mix on a busy station would destroy them and the
        // first mix's result. The sender's view was stale; hand the ingredients back and tell everyone what is running.
        private static bool RefuseSecondMix(MixingStation __instance, MixOperation __0)
        {
            if (!InstanceFinder.IsServer || __instance.CurrentMixOperation == null || __0 == null)
                return true;
            GiveBack(__instance.ProductSlot, __0.ProductID, __0.Quantity, __0.ProductQuality);
            GiveBack(__instance.MixerSlot, __0.IngredientID, __0.Quantity, null);
            __instance.SetMixOperation(null, __instance.CurrentMixOperation, __instance.CurrentMixTime);
            Sync.Note("Refused a second mix on a busy station");
            return false;
        }

        private static void GiveBack(ItemSlot slot, string id, int quantity, EQuality? quality)
        {
            ItemDefinition definition = Registry.GetItem(id);
            if (definition == null)
                return;
            ItemInstance item = definition.GetDefaultInstance(quantity);
            QualityItemInstance withQuality = item.TryCast<QualityItemInstance>();
            if (withQuality != null && quality.HasValue)
                withQuality.Quality = quality.Value;
            slot.AddItem(item);
        }

        // ---- Unknown items ----

        // A drying rack can send a mixed product before its definition arrives, and building its visuals then throws.
        private static bool HoldUnknownDryingOp(DryingRack __instance, NetworkConnection __0, DryingOperation __1, MethodBase __originalMethod)
        {
            if (__1 == null || Registry.ItemExists(__1.ItemID))
                return true;
            HeldOps.Add((__instance, (MethodInfo)__originalMethod, __0, __1));
            Sync.Note("Held a drying rack update until its product is known: " + __1.ItemID);
            return false;
        }

        // ---- Employees carrying items ----

        // Taking more than the employee can carry removed it from the slot and then dropped the excess.
        private static bool LimitToEmployeeRoom(MoveItemBehaviour __instance)
        {
            try
            {
                ItemInstance template = __instance.itemToRetrieveTemplate;
                NPCInventory inventory = __instance.Npc != null ? __instance.Npc.Inventory : null;
                if (template == null || inventory == null)
                    return true;
                int room = inventory.GetCapacityForItem(template);
                if (room <= 0)
                {
                    __instance.Disable_Networked(null);
                    Sync.Note("Employee full, did not take items it would have dropped");
                    return false;
                }
                __instance.maxMoveAmount = __instance.maxMoveAmount < 0 ? room : Math.Min(__instance.maxMoveAmount, room);
            }
            catch (Exception e)
            {
                Sync.Note("Employee capacity: " + e.Message);
            }
            return true;
        }

        // Arriving at a destination short of room deleted the whole carried stack whenever the source could not take it
        // back, and a drying rack's output slot never can. Put it back in the source's output slot instead.
        private static void GuardPlacement()
        {
            foreach (Type nested in typeof(MoveItemBehaviour).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (!nested.Name.Contains("PlaceItem"))
                    continue;
                MethodInfo next = AccessTools.Method(nested, "MoveNext");
                if (next != null)
                    Repairs.PatchMethod(next, typeof(Items), prefix: nameof(ReturnStackInsteadOfLosing));
            }
        }

        private static void ReturnStackInsteadOfLosing(object __instance)
        {
            try
            {
                Type type = __instance.GetType();
                int state = (int)AccessTools.Property(type, "__1__state").GetValue(__instance);
                if (state != 1)
                    return;
                var behaviour = AccessTools.Property(type, "__4__this").GetValue(__instance) as MoveItemBehaviour;
                if (behaviour == null || behaviour.grabbedAmount <= 0 || behaviour.assignedRoute == null)
                    return;
                ItemInstance template = behaviour.itemToRetrieveTemplate;
                ITransitEntity destination = behaviour.assignedRoute.Destination;
                ITransitEntity source = behaviour.assignedRoute.Source;
                if (template == null || destination == null || source == null)
                    return;
                int amount = behaviour.grabbedAmount;
                if (destination.GetInputCapacityForItem(template, behaviour.Npc, true) >= amount
                    || source.GetOutputCapacityForItem(template, behaviour.Npc) >= amount)
                    return;

                ItemInstance carried = behaviour.Npc.Inventory.GetFirstIdenticalItem(template);
                if (carried == null)
                    return;
                foreach (ItemSlot slot in source.OutputSlots)
                {
                    if (slot.ItemInstance != null && !slot.ItemInstance.CanStackWith(template, false))
                        continue;
                    if (slot.GetCapacityForItem(template, false) < amount)
                        continue;
                    slot.AddItem(carried.GetCopy(amount));
                    carried.ChangeQuantity(-amount);
                    behaviour.grabbedAmount = 0;
                    Sync.Note("Employee returned a stack it would have lost");
                    return;
                }
                // Nowhere to put it: staying on the employee beats being deleted.
                behaviour.grabbedAmount = 0;
                Sync.Note("Employee kept a stack it would have lost");
            }
            catch (Exception e)
            {
                Sync.Note("Employee placement: " + e.Message);
            }
        }
    }
}

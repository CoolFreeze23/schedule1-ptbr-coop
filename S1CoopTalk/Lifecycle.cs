using System;
using System.Collections.Generic;
using Il2CppFishNet;
using Il2CppFishNet.Connection;
using Il2CppFishNet.Object;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.GameTime;
using Il2CppScheduleOne.Networking;
using Il2CppScheduleOne.Persistence;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.UI;
using Il2CppScheduleOne.UI.MainMenu;
using Il2CppScheduleOne.Vehicles;
using UnityEngine;

namespace S1CoopTalk
{
    /// <summary>Joining, leaving and saving: players must not lose what they carry, or get stuck on a screen.</summary>
    internal static class Lifecycle
    {
        private const float RetryAfter = 15f, SleepWaitLimit = 20f, ScreenCloseDelay = 10f;
        private static float _closeScreenAt = -1f;
        private const int MaxRetries = 4;
        private const int WaitingForHostPhase = 3;

        private static readonly List<(int index, Player player)> Hidden = new List<(int, Player)>();
        private static float _loadSince = -1f, _nextRetry, _sleepWaitSince = -1f;
        private static int _retries;
        private static bool _sawClient, _everServer;

        public static void Install()
        {
            Type self = typeof(Lifecycle);
            Repairs.Patch(typeof(Player), nameof(Player.PreDestroyClientObjects), self, prefix: nameof(OnClientLeaving));
            Repairs.Patch(typeof(PlayerManager), nameof(PlayerManager.WriteData), self, prefix: nameof(HideUnloadedPlayers), finalizer: nameof(RestorePlayers));
            Repairs.Patch(typeof(PlayerManager), nameof(PlayerManager.SavePlayer), self, prefix: nameof(OnlyLoadedPlayers));
            Repairs.Patch(typeof(LoadManager), nameof(LoadManager.StartGame), self, prefix: nameof(MarkLobbyNotReady));
            Repairs.Patch(typeof(VehicleManager), nameof(VehicleManager.GetSaveString), self, prefix: nameof(ForgetDestroyedVehicles));
        }

        public static void Reset()
        {
            _sleepWaitSince = -1f;
        }

        // ---- Someone leaves ----

        // The game runs this on every Player for every disconnect and ignores which connection left, so any guest
        // leaving stripped ownership of all driven vehicles and threw the host out of theirs. It also never saved the
        // leaver, so a crash lost whatever they had picked up since the last clean exit.
        private static bool OnClientLeaving(Player __instance, NetworkConnection __0)
        {
            try
            {
                if (__instance == null || __0 == null || __instance.Owner == null || __instance.Owner.ClientId != __0.ClientId)
                    return false;

                if (__instance.PlayerLoaded && !__instance.IsLocalPlayer && !string.IsNullOrEmpty(__instance.PlayerCode)
                    && !Singleton<LoadManager>.Instance.IsLoading)
                {
                    Singleton<PlayerManager>.Instance.SavePlayer(__instance);
                    Sync.Note("Saved " + __instance.PlayerName + " as they left");
                }

                // Anything the leaver still owned would despawn with them.
                var owned = new List<NetworkObject>();
                foreach (NetworkObject o in __0.Objects)
                    owned.Add(o);
                foreach (NetworkObject o in owned)
                    if (o != null && o.GetComponent<LandVehicle>() != null)
                        o.RemoveOwnership();

                NetworkObject current = __instance.CurrentVehicle;
                LandVehicle vehicle = current != null ? current.GetComponent<LandVehicle>() : null;
                if (vehicle != null)
                    for (int i = 0; i < vehicle.Seats.Length; i++)
                        if (vehicle.Seats[i].Occupant == __instance)
                            vehicle.SetSeatOccupant(null, i, null);
            }
            catch (Exception e)
            {
                Sync.Note("Leaving player: " + e.Message);
            }
            return false;
        }

        // ---- Saving ----

        // A joiner's inventory on the host starts empty and fills as their data arrives. Saving in that window would
        // overwrite their real save with nothing.
        private static void HideUnloadedPlayers()
        {
            Hidden.Clear();
            for (int i = Player.PlayerList.Count - 1; i >= 0; i--)
            {
                Player p = Player.PlayerList[i];
                if (p != null && !p.IsLocalPlayer && !p.PlayerLoaded)
                {
                    Hidden.Add((i, p));
                    Player.PlayerList.RemoveAt(i);
                    Sync.Note("Save skipped " + p.PlayerName + ", still joining");
                }
            }
        }

        private static Exception RestorePlayers(Exception __exception)
        {
            for (int i = Hidden.Count - 1; i >= 0; i--)
                Player.PlayerList.Insert(Hidden[i].index, Hidden[i].player);
            Hidden.Clear();
            return __exception;
        }

        private static bool OnlyLoadedPlayers(Player __0) => __0 != null && __0.PlayerLoaded;

        private static void MarkLobbyNotReady()
        {
            try
            {
                Lobby lobby = PersistentSingleton<Lobby>.Instance;
                if (lobby != null && lobby.IsHost)
                    lobby.SetLobbyData("ready", "false");
            }
            catch (Exception e)
            {
                Sync.Note("Lobby state: " + e.Message);
            }
        }

        private static void ForgetDestroyedVehicles(VehicleManager __instance)
        {
            for (int i = __instance.PlayerOwnedVehicles.Count - 1; i >= 0; i--)
                if (__instance.PlayerOwnedVehicles[i] == null)
                    __instance.PlayerOwnedVehicles.RemoveAt(i);
        }

        // ---- Stuck screens ----

        public static void Tick()
        {
            try
            {
                WatchLoading();
                WatchSleep();
            }
            catch (Exception e)
            {
                Sync.Note("Watchdog: " + e.Message);
            }
        }

        // Stopping the abandoned load can leave its loading screen open on top of the menu.
        private static void CloseLeftoverLoadingScreen()
        {
            if (_closeScreenAt < 0f || Time.unscaledTime < _closeScreenAt)
                return;
            _closeScreenAt = -1f;
            LoadingScreen screen = PersistentSingleton<LoadingScreen>.Instance;
            if (screen != null && screen.IsOpen)
            {
                screen.Close();
                Sync.Note("Closed a loading screen left over from the lost connection");
            }
        }

        private static void WatchLoading()
        {
            CloseLeftoverLoadingScreen();
            if (!Singleton<LoadManager>.InstanceExists)
                return;
            LoadManager load = Singleton<LoadManager>.Instance;
            if (!load.IsLoading)
            {
                _loadSince = -1f;
                _sawClient = _everServer = false;
                _retries = 0;
                return;
            }
            if (_loadSince < 0f)
            {
                _loadSince = Time.unscaledTime;
                _nextRetry = Time.unscaledTime + RetryAfter;
            }

            _everServer |= InstanceFinder.IsServer;
            if (_everServer)
                return;
            _sawClient |= InstanceFinder.IsClient;

            // The connection dropped while loading. The game ignores this and shows the loading screen forever.
            if (_sawClient && !InstanceFinder.IsClient)
            {
                _sawClient = false;
                Sync.Note("Connection lost while loading, returning to the menu");
                load.StopAllCoroutines();
                load.IsGameLoaded = true;
                load.IsLoading = false;
                load.ExitToMenu(null, new MainMenuPopup.Data(Text.Ui("load_lost_title"), Text.Ui("load_lost_body"), true), false);
                _closeScreenAt = Time.unscaledTime + ScreenCloseDelay;
                return;
            }

            // The single message with this player's data can be lost, and nothing asks for it again.
            Player local = Player.Local;
            if (local != null && !local.PlayerLoaded && !string.IsNullOrEmpty(local.PlayerCode)
                && Time.unscaledTime >= _nextRetry && _retries < MaxRetries)
            {
                _nextRetry = Time.unscaledTime + RetryAfter;
                _retries++;
                local.RequestPlayerData_Server(local.PlayerCode, false);
                Sync.Note($"Asked again for player data, try {_retries}");
            }
        }

        // The guest's sleep ends on one state update from the host, with no timeout, so losing it means a black screen.
        private static void WatchSleep()
        {
            if (!NetworkSingleton<SleepController>.InstanceExists || InstanceFinder.IsServer)
                return;
            SleepController sleep = NetworkSingleton<SleepController>.Instance;
            if ((int)sleep.CurrentPhase != WaitingForHostPhase)
            {
                _sleepWaitSince = -1f;
                return;
            }
            if (_sleepWaitSince < 0f)
                _sleepWaitSince = Time.unscaledTime;
            if (Time.unscaledTime - _sleepWaitSince > SleepWaitLimit)
            {
                _sleepWaitSince = -1f;
                sleep.sync___set_value__IsHostReadyToProceed_k__BackingField(true, false);
                Sync.Note("Sleep: stopped waiting for the host");
            }
        }
    }
}

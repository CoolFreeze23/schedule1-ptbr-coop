using System;
using System.Text;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Networking;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppSteamworks;

namespace S1CoopTalk
{
    /// <summary>
    /// Steam lobby chat transport. The game's own lobby send is ASCII-only and its receive
    /// buffer is 128 bytes, so this reads and writes the Steam API directly with UTF-8 and
    /// keeps these messages away from the game's handler.
    /// </summary>
    public static class Net
    {
        public const string Hello = "hi", HelloBack = "hb", Line = "ln", Choice = "ch", Bubble = "bb",
            CallStart = "cs", CallLine = "cl", Sale = "sl", Arrested = "ar", Died = "di",
            ActivityEnd = "en", WantedOn = "w1", WantedOff = "w0", PingMark = "pg", Saved = "sv",
            ChatLine = "tx", CashSend = "c1", CashAck = "c2", ResyncAsk = "rq";

        private const string Magic = "S1CT1";
        private const char Sep = '\u001f';
        private const int MaxBytes = 3900;
        private const int FieldCount = 5;

        public static string LocalName
        {
            get
            {
                try
                {
                    return Player.Local != null ? Player.Local.PlayerName : "?";
                }
                catch
                {
                    return "?";
                }
            }
        }

#if DEVUDP
        public static void DevReceive(string payload)
        {
            string[] parts = payload.Split(Sep, FieldCount + 1);
            if (parts.Length != FieldCount + 1 || parts[0] != Magic)
                return;
            var fields = new string[FieldCount];
            Array.Copy(parts, 1, fields, 0, FieldCount);
            Mod.Receive(fields);
        }
#endif

        // Lobby.LobbyID is never assigned in this build; the ID lives in the Steam service.
        private static ulong CurrentLobby()
        {
            Lobby lobby = PersistentSingleton<Lobby>.Instance;
            if (lobby == null || lobby._lobbyService == null)
                return 0UL;
            SteamLobbyService service = lobby._lobbyService.TryCast<SteamLobbyService>();
            return service != null ? service._lobbyID : 0UL;
        }

        public static void Install(HarmonyLib.Harmony h)
        {
            var target = AccessTools.Method(typeof(SteamLobbyService), "OnLobbyChatMessage");
            if (target == null)
            {
                Mod.Log.Error("Lobby message handler not found, sync is off");
                return;
            }
            h.Patch(target, prefix: new HarmonyMethod(typeof(Net), nameof(OnChat)));
        }

        public static void Send(string type, string who, string npcId, string npcName, string body)
        {
#if DEVUDP
            DevLink.Send(string.Join(Sep, Magic, type, who ?? "", npcId ?? "", npcName ?? "", body ?? ""));
#endif
            try
            {
                ulong lobby = CurrentLobby();
                if (lobby == 0UL)
                    return;

                string payload = string.Join(Sep, Magic, type, who ?? "", npcId ?? "", npcName ?? "", body ?? "");
                byte[] raw = Encoding.UTF8.GetBytes(payload);
                int length = Math.Min(raw.Length, MaxBytes);
                var buffer = new Il2CppStructArray<byte>(length);
                for (int i = 0; i < length; i++)
                    buffer[i] = raw[i];
                _lastSendOk = SteamMatchmaking.SendLobbyChatMsg(new CSteamID(lobby), buffer, length);
                _sent++;
            }
            catch (Exception e)
            {
                Mod.Log.Warning("Send failed: " + e.Message);
            }
        }

        private static bool _lastSendOk;
        private static int _sent, _received, _ours;

        /// <summary>Connection state for troubleshooting.</summary>
        public static string Diag()
        {
            ulong lobby = CurrentLobby();
            return $"lobby={lobby} sent={_sent} lastOk={_lastSendOk} received={_received} ours={_ours}";
        }

        private static bool OnChat(LobbyChatMsg_t result)
        {
            _received++;
            try
            {
                var buffer = new Il2CppStructArray<byte>(4096);
                CSteamID user;
                EChatEntryType kind;
                int n = SteamMatchmaking.GetLobbyChatEntry(new CSteamID(result.m_ulSteamIDLobby), (int)result.m_iChatID, out user, buffer, buffer.Length, out kind);
                if (n < Magic.Length + 1)
                    return true;

                byte[] raw = new byte[n];
                for (int i = 0; i < n; i++)
                    raw[i] = buffer[i];
                for (int i = 0; i < Magic.Length; i++)
                    if (raw[i] != Magic[i])
                        return true;

                _ours++;
                bool mine = user.m_SteamID == SteamUser.GetSteamID().m_SteamID;
                if (mine && !Mod.Echo.Value)
                    return false;

                string[] parts = Encoding.UTF8.GetString(raw).Split(Sep, FieldCount + 1);
                if (parts.Length == FieldCount + 1)
                {
                    var fields = new string[FieldCount];
                    Array.Copy(parts, 1, fields, 0, FieldCount);
                    Mod.Receive(fields);
                }
                return false;
            }
            catch (Exception e)
            {
                Mod.Log.Warning("Receive failed: " + e.Message);
                return true;
            }
        }
    }
}

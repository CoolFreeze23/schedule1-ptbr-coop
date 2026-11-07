using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

namespace S1CoopTalk
{
    /// <summary>Lines travel in English; each player's game shows them in that player's language.</summary>
    public static class Text
    {
        private static Func<string, string> _display, _toEnglish;
        private static Func<bool> _enabled;

        private static readonly Dictionary<string, (string en, string pt)> Strings = new Dictionary<string, (string, string)>
        {
            ["npc_to"] = ("{0} to {1}", "{0} para {1}"),
            ["call_line"] = ("{0} on the phone with {1}", "{0} no telefone com {1}"),
            ["call_start"] = ("{0} answered a call from {1}", "{0} atendeu uma ligação de {1}"),
            ["sale"] = ("{0} sold to {1} for {2}", "{0} vendeu para {1} por {2}"),
            ["arrested"] = ("{0} got arrested!", "{0} foi em cana!"),
            ["died"] = ("{0} is down!", "{0} caiu!"),
            ["joined"] = ("Conversations are now shared with {0}", "Conversas compartilhadas com {0}"),
            ["missing_mod"] = ("A player doesn't have S1 Co-op Talk, so you won't see their conversations", "Tem jogador sem o S1 Co-op Talk, então você não vai ver as conversas dessa pessoa"),
            ["history"] = ("Shared conversations ({0} to close)", "Conversas compartilhadas ({0} para fechar)"),
            ["history_empty"] = ("Nothing yet.", "Nada por enquanto."),
            ["you"] = ("You", "Você"),
            ["ping"] = ("{0} marked a spot ({1} away)", "{0} marcou um ponto (a {1})"),
            ["bed"] = ("{0} is in bed, waiting for you to sleep", "{0} tá na cama esperando você dormir"),
            ["arrived"] = ("{0} joined the game", "{0} entrou no jogo"),
            ["left"] = ("{0} left the game", "{0} saiu do jogo"),
            ["saved"] = ("The host saved the game", "O host salvou o jogo"),
            ["wanted"] = ("The police are after {0}!", "A polícia tá na cola de {0}!"),
            ["escaped"] = ("{0} shook off the police", "{0} despistou a polícia"),
            ["version"] = ("Version mismatch with {0}: {1} here, {2} there. Things may not sync.", "Versão diferente da de {0}: aqui {1}, lá {2}. Pode dar problema."),
            ["sos"] = ("{0} needs help! ({1} away)", "{0} precisa de ajuda! (a {1})"),
            ["chat_hint"] = ("Type a message...  (/cash 500 gives money)", "Digite uma mensagem...  (/grana 500 dá dinheiro)"),
            ["cash_sent"] = ("You gave {1} to {0}", "Você deu {1} para {0}"),
            ["cash_got"] = ("{0} gave you {1}!", "{0} te deu {1}!"),
            ["cash_refund"] = ("{0} didn't get the {1}, it came back to you", "{0} não recebeu os {1}, a grana voltou pra você"),
            ["cash_short"] = ("You don't have that much cash on you", "Você não tem tudo isso em dinheiro vivo"),
            ["cash_nobody"] = ("No player found with that name", "Nenhum jogador com esse nome"),
            ["cash_usage"] = ("Use: /cash 500  or  /cash Name 500", "Use: /grana 500  ou  /grana Nome 500"),
            ["score_title"] = ("Today's scoreboard", "Placar do dia"),
            ["score_row"] = ("{0} sales, {1}", "{0} vendas, {1}"),
            ["sync_asked"] = ("{0} items out of sync, asking the host to re-send", "{0} itens fora de sincronia, pedindo pro host reenviar"),
            ["sync_done"] = ("Everything back in sync", "Tudo sincronizado de novo"),
            ["st_arrested"] = ("Arrested", "Em cana"),
            ["st_down"] = ("Knocked out", "Apagado"),
            ["st_wanted"] = ("Police chase", "Fugindo da polícia"),
            ["st_bed"] = ("In bed, waiting for you", "Na cama, esperando você"),
            ["st_driving"] = ("Driving", "Dirigindo"),
            ["st_talking"] = ("Talking to {0}", "Conversando com {0}"),
            ["st_phone"] = ("On the phone with {0}", "No telefone com {0}"),
        };

        public static void Connect()
        {
            Assembly asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "S1PortuguesBR");
            Type t = asm?.GetType("S1PortuguesBR.Translator");
            if (t == null)
                return;
            _display = t.GetMethod("Display")?.CreateDelegate<Func<string, string>>();
            _toEnglish = t.GetMethod("ToEnglish")?.CreateDelegate<Func<string, string>>();
            _enabled = t.GetProperty("Enabled")?.GetGetMethod()?.CreateDelegate<Func<bool>>();
            Mod.Log.Msg("Connected to the Portuguese translation");
        }

        public static string Display(string s) => _display != null ? _display(s) : s;
        public static string ToEnglish(string s) => _toEnglish != null ? _toEnglish(s) : s;
        private static bool Portuguese => _enabled != null && _enabled();

        public static string Ui(string key) => Portuguese ? Strings[key].pt : Strings[key].en;

        private static readonly Regex InputTag = new Regex(@"<Input_[A-Za-z0-9]+>", RegexOptions.Compiled);

        /// <summary>Game markup to something a plain text label can show.</summary>
        public static string ForFeed(string s)
        {
            if (string.IsNullOrEmpty(s))
                return s;
            return InputTag.Replace(s, "").Replace("<h1>", "<color=#FFD966>").Replace("</h>", "</color>").Trim();
        }

        public static string StripForBubble(string s) => InputTag.Replace(s ?? "", "").Replace("<h1>", "").Replace("</h>", "").Trim();
    }
}

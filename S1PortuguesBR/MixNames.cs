using System.Collections.Generic;
using Il2CppScheduleOne.UI;

namespace S1PortuguesBR
{
    /// <summary>
    /// The "Randomize" product names. Written for Portuguese rather than translated: the first word
    /// is the noun and the second never needs gender agreement, so every pair reads naturally.
    /// </summary>
    public static class MixNames
    {
        private static readonly string[] Nouns =
        {
            "Morte", "Bolas", "Cristal", "Vômito", "Fedor", "Piroca", "Biscoito", "Neblina", "Soco", "Ouro",
            "Queijo", "Diesel", "Broto", "Foda-Trovão", "Fantasma", "Pinto", "Bolo", "Diamante", "Saco", "Urkle",
            "Durkle", "Macaco", "Mijo", "Combustível", "Rinoceronte", "McLovin", "Explode-Cu", "Expresso", "Menta",
            "Crack", "Bunda", "Fruta", "Peido Molhado", "Sebo", "Gozada", "Porra", "Pum de Xota", "Melado", "Gosma"
        };

        private static readonly string[] Qualifiers =
        {
            "de Aspen", "de Tóquio", "da Califórnia", "Super", "Mega", "de LA", "do Vovô", "OG", "da Neve", "das Trevas", "Doce",
            "da Ilha", "do Milagre", "da Morte", "Rosa", "Bio", "de Escoteira", "de Gorila", "Tutti-Frutti", "de Casamento",
            "de Morango", "de Banana", "de Sorvete", "Roxo-Roxo", "do Afeganistão", "de Pesadelo", "dos Sonhos", "Ultra",
            "Sexy", "do Cabeludo", "Brilhante", "Grande", "do Gordo", "Parrudo", "ao Extremo", "do Fedido", "do Gosmento"
        };

        private static readonly string[] EnglishNames1 =
        {
            "Aspen", "Tokyo", "California", "Super", "Mega", "LA", "Granddaddy", "OG", "White", "Dark", "Sweet", "Island", "Miracle", "Death", "Pink", "Bio",
            "Girl Scout", "Gorilla", "Fruity", "Wedding", "Strawberry", "Banana", "Ice Cream", "Purple", "Afghan", "Nightmare", "Dream", "Ultra", "Sexy",
            "Hairy", "Shiny", "Big", "Fat", "Thick", "Extreme", "Stinky", "Slimy"
        };

        private static readonly string[] EnglishNames2 =
        {
            "Death", "Balls", "Crystal", "Puke", "Stink", "Cock", "Cookies", "Haze", "Punch", "Gold", "Cheese", "Diesel", "Bud", "Thunderfuck", "Ghost", "Dick",
            "Cake", "Diamond", "Balls", "Urkle", "Durkle", "Monkey", "Piss", "Fuel", "Rhino", "McLovin", "Assblaster", "Express", "Mint", "Crack", "Ass", "Fruit",
            "Shart", "Smegma", "Splooge", "Cum", "Queef", "Grool", "Slime"
        };

        /// <summary>Every name the English generator can produce, as shown in Portuguese. Saves keep the English name.</summary>
        public static IEnumerable<(string en, string pt)> AllPairs()
        {
            for (int a = 0; a < EnglishNames1.Length; a++)
                for (int b = 0; b < EnglishNames2.Length; b++)
                    yield return (EnglishNames1[a] + " " + EnglishNames2[b], Nouns[b] + " " + Qualifiers[a]);
        }

        private static bool? _applied;
        private static Il2CppSystem.Collections.Generic.List<string> _english1, _english2;

        public static void Apply(NewMixScreen screen)
        {
            bool portuguese = Translator.Enabled;
            if (_applied == portuguese)
                return;

            if (_english1 == null)
            {
                _english1 = screen.name1Library;
                _english2 = screen.name2Library;
            }

            screen.name1Library = portuguese ? ToList(Nouns) : _english1;
            screen.name2Library = portuguese ? ToList(Qualifiers) : _english2;
            _applied = portuguese;
        }

        private static Il2CppSystem.Collections.Generic.List<string> ToList(IEnumerable<string> items)
        {
            var list = new Il2CppSystem.Collections.Generic.List<string>();
            foreach (string s in items)
                list.Add(s);
            return list;
        }
    }
}

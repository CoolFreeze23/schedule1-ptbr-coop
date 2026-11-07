using System;
using Il2CppInterop.Runtime;
using Il2CppScheduleOne.UI.MainMenu;
using Il2CppScheduleOne.UI.Settings;
using Il2CppTMPro;
using UnityEngine;
using UnityEngine.Events;

namespace S1PortuguesBR
{
    /// <summary>Adds the Language row at the top of the Display tab of the game's Settings screen.</summary>
    public static class LanguageSetting
    {
        private const string RowName = "S1 Language";
        private static readonly string[] Codes = { Mod.Portuguese, Mod.English };
        private static readonly string[] Labels = { "Português (Brasil)", "English" };

        public static void Attach(SettingsScreen screen)
        {
            try
            {
                DisplayModeDropdown source = screen.GetComponentInChildren<DisplayModeDropdown>(true);
                if (source == null)
                {
                    Mod.Log.Warning("Display Mode row not found, cannot add the Language row");
                    return;
                }

                GameObject sourceRow = source.transform.parent.gameObject;
                Transform layout = sourceRow.transform.parent;
                if (layout.Find(RowName) != null)
                    return;

                // Cloned while inactive so the copied DisplayModeDropdown never runs on the new row.
                bool wasActive = sourceRow.activeSelf;
                sourceRow.SetActive(false);
                GameObject row = UnityEngine.Object.Instantiate(sourceRow, layout);
                sourceRow.SetActive(wasActive);

                row.name = RowName;
                row.transform.SetSiblingIndex(0);

                DisplayModeDropdown copied = row.GetComponentInChildren<DisplayModeDropdown>(true);
                TMP_Dropdown dropdown = copied.GetComponent<TMP_Dropdown>();
                UnityEngine.Object.DestroyImmediate(copied);

                foreach (TMP_Text t in row.GetComponentsInChildren<TMP_Text>(true))
                    if (t.name.StartsWith("Option Name"))
                        t.text = "Language";

                dropdown.onValueChanged = new TMP_Dropdown.DropdownEvent();
                dropdown.ClearOptions();
                var options = new Il2CppSystem.Collections.Generic.List<TMP_Dropdown.OptionData>();
                foreach (string label in Labels)
                    options.Add(new TMP_Dropdown.OptionData(label));
                dropdown.AddOptions(options);
                dropdown.SetValueWithoutNotify(Array.IndexOf(Codes, Mod.Language));
                dropdown.RefreshShownValue();
                dropdown.onValueChanged.AddListener(DelegateSupport.ConvertDelegate<UnityAction<int>>(
                    new Action<int>(i => Mod.SetLanguage(Codes[i]))));

                row.SetActive(true);
            }
            catch (Exception e)
            {
                Mod.Log.Error("Could not add the Language row: " + e);
            }
        }
    }
}

using System.IO;
using UnityEditor;
using UnityEngine;

namespace DivineDragon.GUI.Settings
{
    public class DivineRipperSettingsProvider
    {
        /// <summary>
        /// Path to the DivineRipper Settings page
        /// </summary>
        public const string DivineRipperSettingsPath = "Project/Divine Dragon/Divine Ripper Settings";

        /// <summary>
        /// Path to AssetRipper's executable
        /// </summary>
        public const string AssetRipperPathKey = "DivineRipper_AssetRipperPath"; // Should we turn this into a read-only Property for convenience?

        /// <summary>
        /// Whether to show the GUID Sync Report Window after ripping
        /// </summary>
        public const string ShowGuidSyncReportWindowKey = "DivineRipper_ShowGuidSyncReportWindow";

        public static bool ShouldShowGuidSyncReportWindow => EditorPrefs.GetBool(ShowGuidSyncReportWindowKey, false);
        
        [SettingsProvider]
        public static SettingsProvider CreateDivineRipperSettingsProvider()
        {
            var provider = new SettingsProvider(DivineRipperSettingsPath, SettingsScope.Project)
            {
                label = "Divine Ripper",

                guiHandler = (searchContext) =>
                {
                    EditorGUI.BeginChangeCheck();
                    
                    EditorGUILayout.BeginHorizontal();
                    
                    string assetRipperPath = EditorGUILayout.TextField("AssetRipper Path", EditorPrefs.GetString(AssetRipperPathKey, ""));
                    bool showReportWindow = ShouldShowGuidSyncReportWindow;

                    if (GUILayout.Button("Browse...", GUILayout.MaxWidth(80)))
                    {
                        string selectedPath = EditorUtility.OpenFilePanelWithFilters("Select AssetRipper Executable", assetRipperPath, new string[] {});
                        
                        if (!string.IsNullOrEmpty(selectedPath))
                        {
                            assetRipperPath = selectedPath;
                        }
                    }

                    EditorGUILayout.EndHorizontal();

                    showReportWindow = EditorGUILayout.Toggle("Show Dev Report", showReportWindow);

                    if (EditorGUI.EndChangeCheck())
                    {
                        EditorPrefs.SetString(AssetRipperPathKey, assetRipperPath);
                        EditorPrefs.SetBool(ShowGuidSyncReportWindowKey, showReportWindow);
                    }
                },
            };

            return provider;
        }
    }
}

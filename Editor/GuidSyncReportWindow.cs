using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace DivineDragon
{
    /// This hasn't been extensively tested, but it helps visualize the results of a GUID sync operation
    /// Actual verification should be done by manually inspecting the assets, perhaps with the help of git diff or similar tool
    /// and ultimately, by importing from various separate AssetRipper rips and ensuring everything works as expected
    public class GuidSyncReportWindow : EditorWindow
    {
        [SerializeField]
        private GuidSyncReport _report;
        private ScrollView _scrollView;

        public static void ShowReport(GuidSyncReport report)
        {
            if (report == null)
            {
                Debug.LogWarning("ShowReport called with null report");
            }
            else
            {
                Debug.Log($"ShowReport called with: {report.NewFilesImported?.Count ?? 0} new files, {report.Mappings?.Count ?? 0} existing references");
            }

            var window = GetWindow<GuidSyncReportWindow>("GUID Sync Report");
            window._report = report;
            window.minSize = new Vector2(600, 500);
            window.Show();

            if (window.rootVisualElement != null && window.rootVisualElement.childCount > 0)
            {
                window.RefreshUI();
            }
        }

        private void CreateGUI()
        {
            RefreshUI();
        }

        private void RefreshUI()
        {
            var root = rootVisualElement;
            root.Clear();

            root.style.paddingTop = 10;
            root.style.paddingBottom = 10;
            root.style.paddingLeft = 10;
            root.style.paddingRight = 10;

            var title = new Label("GUID Synchronization Report");
            title.style.fontSize = 18;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.marginBottom = 10;
            root.Add(title);

            if (_report == null)
            {
                var errorLabel = new Label("No report data available.");
                errorLabel.style.fontSize = 14;
                errorLabel.style.opacity = 0.6f;
                root.Add(errorLabel);

                var buttonContainer = new VisualElement();
                buttonContainer.style.flexDirection = FlexDirection.Row;
                buttonContainer.style.justifyContent = Justify.FlexEnd;
                buttonContainer.style.marginTop = 10;

                var closeButton = new Button(() => Close())
                {
                    text = "Close"
                };
                closeButton.style.width = 100;
                buttonContainer.Add(closeButton);

                root.Add(buttonContainer);
                return;
            }

            var summaryContainer = new VisualElement();
            summaryContainer.style.backgroundColor = new Color(0.2f, 0.2f, 0.2f, 0.3f);
            summaryContainer.style.paddingTop = 12;
            summaryContainer.style.paddingBottom = 12;
            summaryContainer.style.paddingLeft = 15;
            summaryContainer.style.paddingRight = 15;
            summaryContainer.style.marginBottom = 15;
            summaryContainer.style.borderTopLeftRadius = 5;
            summaryContainer.style.borderTopRightRadius = 5;
            summaryContainer.style.borderBottomLeftRadius = 5;
            summaryContainer.style.borderBottomRightRadius = 5;

            var summaryLabel = new Label("Summary");
            summaryLabel.style.fontSize = 14;
            summaryLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            summaryLabel.style.marginBottom = 5;
            summaryContainer.Add(summaryLabel);

            var summaryText = new Label(_report.SummaryText ?? "No summary available");
            summaryText.style.whiteSpace = WhiteSpace.Normal;
            summaryContainer.Add(summaryText);

            root.Add(summaryContainer);

            _scrollView = new ScrollView();
            _scrollView.style.flexGrow = 1;
            root.Add(_scrollView);

            PopulateReport();

            var buttonContainer2 = new VisualElement();
            buttonContainer2.style.flexDirection = FlexDirection.Row;
            buttonContainer2.style.justifyContent = Justify.FlexEnd;
            buttonContainer2.style.marginTop = 10;

            var closeButton2 = new Button(() => Close())
            {
                text = "Close"
            };
            closeButton2.style.width = 100;
            buttonContainer2.Add(closeButton2);

            root.Add(buttonContainer2);
        }

        private void PopulateReport()
        {
            if (_scrollView == null || _report == null)
            {
                Debug.LogWarning($"PopulateReport: scrollView={_scrollView != null}, report={_report != null}");
                return;
            }

            _scrollView.Clear();

            if (_report.NewFilesImported != null && _report.NewFilesImported.Count > 0)
            {
                var newFilesSection = CreateNewFilesSection();
                _scrollView.Add(newFilesSection);
            }

            if (_report.SkippedFiles != null && _report.SkippedFiles.Count > 0)
            {
                var skippedFilesSection = CreateSkippedFilesSection();
                _scrollView.Add(skippedFilesSection);
            }

            if (_report.Mappings != null && _report.Mappings.Count > 0)
            {
                var remappedFilesSection = CreateRemappedFilesSection();
                _scrollView.Add(remappedFilesSection);
            }

            var exportSection = CreateExportSection();
            _scrollView.Add(exportSection);
        }

        private VisualElement CreateSection(string title, List<string> items)
        {
            var section = new VisualElement();
            section.style.marginBottom = 10;

            var titleLabel = new Label(title);
            titleLabel.style.fontSize = 14;
            titleLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            titleLabel.style.marginBottom = 5;
            section.Add(titleLabel);

            foreach (var item in items)
            {
                var guidPattern = @"\b([a-f0-9]{32})\b";
                var match = System.Text.RegularExpressions.Regex.Match(item, guidPattern);

                if (match.Success)
                {
                    var itemContainer = new VisualElement();
                    itemContainer.style.flexDirection = FlexDirection.Row;
                    itemContainer.style.alignItems = Align.Center;
                    itemContainer.style.marginLeft = 10;

                    var bulletLabel = new Label("  • ");
                    itemContainer.Add(bulletLabel);

                    var parts = item.Split(new[] { match.Value }, System.StringSplitOptions.None);

                    if (!string.IsNullOrEmpty(parts[0]))
                    {
                        var beforeLabel = new Label(parts[0]);
                        beforeLabel.style.fontSize = 12;
                        itemContainer.Add(beforeLabel);
                    }

                    var guidButton = CreateGuidButton(match.Value);
                    itemContainer.Add(guidButton);

                    if (parts.Length > 1 && !string.IsNullOrEmpty(parts[1]))
                    {
                        var afterLabel = new Label(parts[1]);
                        afterLabel.style.fontSize = 12;
                        itemContainer.Add(afterLabel);
                    }

                    section.Add(itemContainer);
                }
                else
                {
                    var itemLabel = new Label($"  • {item}");
                    itemLabel.style.marginLeft = 10;
                    section.Add(itemLabel);
                }
            }

            return section;
        }

        private VisualElement CreateRemappedFilesSection()
        {
            var section = new VisualElement();
            section.style.marginBottom = 20;
            section.style.marginTop = 10;

            var foldout = new Foldout();
            foldout.text = $"UUID Mappings ({_report.Mappings.Count})";
            foldout.value = false; // Start collapsed
            foldout.style.fontSize = 14;

            var scrollView = new ScrollView(ScrollViewMode.Vertical);
            scrollView.style.maxHeight = 400;
            scrollView.style.backgroundColor = new Color(0.15f, 0.15f, 0.15f, 0.05f);
            scrollView.style.paddingTop = 10;
            scrollView.style.paddingBottom = 10;
            scrollView.style.paddingLeft = 10;
            scrollView.style.paddingRight = 10;
            scrollView.style.borderTopLeftRadius = 5;
            scrollView.style.borderTopRightRadius = 5;
            scrollView.style.borderBottomLeftRadius = 5;
            scrollView.style.borderBottomRightRadius = 5;
            scrollView.style.marginTop = 8;

            var sortedMappings = _report.Mappings.OrderBy(m => m.AssetName);

            foreach (var mapping in sortedMappings)
            {
                var container = new VisualElement();
                container.style.backgroundColor = new Color(0.2f, 0.2f, 0.2f, 0.3f);
                container.style.paddingTop = 10;
                container.style.paddingBottom = 10;
                container.style.paddingLeft = 12;
                container.style.paddingRight = 12;
                container.style.marginBottom = 8;
                container.style.borderTopLeftRadius = 4;
                container.style.borderTopRightRadius = 4;
                container.style.borderBottomLeftRadius = 4;
                container.style.borderBottomRightRadius = 4;

                var fileButton = new Button(() =>
                {
                    string projectPath = ConvertToUnityAssetPath(mapping.AssetPath.Replace(".meta", ""));
                    SelectAssetInProject(projectPath);
                })
                {
                    text = ConvertToUnityAssetPath(mapping.AssetPath.Replace(".meta", ""))
                };
                fileButton.style.unityFontStyleAndWeight = FontStyle.Bold;
                fileButton.style.fontSize = 12;
                fileButton.style.unityTextAlign = TextAnchor.MiddleLeft;
                fileButton.style.backgroundColor = Color.clear;
                fileButton.style.borderLeftWidth = 0;
                fileButton.style.borderRightWidth = 0;
                fileButton.style.borderTopWidth = 0;
                fileButton.style.borderBottomWidth = 0;
                fileButton.style.marginBottom = 5;

                fileButton.RegisterCallback<MouseEnterEvent>((evt) =>
                {
                    fileButton.style.color = new Color(0.6f, 0.8f, 1f);
                });
                fileButton.RegisterCallback<MouseLeaveEvent>((evt) =>
                {
                    fileButton.style.color = Color.white;
                });

                container.Add(fileButton);

                var guidContainer = new VisualElement();
                guidContainer.style.flexDirection = FlexDirection.Row;
                guidContainer.style.alignItems = Align.Center;
                guidContainer.style.marginLeft = 10;

                var oldGuidButton = CreateGuidButton(mapping.OldGuid, null, new Color(0.3f, 0.15f, 0.15f, 0.2f));
                guidContainer.Add(oldGuidButton);

                var arrowLabel = new Label(" → ");
                arrowLabel.style.fontSize = 11;
                arrowLabel.style.marginLeft = 5;
                arrowLabel.style.marginRight = 5;
                guidContainer.Add(arrowLabel);

                var newGuidButton = CreateGuidButton(mapping.NewGuid, null, new Color(0.15f, 0.3f, 0.15f, 0.2f));
                guidContainer.Add(newGuidButton);

                container.Add(guidContainer);
                scrollView.Add(container);
            }

            foldout.Add(scrollView);
            section.Add(foldout);
            return section;
        }

        private VisualElement CreateNewFilesSection()
        {
            var section = new VisualElement();
            section.style.marginBottom = 20;
            section.style.marginTop = 10;

            var titleLabel = new Label($"New Files Imported ({_report.NewFilesImported.Count})");
            titleLabel.style.fontSize = 14;
            titleLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            titleLabel.style.marginBottom = 8;
            section.Add(titleLabel);

            var fileListContainer = new ScrollView(ScrollViewMode.Vertical);
            fileListContainer.style.maxHeight = 200;
            fileListContainer.style.backgroundColor = new Color(0.2f, 0.3f, 0.2f, 0.1f);
            fileListContainer.style.paddingTop = 10;
            fileListContainer.style.paddingBottom = 10;
            fileListContainer.style.paddingLeft = 10;
            fileListContainer.style.paddingRight = 10;
            fileListContainer.style.borderTopLeftRadius = 5;
            fileListContainer.style.borderTopRightRadius = 5;
            fileListContainer.style.borderBottomLeftRadius = 5;
            fileListContainer.style.borderBottomRightRadius = 5;

            foreach (var file in _report.NewFilesImported)
            {
                string projectPath = file;

                var fileContainer = new VisualElement();
                fileContainer.style.marginBottom = 5;

                var fileButton = new Button(() =>
                {
                    SelectAssetInProject(projectPath);
                })
                {
                    text = $"• {projectPath}"
                };
                fileButton.style.fontSize = 12;
                fileButton.style.marginLeft = 5;
                fileButton.style.color = new Color(0.5f, 1f, 0.5f);
                fileButton.style.unityTextAlign = TextAnchor.MiddleLeft;
                fileButton.style.backgroundColor = Color.clear;
                fileButton.style.borderLeftWidth = 0;
                fileButton.style.borderRightWidth = 0;
                fileButton.style.borderTopWidth = 0;
                fileButton.style.borderBottomWidth = 0;

                fileButton.RegisterCallback<MouseEnterEvent>((evt) =>
                {
                    fileButton.style.backgroundColor = new Color(0.3f, 0.4f, 0.3f, 0.2f);
                });
                fileButton.RegisterCallback<MouseLeaveEvent>((evt) =>
                {
                    fileButton.style.backgroundColor = Color.clear;
                });

                fileContainer.Add(fileButton);

                if (_report.FileDependencyUpdates != null && _report.FileDependencyUpdates.ContainsKey(projectPath))
                {
                    var dependencies = _report.FileDependencyUpdates[projectPath];
                    foreach (var dep in dependencies)
                    {
                        var depContainer = new VisualElement();
                        depContainer.style.flexDirection = FlexDirection.Row;
                        depContainer.style.alignItems = Align.Center;
                        depContainer.style.marginLeft = 25;
                        depContainer.style.marginTop = 2;
                        depContainer.style.marginBottom = 2;

                        var arrowLabel = new Label("→");
                        arrowLabel.style.fontSize = 10;
                        arrowLabel.style.color = new Color(0.7f, 0.7f, 0.7f);
                        arrowLabel.style.marginRight = 5;
                        depContainer.Add(arrowLabel);

                        var depButton = new Button(() =>
                        {
                            string depProjectPath = ConvertToUnityAssetPath(dep.DependencyPath.Replace(".meta", ""));
                            SelectAssetInProject(depProjectPath);
                        })
                        {
                            text = $"{dep.DependencyName}:"
                        };
                        depButton.style.fontSize = 10;
                        depButton.style.color = new Color(0.7f, 0.7f, 0.7f);
                        depButton.style.backgroundColor = Color.clear;
                        depButton.style.borderLeftWidth = 0;
                        depButton.style.borderRightWidth = 0;
                        depButton.style.borderTopWidth = 0;
                        depButton.style.borderBottomWidth = 0;
                        depButton.style.marginRight = 5;
                        depButton.style.unityTextAlign = TextAnchor.MiddleLeft;

                        depButton.RegisterCallback<MouseEnterEvent>((evt) =>
                        {
                            depButton.style.color = new Color(0.6f, 0.8f, 1f);
                        });
                        depButton.RegisterCallback<MouseLeaveEvent>((evt) =>
                        {
                            depButton.style.color = new Color(0.7f, 0.7f, 0.7f);
                        });

                        depContainer.Add(depButton);

                        var oldGuidButton = CreateGuidButton(dep.OldGuid, null, new Color(0.3f, 0.15f, 0.15f, 0.2f));
                        oldGuidButton.style.fontSize = 9;
                        depContainer.Add(oldGuidButton);

                        var depArrowLabel = new Label(" → ");
                        depArrowLabel.style.fontSize = 9;
                        depArrowLabel.style.marginLeft = 4;
                        depArrowLabel.style.marginRight = 4;
                        depContainer.Add(depArrowLabel);

                        var newGuidButton = CreateGuidButton(dep.NewGuid, null, new Color(0.15f, 0.3f, 0.15f, 0.2f));
                        newGuidButton.style.fontSize = 9;
                        depContainer.Add(newGuidButton);

                        fileContainer.Add(depContainer);
                    }
                }

                fileListContainer.Add(fileContainer);
            }

            section.Add(fileListContainer);
            return section;
        }

        private VisualElement CreateSkippedFilesSection()
        {
            var section = new VisualElement();
            section.style.marginBottom = 20;
            section.style.marginTop = 10;

            var titleLabel = new Label($"Files Skipped ({_report.SkippedFiles.Count})");
            titleLabel.style.fontSize = 14;
            titleLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            titleLabel.style.marginBottom = 8;
            section.Add(titleLabel);

            var fileListContainer = new ScrollView(ScrollViewMode.Vertical);
            fileListContainer.style.maxHeight = 200;
            fileListContainer.style.backgroundColor = new Color(0.3f, 0.3f, 0.2f, 0.1f);
            fileListContainer.style.paddingTop = 10;
            fileListContainer.style.paddingBottom = 10;
            fileListContainer.style.paddingLeft = 10;
            fileListContainer.style.paddingRight = 10;
            fileListContainer.style.borderTopLeftRadius = 5;
            fileListContainer.style.borderTopRightRadius = 5;
            fileListContainer.style.borderBottomLeftRadius = 5;
            fileListContainer.style.borderBottomRightRadius = 5;

            foreach (var file in _report.SkippedFiles)
            {
                var fileButton = new Button(() =>
                {
                    SelectAssetInProject(file);
                })
                {
                    text = $"• {file}"
                };
                fileButton.style.fontSize = 12;
                fileButton.style.marginLeft = 5;
                fileButton.style.marginBottom = 3;
                fileButton.style.color = new Color(1f, 1f, 0.5f);
                fileButton.style.unityTextAlign = TextAnchor.MiddleLeft;
                fileButton.style.backgroundColor = Color.clear;
                fileButton.style.borderLeftWidth = 0;
                fileButton.style.borderRightWidth = 0;
                fileButton.style.borderTopWidth = 0;
                fileButton.style.borderBottomWidth = 0;

                fileButton.RegisterCallback<MouseEnterEvent>((evt) =>
                {
                    fileButton.style.backgroundColor = new Color(0.4f, 0.4f, 0.3f, 0.2f);
                });
                fileButton.RegisterCallback<MouseLeaveEvent>((evt) =>
                {
                    fileButton.style.backgroundColor = Color.clear;
                });

                fileListContainer.Add(fileButton);
            }

            section.Add(fileListContainer);
            return section;
        }

        private VisualElement CreateExportSection()
        {
            var section = new VisualElement();
            section.style.marginTop = 20;
            section.style.paddingTop = 10;
            section.style.borderTopWidth = 1;
            section.style.borderTopColor = new Color(0.3f, 0.3f, 0.3f, 0.5f);

            var titleLabel = new Label("Export Report");
            titleLabel.style.fontSize = 14;
            titleLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            titleLabel.style.marginBottom = 5;
            section.Add(titleLabel);

            var buttonContainer = new VisualElement();
            buttonContainer.style.flexDirection = FlexDirection.Row;
            buttonContainer.style.marginTop = 5;

            var jsonButton = new Button(() =>
            {
                var json = _report.ToJson();
                GUIUtility.systemCopyBuffer = json;
                Debug.Log("Report exported to clipboard as JSON");
                EditorUtility.DisplayDialog("Export Complete", "Report has been copied to clipboard as JSON.", "OK");
            })
            {
                text = "Copy as JSON"
            };
            jsonButton.style.width = 120;
            jsonButton.style.marginRight = 10;
            buttonContainer.Add(jsonButton);

            var logButton = new Button(() =>
            {
                var json = _report.ToJson();
                Debug.Log("GUID Sync Report (JSON):\n" + json);
                EditorUtility.DisplayDialog("Export Complete", "Report has been logged to console as JSON.", "OK");
            })
            {
                text = "Log to Console"
            };
            logButton.style.width = 120;
            buttonContainer.Add(logButton);

            section.Add(buttonContainer);
            return section;
        }


        private Button CreateGuidButton(string guid, string label = null, Color? backgroundColor = null)
        {
            var button = new Button(() =>
            {
                GUIUtility.systemCopyBuffer = guid;
                Debug.Log($"Copied GUID to clipboard: {guid}");
            })
            {
                text = label ?? guid
            };

            button.style.fontSize = 9;
            button.style.backgroundColor = backgroundColor ?? new Color(0.15f, 0.15f, 0.2f, 0.3f);
            button.style.borderLeftWidth = 0;
            button.style.borderRightWidth = 0;
            button.style.borderTopWidth = 0;
            button.style.borderBottomWidth = 0;
            button.style.paddingLeft = 2;
            button.style.paddingRight = 2;

            return button;
        }

        private string ConvertToUnityAssetPath(string filePath)
        {
            string assetsKeyword = "Assets" + System.IO.Path.DirectorySeparatorChar;
            int assetsIndex = filePath.LastIndexOf(assetsKeyword);

            if (assetsIndex >= 0)
            {
                string relativePath = filePath.Substring(assetsIndex);
                return relativePath.Replace('\\', '/');
            }

            string fileName = System.IO.Path.GetFileNameWithoutExtension(filePath);
            string[] guids = AssetDatabase.FindAssets(fileName);
            if (guids.Length > 0)
            {
                return AssetDatabase.GUIDToAssetPath(guids[0]);
            }

            return filePath;
        }

        private void SelectAssetInProject(string assetPath)
        {
            UnityEngine.Object asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);

            if (asset != null)
            {
                EditorGUIUtility.PingObject(asset);

                Selection.activeObject = asset;

                EditorUtility.FocusProjectWindow();

                Debug.Log($"Selected asset: {assetPath}");
            }
            else
            {
                Debug.LogWarning($"Could not find asset at path: {assetPath}");
            }
        }
    }
}
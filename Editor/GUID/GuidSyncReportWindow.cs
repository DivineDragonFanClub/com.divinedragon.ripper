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

            var exportButtons = CreateExportButtons();
            root.Add(exportButtons);

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

            var fileIdLookup = BuildFileIdLookup();

            if (_report.NewFilesImported != null && _report.NewFilesImported.Count > 0)
            {
                var newFilesSection = CreateNewFilesSection(fileIdLookup);
                _scrollView.Add(newFilesSection);
            }

            if ((_report.Mappings != null && _report.Mappings.Count > 0) ||
                (_report.FileIdRemappings != null && _report.FileIdRemappings.Count > 0))
            {
                var remappedFilesSection = CreateRemappedFilesSection(CloneLookup(fileIdLookup));
                _scrollView.Add(remappedFilesSection);
            }

            if (_report.SkippedFiles != null && _report.SkippedFiles.Count > 0)
            {
                var skippedFilesSection = CreateSkippedFilesSection();
                _scrollView.Add(skippedFilesSection);
            }
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

        private Dictionary<string, List<FileIdRemapping>> BuildFileIdLookup()
        {
            var lookup = new Dictionary<string, List<FileIdRemapping>>();
            if (_report.FileIdRemappings != null)
            {
                foreach (var remap in _report.FileIdRemappings)
                {
                    var key = NormalizePath(remap.FilePath);
                    if (!lookup.TryGetValue(key, out var list))
                    {
                        list = new List<FileIdRemapping>();
                        lookup[key] = list;
                    }
                    list.Add(remap);
                }
            }
            return lookup;
        }

        private Dictionary<string, List<FileIdRemapping>> CloneLookup(Dictionary<string, List<FileIdRemapping>> source)
        {
            var clone = new Dictionary<string, List<FileIdRemapping>>();
            foreach (var kvp in source)
            {
                clone[kvp.Key] = new List<FileIdRemapping>(kvp.Value);
            }
            return clone;
        }

        private VisualElement CreateRemappedFilesSection(Dictionary<string, List<FileIdRemapping>> fileIdLookup)
        {
            var section = new VisualElement();
            section.style.marginBottom = 20;
            section.style.marginTop = 10;

            int guidCount = _report.Mappings?.Count ?? 0;
            int fileIdCount = _report.FileIdRemappings?.Count ?? 0;

            var foldout = new Foldout();
            foldout.text = $"Remapped IDs (GUID {guidCount}, FileID {fileIdCount})";
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

            var sortedMappings = (_report.Mappings ?? new List<GuidMapping>()).OrderBy(m => m.AssetName);

            foreach (var mapping in sortedMappings)
            {
                var displayPath = ConvertToUnityAssetPath(mapping.AssetPath.Replace(".meta", ""));
                var lookupKey = NormalizePath(displayPath);

                var container = BuildRemapContainer(displayPath, mapping);

                if (fileIdLookup.TryGetValue(lookupKey, out var remapsForFile))
                {
                    foreach (var remap in remapsForFile)
                    {
                        container.Add(BuildFileIdRow(remap));
                    }

                    fileIdLookup.Remove(lookupKey);
                }

                scrollView.Add(container);
            }

            foreach (var kvp in fileIdLookup.OrderBy(k => k.Key))
            {
                var container = BuildRemapContainer(kvp.Key);

                foreach (var remap in kvp.Value)
                {
                    container.Add(BuildFileIdRow(remap));
                }

                scrollView.Add(container);
            }

            foldout.Add(scrollView);
            section.Add(foldout);
            return section;
        }

        private VisualElement BuildRemapContainer(string displayPath, GuidMapping mapping = null)
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

            var resolvedPath = ConvertToUnityAssetPath(displayPath);

            var fileButton = new Button(() =>
            {
                SelectAssetInProject(resolvedPath);
            })
            {
                text = resolvedPath
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

            if (mapping != null)
            {
                var guidRow = new VisualElement();
                guidRow.style.flexDirection = FlexDirection.Row;
                guidRow.style.alignItems = Align.Center;
                guidRow.style.marginLeft = 10;
                guidRow.style.marginBottom = 4;

                var guidLabel = new Label("GUID:");
                guidLabel.style.fontSize = 9;
                guidLabel.style.marginRight = 6;
                guidRow.Add(guidLabel);

                var oldGuidButton = CreateGuidButton(mapping.OldGuid, null, new Color(0.3f, 0.15f, 0.15f, 0.2f));
                oldGuidButton.style.fontSize = 9;
                guidRow.Add(oldGuidButton);

                var arrowLabel = new Label(" → ");
                arrowLabel.style.fontSize = 9;
                arrowLabel.style.marginLeft = 6;
                arrowLabel.style.marginRight = 6;
                guidRow.Add(arrowLabel);

                var newGuidButton = CreateGuidButton(mapping.NewGuid, null, new Color(0.15f, 0.3f, 0.15f, 0.2f));
                newGuidButton.style.fontSize = 9;
                guidRow.Add(newGuidButton);

                container.Add(guidRow);
            }
            return container;
        }

        private VisualElement BuildFileIdRow(FileIdRemapping remap)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.marginLeft = 10;
            row.style.marginTop = 4;

            var label = new Label("FileID:");
            label.style.fontSize = 9;
            label.style.marginRight = 6;
            row.Add(label);

            var oldIdButton = CreateCopyButton(remap.OldFileId.ToString(), new Color(0.3f, 0.15f, 0.15f, 0.2f));
            oldIdButton.style.fontSize = 9;
            row.Add(oldIdButton);

            var arrowLabel = new Label(" → ");
            arrowLabel.style.fontSize = 9;
            arrowLabel.style.marginLeft = 6;
            arrowLabel.style.marginRight = 6;
            row.Add(arrowLabel);

            var newIdButton = CreateCopyButton(remap.NewFileId.ToString(), new Color(0.15f, 0.3f, 0.15f, 0.2f));
            newIdButton.style.fontSize = 9;
            row.Add(newIdButton);

            return row;
        }

        private string NormalizePath(string rawPath)
        {
            if (string.IsNullOrEmpty(rawPath))
            {
                return rawPath;
            }

            var withoutMeta = rawPath.Replace(".meta", string.Empty);
            return withoutMeta.Replace('\\', '/');
        }

        private VisualElement CreateNewFilesSection(Dictionary<string, List<FileIdRemapping>> fileIdLookup)
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
                fileContainer.style.marginBottom = 8;

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

                var fileLevelFileIds = fileIdLookup.TryGetValue(projectPath, out var fileRemaps)
                    ? fileRemaps
                    : null;
                if (fileLevelFileIds != null)
                {
                    foreach (var remap in fileLevelFileIds)
                    {
                        fileContainer.Add(BuildFileIdRow(remap));
                    }
                }

                if (_report.FileDependencyUpdates != null && _report.FileDependencyUpdates.ContainsKey(projectPath))
                {
                    var dependencies = _report.FileDependencyUpdates[projectPath];
                    foreach (var dep in dependencies)
                    {
                        var depColumn = new VisualElement();
                        depColumn.style.marginLeft = 25;
                        depColumn.style.marginTop = 2;
                        depColumn.style.marginBottom = 4;
                        depColumn.style.flexDirection = FlexDirection.Column;

                        var depHeader = new VisualElement();
                        depHeader.style.flexDirection = FlexDirection.Row;
                        depHeader.style.alignItems = Align.Center;

                        var arrowLabel = new Label("→");
                        arrowLabel.style.fontSize = 10;
                        arrowLabel.style.color = new Color(0.7f, 0.7f, 0.7f);
                        arrowLabel.style.marginRight = 5;
                        depHeader.Add(arrowLabel);

                        var depButton = new Button(() =>
                        {
                            string depProjectPath = ConvertToUnityAssetPath(dep.DependencyPath.Replace(".meta", ""));
                            SelectAssetInProject(depProjectPath);
                        })
                        {
                            text = dep.DependencyName
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

                        depHeader.Add(depButton);
                        depColumn.Add(depHeader);

                        var guidRow = new VisualElement();
                        guidRow.style.flexDirection = FlexDirection.Row;
                        guidRow.style.alignItems = Align.Center;
                        guidRow.style.marginLeft = 15;
                        guidRow.style.marginTop = 2;

                        var guidLabel = new Label("GUID:");
                        guidLabel.style.fontSize = 9;
                        guidLabel.style.marginRight = 6;
                        guidRow.Add(guidLabel);

                        var oldGuidButton = CreateGuidButton(dep.OldGuid, null, new Color(0.3f, 0.15f, 0.15f, 0.2f));
                        oldGuidButton.style.fontSize = 9;
                        guidRow.Add(oldGuidButton);

                        var guidArrow = new Label(" → ");
                        guidArrow.style.fontSize = 9;
                        guidArrow.style.marginLeft = 4;
                        guidArrow.style.marginRight = 4;
                        guidRow.Add(guidArrow);

                        var newGuidButton = CreateGuidButton(dep.NewGuid, null, new Color(0.15f, 0.3f, 0.15f, 0.2f));
                        newGuidButton.style.fontSize = 9;
                        guidRow.Add(newGuidButton);

                        depColumn.Add(guidRow);

                        var depPathNormalized = NormalizePath(ConvertToUnityAssetPath(dep.DependencyPath.Replace(".meta", "")));
                        if (fileIdLookup.TryGetValue(depPathNormalized, out var depFileIds))
                        {
                            foreach (var remap in depFileIds)
                            {
                                depColumn.Add(BuildFileIdRow(remap));
                            }
                        }

                        fileContainer.Add(depColumn);
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

        private VisualElement CreateExportButtons()
        {
            var buttonContainer = new VisualElement();
            buttonContainer.style.flexDirection = FlexDirection.Row;
            buttonContainer.style.marginBottom = 10;

            var jsonButton = new Button(() =>
            {
                string timestamp = System.DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
                string defaultName = $"GuidSyncReport_{timestamp}.json";
                string path = EditorUtility.SaveFilePanel(
                    "Save GUID Sync Report",
                    "",
                    defaultName,
                    "json"
                );

                if (!string.IsNullOrEmpty(path))
                {
                    var json = _report.ToJson();
                    System.IO.File.WriteAllText(path, json);
                    Debug.Log($"Report saved to: {path}");
                    EditorUtility.RevealInFinder(path);
                }
            })
            {
                text = "Export Report to JSON"
            };
            jsonButton.style.width = 140;
            buttonContainer.Add(jsonButton);

            return buttonContainer;
        }


        private Button CreateGuidButton(string guid, string label = null, Color? backgroundColor = null)
        {
            var button = new Button(() =>
            {
                GUIUtility.systemCopyBuffer = guid;
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

        private Button CreateCopyButton(string value, Color? backgroundColor = null)
        {
            var button = new Button(() =>
            {
                GUIUtility.systemCopyBuffer = value;
            })
            {
                text = value
            };

            button.style.fontSize = 9;
            button.style.backgroundColor = backgroundColor ?? new Color(0.2f, 0.2f, 0.2f, 0.3f);
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

            }
            else
            {
                Debug.LogWarning($"Could not find asset at path: {assetPath}");
            }
        }
    }
}

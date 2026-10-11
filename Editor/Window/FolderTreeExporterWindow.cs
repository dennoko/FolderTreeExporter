using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace dennokoworks.FolderTreeExporter
{
    /// <summary>
    /// 指定フォルダの階層構造をテキストツリーとしてプレビュー・コピー・保存するウィンドウ。
    /// UI Toolkit (UXML/USS) および dennokoworks フローティングデザインシステムに準拠。
    /// </summary>
    public class FolderTreeExporterWindow : EditorWindow
    {
        private const string WindowTitle = "FolderTreeExporter";

        // UXML / USS の GUID
        private const string UXML_GUID = "994b958f258b4a32969e96d541cc0cbd";
        private const string USS_GUID  = "77885b25f67d49f8bc8942fc6734b65a";

        // フォールバックパス
        private const string FallbackUxmlPath = "Assets/dennokoworks/FolderTreeExporter/Editor/UI/FolderTreeExporterWindow.uxml";
        private const string FallbackUssPath  = "Assets/dennokoworks/FolderTreeExporter/Editor/UI/DennokoTheme.uss";

        private const int PreviewMaxChars = 12000;

        private static readonly string[] ModeChoices = { "フォルダ + アセットタイプ", "フォルダのみ", "ファイル込み" };
        private static readonly string[] StyleChoices = { "標準ツリー（Unicode罫線）", "ASCIIツリー", "Markdownリスト" };

        public enum StatusType { Info, Success, Error }

        // ─── 設定（ドメインリロードをまたいで保持） ──────────────────────────
        [SerializeField] private DefaultAsset _folder;
        [SerializeField] private TreeOutputMode _mode = TreeOutputMode.FoldersWithAssetTypes;
        [SerializeField] private TreeStyle _style = TreeStyle.Unicode;
        [SerializeField] private bool _showAssetCounts;
        [SerializeField] private bool _wrapInCodeBlock;

        // ─── 生成結果 ────────────────────────────────────────────────────────
        private string _treeText = string.Empty;

        // ─── UI 要素参照 ─────────────────────────────────────────────────────
        private ObjectField _folderField;
        private Button _browseButton;
        private VisualElement _dropArea;
        private DropdownField _modeDropdown;
        private DropdownField _styleDropdown;
        private Toggle _showCountsToggle;
        private Toggle _codeBlockToggle;
        private Button _refreshButton;
        private TextField _previewField;
        private Button _copyButton;
        private Button _saveButton;
        private Label _statusLabel;
        private Label _versionLabel;
        private Button _versionReloadButton;

        private IVisualElementScheduledItem _statusResetSchedule;
        private DennokoVersionChecker.Result _versionResult =
            new DennokoVersionChecker.Result { State = DennokoVersionChecker.State.Checking, LocalVersion = "0.0.0" };

        // ─── Window Registration ─────────────────────────────────────────────
        [MenuItem("dennokoworks/FolderTreeExporter")]
        public static void ShowWindow()
        {
            Open();
        }

        [MenuItem("Assets/dennokoworks/Export Folder Tree", false, 2000)]
        private static void ExportFolderTreeFromContextMenu()
        {
            DefaultAsset folder = GetSelectedFolder();
            if (folder != null) Open().SetFolder(folder);
        }

        [MenuItem("Assets/dennokoworks/Export Folder Tree", true)]
        private static bool ValidateExportFolderTreeFromContextMenu()
        {
            return GetSelectedFolder() != null;
        }

        private static FolderTreeExporterWindow Open()
        {
            var window = GetWindow<FolderTreeExporterWindow>(WindowTitle);
            window.titleContent = new GUIContent(WindowTitle);
            window.minSize = new Vector2(380, 420);
            window.Show();
            return window;
        }

        private static DefaultAsset GetSelectedFolder()
        {
            string[] guids = Selection.assetGUIDs;
            if (guids == null || guids.Length == 0) return null;
            return LoadFolder(AssetDatabase.GUIDToAssetPath(guids[0]));
        }

        private static DefaultAsset LoadFolder(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath) || !AssetDatabase.IsValidFolder(assetPath)) return null;
            return AssetDatabase.LoadAssetAtPath<DefaultAsset>(assetPath);
        }

        private static string GetValidFolderPath(DefaultAsset folder)
        {
            if (folder == null) return null;
            string path = AssetDatabase.GetAssetPath(folder);
            return AssetDatabase.IsValidFolder(path) ? path : null;
        }

        // ─── UI Toolkit Lifecycle ────────────────────────────────────────────
        public void CreateGUI()
        {
            VisualElement root = rootVisualElement;
            root.Clear();

            // テーマ非依存のためのルートクラスを適用
            root.AddToClassList("dennoko-root");
            root.style.backgroundColor = (Color)new Color32(0x12, 0x12, 0x12, 0xFF);
            root.style.flexGrow = 1;

            // 標準フォント（メイリオ SDF）の適用
            DennokoUIFont.Apply(root);

            // USS のロードと適用
            string ussPath = AssetDatabase.GUIDToAssetPath(USS_GUID);
            if (string.IsNullOrEmpty(ussPath)) ussPath = FallbackUssPath;
            var uss = AssetDatabase.LoadAssetAtPath<StyleSheet>(ussPath);
            if (uss != null)
            {
                root.styleSheets.Add(uss);
            }
            else
            {
                Debug.LogWarning($"[FolderTreeExporterWindow] USS が見つかりません: {ussPath}");
            }

            // UXML のロードとインスタンス化
            string uxmlPath = AssetDatabase.GUIDToAssetPath(UXML_GUID);
            if (string.IsNullOrEmpty(uxmlPath)) uxmlPath = FallbackUxmlPath;
            var uxml = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(uxmlPath);
            if (uxml == null)
            {
                root.Add(new Label($"UXML Asset が見つかりません: {uxmlPath}"));
                return;
            }
            uxml.CloneTree(root);

            InitializeUI(root);
            UpdateUIFromState();
            Regenerate();

            StartVersionCheck();
        }

        private void OnProjectChange()
        {
            Regenerate();
        }

        // ─── UI 初期化 & バインディング ──────────────────────────────────────
        private void InitializeUI(VisualElement root)
        {
            _versionLabel = root.Q<Label>("version-label");
            _versionReloadButton = root.Q<Button>("version-reload-button");
            if (_versionReloadButton != null)
            {
                _versionReloadButton.tooltip = "アップデートを再確認";
                _versionReloadButton.clicked += () =>
                {
                    FolderTreeExporterVersion.ForceRecheck();
                    LoadVersionResultFromSessionState();
                };
            }

            _folderField = root.Q<ObjectField>("folder-field");
            if (_folderField != null)
            {
                _folderField.objectType = typeof(DefaultAsset);
                _folderField.allowSceneObjects = false;
                _folderField.RegisterValueChangedCallback(evt =>
                {
                    var newFolder = evt.newValue as DefaultAsset;
                    if (newFolder != null && GetValidFolderPath(newFolder) == null)
                    {
                        SetStatus("指定されたアセットは有効なフォルダではありません。", StatusType.Error);
                        _folderField.SetValueWithoutNotify(_folder);
                        return;
                    }
                    SetFolder(newFolder);
                });
            }

            _browseButton = root.Q<Button>("browse-button");
            if (_browseButton != null)
            {
                _browseButton.clicked += BrowseFolder;
            }

            _dropArea = root.Q<VisualElement>("folder-drop-area");
            if (_dropArea != null)
            {
                SetupDropArea(_dropArea);
            }

            _modeDropdown = root.Q<DropdownField>("mode-dropdown");
            if (_modeDropdown != null)
            {
                // 初期値は UpdateUIFromState() が通知なしで流し込む。ここで index / value を
                // 代入すると変更イベントが飛び、起動時にツリー生成が余分に走る。
                _modeDropdown.choices = new List<string>(ModeChoices);
                _modeDropdown.RegisterValueChangedCallback(evt =>
                {
                    int idx = Array.IndexOf(ModeChoices, evt.newValue);
                    if (idx >= 0)
                    {
                        _mode = (TreeOutputMode)idx;
                        UpdateOptionAvailability();
                        Regenerate();
                    }
                });
            }

            _styleDropdown = root.Q<DropdownField>("style-dropdown");
            if (_styleDropdown != null)
            {
                _styleDropdown.choices = new List<string>(StyleChoices);
                _styleDropdown.RegisterValueChangedCallback(evt =>
                {
                    int idx = Array.IndexOf(StyleChoices, evt.newValue);
                    if (idx >= 0)
                    {
                        _style = (TreeStyle)idx;
                        Regenerate();
                    }
                });
            }

            _showCountsToggle = root.Q<Toggle>("show-counts-toggle");
            if (_showCountsToggle != null)
            {
                _showCountsToggle.RegisterValueChangedCallback(evt =>
                {
                    _showAssetCounts = evt.newValue;
                    Regenerate();
                });
            }

            _codeBlockToggle = root.Q<Toggle>("code-block-toggle");
            if (_codeBlockToggle != null)
            {
                _codeBlockToggle.RegisterValueChangedCallback(evt =>
                {
                    _wrapInCodeBlock = evt.newValue;
                    Regenerate();
                });
            }

            _refreshButton = root.Q<Button>("refresh-button");
            if (_refreshButton != null)
            {
                _refreshButton.clicked += Regenerate;
            }

            _previewField = root.Q<TextField>("preview-field");

            _copyButton = root.Q<Button>("copy-button");
            if (_copyButton != null)
            {
                _copyButton.clicked += CopyToClipboard;
            }

            _saveButton = root.Q<Button>("save-button");
            if (_saveButton != null)
            {
                _saveButton.clicked += ShowSaveMenu;
            }

            _statusLabel = root.Q<Label>("status-label");
        }

        private void UpdateUIFromState()
        {
            if (_folderField != null) _folderField.SetValueWithoutNotify(_folder);
            if (_modeDropdown != null) _modeDropdown.SetValueWithoutNotify(ModeChoices[(int)_mode]);
            if (_styleDropdown != null) _styleDropdown.SetValueWithoutNotify(StyleChoices[(int)_style]);
            if (_showCountsToggle != null) _showCountsToggle.SetValueWithoutNotify(_showAssetCounts);
            if (_codeBlockToggle != null) _codeBlockToggle.SetValueWithoutNotify(_wrapInCodeBlock);
            UpdateOptionAvailability();
        }

        private void UpdateOptionAvailability()
        {
            if (_showCountsToggle != null)
            {
                _showCountsToggle.SetEnabled(_mode == TreeOutputMode.FoldersWithAssetTypes);
            }
        }

        private void SetupDropArea(VisualElement dropArea)
        {
            dropArea.RegisterCallback<DragUpdatedEvent>(evt =>
            {
                DefaultAsset folder = GetDraggedFolder();
                if (folder != null)
                {
                    DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                    dropArea.AddToClassList("folder-drop-area--dragover");
                }
                else
                {
                    DragAndDrop.visualMode = DragAndDropVisualMode.Rejected;
                    dropArea.RemoveFromClassList("folder-drop-area--dragover");
                }
                evt.StopPropagation();
            });

            dropArea.RegisterCallback<DragLeaveEvent>(evt =>
            {
                dropArea.RemoveFromClassList("folder-drop-area--dragover");
                evt.StopPropagation();
            });

            dropArea.RegisterCallback<DragPerformEvent>(evt =>
            {
                dropArea.RemoveFromClassList("folder-drop-area--dragover");
                DefaultAsset folder = GetDraggedFolder();
                if (folder != null)
                {
                    DragAndDrop.AcceptDrag();
                    SetFolder(folder);
                }
                evt.StopPropagation();
            });
        }

        private static DefaultAsset GetDraggedFolder()
        {
            foreach (string path in DragAndDrop.paths)
            {
                DefaultAsset folder = LoadFolder(path);
                if (folder != null) return folder;
            }
            return null;
        }

        // ─── ロジック ────────────────────────────────────────────────────────
        public void SetFolder(DefaultAsset folder)
        {
            _folder = folder;
            if (_folderField != null) _folderField.SetValueWithoutNotify(_folder);
            Regenerate();
        }

        private void Regenerate()
        {
            string path = GetValidFolderPath(_folder);
            if (string.IsNullOrEmpty(path))
            {
                _treeText = string.Empty;
                if (_previewField != null) _previewField.value = "フォルダを指定するとツリーが表示されます。";
                _refreshButton?.SetEnabled(false);
                SetActionButtonsEnabled(false);
                return;
            }
            _refreshButton?.SetEnabled(true);

            _treeText = FolderTreeBuilder.Build(path, new TreeBuildOptions
            {
                Mode = _mode,
                Style = _style,
                ShowAssetCounts = _showAssetCounts,
                WrapInCodeBlock = _wrapInCodeBlock,
            });

            if (_previewField != null)
            {
                if (_treeText.Length > PreviewMaxChars)
                {
                    int cut = _treeText.LastIndexOf('\n', PreviewMaxChars);
                    string preview = _treeText.Substring(0, cut > 0 ? cut : PreviewMaxChars);
                    _previewField.value = preview + "\n\n... (プレビューは一部のみ表示されています。コピー・保存は全体が対象です)";
                }
                else
                {
                    _previewField.value = _treeText.TrimEnd('\n');
                }
            }

            SetActionButtonsEnabled(!string.IsNullOrEmpty(_treeText));
        }

        private void SetActionButtonsEnabled(bool enabled)
        {
            _copyButton?.SetEnabled(enabled);
            _saveButton?.SetEnabled(enabled);
        }

        private void CopyToClipboard()
        {
            if (string.IsNullOrEmpty(_treeText)) return;
            EditorGUIUtility.systemCopyBuffer = _treeText;
            SetStatus("クリップボードにコピーしました", StatusType.Success);
        }

        private void ShowSaveMenu()
        {
            if (string.IsNullOrEmpty(_treeText)) return;
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("テキスト (.txt)"), false, () => SaveToFile("txt"));
            menu.AddItem(new GUIContent("Markdown (.md)"), false, () => SaveToFile("md"));
            menu.ShowAsContext();
        }

        private void SaveToFile(string extension)
        {
            string folderName = Path.GetFileName(GetValidFolderPath(_folder)) ?? "tree";
            string defaultName = folderName + "_tree";
            string path = EditorUtility.SaveFilePanel("フォルダツリーを保存", string.Empty, defaultName, extension);
            if (string.IsNullOrEmpty(path)) return;

            try
            {
                File.WriteAllText(path, _treeText, new UTF8Encoding(false));
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Debug.LogError($"[FolderTreeExporter] 保存に失敗しました: {path}\n{e.Message}");
                SetStatus("ファイルの保存に失敗しました: " + e.Message, StatusType.Error);
                return;
            }

            if (!string.IsNullOrEmpty(FileUtil.GetProjectRelativePath(path.Replace('\\', '/'))))
                AssetDatabase.Refresh();

            SetStatus("保存しました: " + Path.GetFileName(path), StatusType.Success);
        }

        private void BrowseFolder()
        {
            string start = GetValidFolderPath(_folder) ?? "Assets";
            string selected = EditorUtility.OpenFolderPanel("フォルダを選択", start, string.Empty);
            if (string.IsNullOrEmpty(selected)) return;

            DefaultAsset folder = LoadFolder(FileUtil.GetProjectRelativePath(selected.Replace('\\', '/')));
            if (folder == null)
            {
                EditorUtility.DisplayDialog(
                    WindowTitle, "プロジェクト内（Assets 配下）のフォルダを選択してください。", "OK");
                return;
            }
            SetFolder(folder);
        }

        // ─── ステータスバー ──────────────────────────────────────────────────
        private void SetStatus(string message, StatusType type, long autoResetMs = 3000)
        {
            if (_statusLabel == null) return;

            _statusLabel.text = message;
            _statusLabel.EnableInClassList("dennoko-status--success", type == StatusType.Success);
            _statusLabel.EnableInClassList("dennoko-status--error",   type == StatusType.Error);

            _statusResetSchedule?.Pause();
            if (type != StatusType.Info)
            {
                _statusResetSchedule = _statusLabel.schedule
                    .Execute(() => SetStatus("Ready", StatusType.Info))
                    .StartingIn(autoResetMs);
            }
        }

        // ─── バージョンチェック ──────────────────────────────────────────────
        private void StartVersionCheck()
        {
            LoadVersionResultFromSessionState();
            FolderTreeExporterVersion.StartCheckBackgroundTask();
        }

        internal void LoadVersionResultFromSessionState()
        {
            string local  = FolderTreeExporterVersion.Current;
            string latest = SessionState.GetString(FolderTreeExporterVersion.VerCheckLatestKey, string.Empty);
            bool   done   = SessionState.GetBool(FolderTreeExporterVersion.VerCheckDoneKey, false);
            bool   error  = SessionState.GetBool(FolderTreeExporterVersion.VerCheckErrorKey, false);

            DennokoVersionChecker.State state;
            if (!done)
                state = DennokoVersionChecker.State.Checking;
            else if (error || string.IsNullOrEmpty(latest))
                state = DennokoVersionChecker.State.Error;
            else if (DennokoVersionChecker.IsUpdateAvailable(latest, local))
                state = DennokoVersionChecker.State.UpdateAvailable;
            else
                state = DennokoVersionChecker.State.UpToDate;

            _versionResult = new DennokoVersionChecker.Result
            {
                State = state,
                LocalVersion = local,
                LatestVersion = latest,
                Url = SessionState.GetString(FolderTreeExporterVersion.VerCheckUrlKey, string.Empty),
                Message = SessionState.GetString(FolderTreeExporterVersion.VerCheckMessageKey, string.Empty)
            };
            ApplyVersionLabel();
        }

        private void ApplyVersionLabel()
        {
            if (_versionLabel == null) return;

            var r = _versionResult;
            string baseText = "v" + r.LocalVersion;
            string text;
            bool update = false, error = false;

            switch (r.State)
            {
                case DennokoVersionChecker.State.UpdateAvailable:
                    text = $"{baseText}  更新あり {r.LatestVersion}";
                    update = true;
                    break;
                case DennokoVersionChecker.State.Error:
                    text = $"{baseText}  最新版を取得できません";
                    error = true;
                    break;
                case DennokoVersionChecker.State.Checking:
                    text = $"{baseText}  確認中...";
                    break;
                default:
                    text = baseText;
                    break;
            }

            _versionLabel.text = text;
            _versionLabel.EnableInClassList("dennoko-version-label--update", update);
            _versionLabel.EnableInClassList("dennoko-version-label--error", error);
        }
    }
}

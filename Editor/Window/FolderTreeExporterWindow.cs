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
    /// 日本語（デフォルト）および英語の多言語表示、プレビューの手動編集に対応。
    /// </summary>
    public class FolderTreeExporterWindow : EditorWindow
    {
        private const string WindowTitle = "FolderTreeExporter";
        private const string LanguagePrefKey = "FolderTreeExporter_Language";

        // UXML / USS の GUID
        private const string UXML_GUID = "994b958f258b4a32969e96d541cc0cbd";
        private const string USS_GUID  = "77885b25f67d49f8bc8942fc6734b65a";

        // フォールバックパス
        private const string FallbackUxmlPath = "Assets/dennokoworks/FolderTreeExporter/Editor/UI/FolderTreeExporterWindow.uxml";
        private const string FallbackUssPath  = "Assets/dennokoworks/FolderTreeExporter/Editor/UI/DennokoTheme.uss";

        private const int PreviewMaxChars = 12000;

        public enum UILanguage { Japanese, English }
        public enum StatusType { Info, Success, Error }

        // ─── 設定（ドメインリロードをまたいで保持） ──────────────────────────
        [SerializeField] private DefaultAsset _folder;
        [SerializeField] private TreeOutputMode _mode = TreeOutputMode.FoldersWithAssetTypes;
        [SerializeField] private TreeStyle _style = TreeStyle.Unicode;
        [SerializeField] private bool _showAssetCounts;
        [SerializeField] private bool _wrapInCodeBlock;
        [SerializeField] private bool _isManualEdit;
        // 手動編集した内容。ドメインリロードやレイアウト復元で失われないよう保持する。
        [SerializeField] private bool _manualDirty;
        [SerializeField] private string _manualText = string.Empty;

        private UILanguage _currentLanguage = UILanguage.Japanese;

        // ─── 生成結果 ────────────────────────────────────────────────────────
        private string _treeText = string.Empty;

        // ─── UI 要素参照 ─────────────────────────────────────────────────────
        private Label _targetFolderTitle;
        private ObjectField _folderField;
        private Button _browseButton;
        private VisualElement _dropArea;
        private Label _dropAreaText;

        private Label _settingsTitle;
        private DropdownField _modeDropdown;
        private DropdownField _styleDropdown;
        private Toggle _showCountsToggle;
        private Toggle _codeBlockToggle;

        private Label _previewTitle;
        private Toggle _manualEditToggle;
        private Button _refreshButton;
        private TextField _previewField;

        private Button _copyButton;
        private Button _saveButton;
        private Label _statusLabel;

        private Label _versionLabel;
        private Button _versionReloadButton;
        private Button _langButton;

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

        // ─── UI Toolkit Lifecycle ────────────────────────────────────
        public void CreateGUI()
        {
            LoadLanguagePref();

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
            ApplyLocalization();
            UpdateUIFromState();
            if (_isManualEdit && _manualDirty && _previewField != null)
                _previewField.SetValueWithoutNotify(_manualText);
            Regenerate();

            StartVersionCheck();
        }

        private void OnProjectChange()
        {
            Regenerate();
        }

        // ─── ローカライズ関連 ────────────────────────────────────────────────
        private void LoadLanguagePref()
        {
            string pref = EditorPrefs.GetString(LanguagePrefKey, "ja");
            _currentLanguage = string.Equals(pref, "en", StringComparison.OrdinalIgnoreCase)
                ? UILanguage.English
                : UILanguage.Japanese;
        }

        private string Loc(string ja, string en) => _currentLanguage == UILanguage.English ? en : ja;

        private void ToggleLanguage()
        {
            _currentLanguage = _currentLanguage == UILanguage.Japanese ? UILanguage.English : UILanguage.Japanese;
            EditorPrefs.SetString(LanguagePrefKey, _currentLanguage == UILanguage.English ? "en" : "ja");
            ApplyLocalization();
            UpdateUIFromState();
            Regenerate();
        }

        private void ApplyLocalization()
        {
            // 言語切替ボタン
            if (_langButton != null)
            {
                _langButton.text = _currentLanguage == UILanguage.Japanese ? "EN" : "JA";
                _langButton.tooltip = Loc("言語を英語に切り替え", "日本語に切り替え");
            }

            if (_versionReloadButton != null)
            {
                _versionReloadButton.tooltip = Loc("アップデートを再確認", "Check for updates");
            }

            // カード 1: 対象フォルダ
            if (_targetFolderTitle != null) _targetFolderTitle.text = Loc("対象フォルダ", "TARGET FOLDER");
            if (_folderField != null) _folderField.label = Loc("フォルダ", "Folder");
            if (_browseButton != null) _browseButton.text = Loc("参照", "Browse");
            if (_dropAreaText != null)
                _dropAreaText.text = Loc("ここに Project ウィンドウからフォルダをドラッグ＆ドロップ", "Drag & drop folder from Project window here");

            // カード 2: 設定・オプション
            if (_settingsTitle != null) _settingsTitle.text = Loc("設定", "SETTINGS");
            if (_modeDropdown != null)
            {
                _modeDropdown.label = Loc("出力モード", "Output Mode");
                _modeDropdown.choices = GetModeChoices();
                _modeDropdown.SetValueWithoutNotify(_modeDropdown.choices[(int)_mode]);
            }
            if (_styleDropdown != null)
            {
                _styleDropdown.label = Loc("ツリータイプ", "Tree Style");
                _styleDropdown.choices = GetStyleChoices();
                _styleDropdown.SetValueWithoutNotify(_styleDropdown.choices[(int)_style]);
            }
            if (_showCountsToggle != null)
            {
                _showCountsToggle.text = Loc("アセット件数を表示", "Show Asset Counts");
                _showCountsToggle.tooltip = Loc(
                    "「フォルダ + アセットタイプ」モードで、種別ごとのファイル件数を併記します。",
                    "In 'Folders + Asset Types' mode, appends the count of files for each type.");
            }
            if (_codeBlockToggle != null)
            {
                _codeBlockToggle.text = Loc("Markdownコードブロックで囲む (```)", "Wrap in Code Block (```)");
                _codeBlockToggle.tooltip = Loc(
                    "出力全体を ``` で囲みます。Discord や GitHub 等へ貼り付ける際の桁ズレ防止用です。",
                    "Encloses output in ```. Prevents misaligned text on Discord or GitHub.");
            }

            // カード 3: プレビュー
            if (_previewTitle != null) _previewTitle.text = Loc("プレビュー", "PREVIEW");
            if (_manualEditToggle != null)
            {
                _manualEditToggle.text = Loc("手動編集", "Manual Edit");
                _manualEditToggle.tooltip = Loc(
                    "プレビューのツリーを直接編集できるようにします（注釈の追記など）。\nプレビューが一部のみ表示されている場合は利用できません。",
                    "Allows editing the preview tree directly (e.g. adding annotations).\nUnavailable while the preview is truncated.");
            }
            if (_refreshButton != null) _refreshButton.text = Loc("更新", "Refresh");

            // フッター & アクション
            if (_copyButton != null) _copyButton.text = Loc("クリップボードにコピー", "Copy to Clipboard");
            if (_saveButton != null) _saveButton.text = Loc("ファイルに保存", "Save to File");

            // バージョンラベル更新
            ApplyVersionLabel();
        }

        private List<string> GetModeChoices()
        {
            return _currentLanguage == UILanguage.English
                ? new List<string> { "Folders + Asset Types", "Folders Only", "With Files" }
                : new List<string> { "フォルダ + アセットタイプ", "フォルダのみ", "ファイル込み" };
        }

        private List<string> GetStyleChoices()
        {
            return _currentLanguage == UILanguage.English
                ? new List<string> { "Standard Tree (Unicode)", "ASCII Tree", "Markdown List" }
                : new List<string> { "標準ツリー（Unicode罫線）", "ASCIIツリー", "Markdownリスト" };
        }

        // ─── UI 初期化 & バインディング ──────────────────────────────────────
        private void InitializeUI(VisualElement root)
        {
            _versionLabel = root.Q<Label>("version-label");
            _versionReloadButton = root.Q<Button>("version-reload-button");
            if (_versionReloadButton != null)
            {
                _versionReloadButton.clicked += () =>
                {
                    FolderTreeExporterVersion.ForceRecheck();
                    LoadVersionResultFromSessionState();
                };
            }

            _langButton = root.Q<Button>("lang-button");
            if (_langButton != null)
            {
                _langButton.clicked += ToggleLanguage;
            }

            _targetFolderTitle = root.Q<Label>("target-folder-title");
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
                        SetStatus(Loc("指定されたアセットは有効なフォルダではありません。", "The specified asset is not a valid folder."), StatusType.Error);
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
            _dropAreaText = root.Q<Label>("drop-area-text");
            if (_dropArea != null)
            {
                SetupDropArea(_dropArea);
            }

            _settingsTitle = root.Q<Label>("settings-title");
            _modeDropdown = root.Q<DropdownField>("mode-dropdown");
            if (_modeDropdown != null)
            {
                _modeDropdown.RegisterValueChangedCallback(_ =>
                {
                    int idx = _modeDropdown.index;
                    if (idx >= 0 && idx < 3)
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
                _styleDropdown.RegisterValueChangedCallback(_ =>
                {
                    int idx = _styleDropdown.index;
                    if (idx >= 0 && idx < 3)
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

            _previewTitle = root.Q<Label>("preview-title");

            _manualEditToggle = root.Q<Toggle>("manual-edit-toggle");
            if (_manualEditToggle != null)
            {
                _manualEditToggle.RegisterValueChangedCallback(OnManualEditToggleChanged);
            }

            _refreshButton = root.Q<Button>("refresh-button");
            if (_refreshButton != null)
            {
                _refreshButton.clicked += () =>
                {
                    if (ConfirmOverwriteManualEdits()) Regenerate(force: true);
                };
            }

            _previewField = root.Q<TextField>("preview-field");
            if (_previewField != null)
            {
                // プログラムからの書き込みは SetValueWithoutNotify で行うため、ここに来るのはユーザーの編集のみ。
                _previewField.RegisterValueChangedCallback(evt =>
                {
                    if (!_isManualEdit) return;
                    _manualText = evt.newValue;
                    _manualDirty = true;
                    UpdateActionButtons();
                });
            }

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

        private void OnManualEditToggleChanged(ChangeEvent<bool> evt)
        {
            if (evt.newValue)
            {
                _isManualEdit = true;
                if (_previewField != null) _previewField.isReadOnly = false;
                return;
            }

            if (_manualDirty && !EditorUtility.DisplayDialog(
                    WindowTitle,
                    Loc("手動編集を終了して再生成しますか？\n手動で編集した内容は破棄されます。",
                        "Exit manual editing and regenerate?\nYour manual edits will be discarded."),
                    Loc("破棄して再生成", "Discard & Regenerate"),
                    Loc("キャンセル", "Cancel")))
            {
                _manualEditToggle.SetValueWithoutNotify(true);
                return;
            }
            _isManualEdit = false;
            Regenerate(force: true);
        }

        /// <summary>手動編集した内容を上書きしてよいか確認する。編集が無ければ確認せず true。</summary>
        private bool ConfirmOverwriteManualEdits()
        {
            if (!_isManualEdit || !_manualDirty) return true;
            return EditorUtility.DisplayDialog(
                WindowTitle,
                Loc("ツリーを再生成しますか？\n手動で編集した内容は上書きされます。",
                    "Regenerate tree?\nYour manual edits will be overwritten."),
                Loc("再生成", "Regenerate"),
                Loc("キャンセル", "Cancel"));
        }

        private void UpdateUIFromState()
        {
            if (_folderField != null) _folderField.SetValueWithoutNotify(_folder);
            if (_modeDropdown != null && _modeDropdown.choices != null && (int)_mode < _modeDropdown.choices.Count)
                _modeDropdown.SetValueWithoutNotify(_modeDropdown.choices[(int)_mode]);
            if (_styleDropdown != null && _styleDropdown.choices != null && (int)_style < _styleDropdown.choices.Count)
                _styleDropdown.SetValueWithoutNotify(_styleDropdown.choices[(int)_style]);
            if (_showCountsToggle != null) _showCountsToggle.SetValueWithoutNotify(_showAssetCounts);
            if (_codeBlockToggle != null) _codeBlockToggle.SetValueWithoutNotify(_wrapInCodeBlock);
            if (_manualEditToggle != null) _manualEditToggle.SetValueWithoutNotify(_isManualEdit);
            if (_previewField != null) _previewField.isReadOnly = !_isManualEdit;
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
            if (!ConfirmOverwriteManualEdits())
            {
                if (_folderField != null) _folderField.SetValueWithoutNotify(_folder);
                return;
            }
            _folder = folder;
            if (_folderField != null) _folderField.SetValueWithoutNotify(_folder);
            Regenerate(force: true);
        }

        private void Regenerate(bool force = false)
        {
            string path = GetValidFolderPath(_folder);
            if (string.IsNullOrEmpty(path))
            {
                _treeText = string.Empty;
                ShowPreview(
                    Loc("フォルダを指定するとツリーが表示されます。", "Specify a folder to display the tree."),
                    allowManualEdit: false);
                return;
            }

            _treeText = FolderTreeBuilder.Build(path, new TreeBuildOptions
            {
                Mode = _mode,
                Style = _style,
                ShowAssetCounts = _showAssetCounts,
                WrapInCodeBlock = _wrapInCodeBlock,
            });

            // 手動で編集した内容は、強制再生成でない限り保護する
            if (_isManualEdit && _manualDirty && !force)
            {
                UpdateActionButtons();
                return;
            }

            string formattedPreview;
            bool truncated = _treeText.Length > PreviewMaxChars;
            if (truncated)
            {
                int cut = _treeText.LastIndexOf('\n', PreviewMaxChars);
                string preview = _treeText.Substring(0, cut > 0 ? cut : PreviewMaxChars);
                formattedPreview = preview + Loc(
                    "\n\n... (プレビューは一部のみ表示されています。コピー・保存は全体が対象です)",
                    "\n\n... (Preview is truncated. Copy & Save will export the full tree)");
            }
            else
            {
                formattedPreview = _treeText.TrimEnd('\n');
            }

            // 切り詰めたプレビューを編集・出力すると全文が失われるため、その間は手動編集を不可とする
            ShowPreview(formattedPreview, allowManualEdit: !truncated);
        }

        /// <summary>プレビューを書き換える。手動編集の内容は破棄される。</summary>
        private void ShowPreview(string text, bool allowManualEdit)
        {
            if (!allowManualEdit) _isManualEdit = false;
            _manualDirty = false;
            _manualText = string.Empty;

            if (_manualEditToggle != null)
            {
                _manualEditToggle.SetValueWithoutNotify(_isManualEdit);
                _manualEditToggle.SetEnabled(allowManualEdit);
            }
            if (_previewField != null)
            {
                _previewField.SetValueWithoutNotify(text);
                _previewField.isReadOnly = !_isManualEdit;
            }
            UpdateActionButtons();
        }

        private string GetExportText()
        {
            return _isManualEdit && _manualDirty ? _manualText : _treeText;
        }

        private void UpdateActionButtons()
        {
            SetActionButtonsEnabled(!string.IsNullOrEmpty(GetExportText()));
        }

        private void SetActionButtonsEnabled(bool enabled)
        {
            _copyButton?.SetEnabled(enabled);
            _saveButton?.SetEnabled(enabled);
        }

        private void CopyToClipboard()
        {
            string text = GetExportText();
            if (string.IsNullOrEmpty(text)) return;
            EditorGUIUtility.systemCopyBuffer = text;
            SetStatus(Loc("クリップボードにコピーしました", "Copied to clipboard"), StatusType.Success);
        }

        private void ShowSaveMenu()
        {
            if (string.IsNullOrEmpty(GetExportText())) return;
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent(Loc("テキスト (.txt)", "Text (.txt)")), false, () => SaveToFile("txt"));
            menu.AddItem(new GUIContent(Loc("Markdown (.md)", "Markdown (.md)")), false, () => SaveToFile("md"));
            menu.ShowAsContext();
        }

        private void SaveToFile(string extension)
        {
            string text = GetExportText();
            if (string.IsNullOrEmpty(text)) return;

            string folderName = Path.GetFileName(GetValidFolderPath(_folder)) ?? "tree";
            string defaultName = folderName + "_tree";
            string path = EditorUtility.SaveFilePanel(Loc("フォルダツリーを保存", "Save Folder Tree"), string.Empty, defaultName, extension);
            if (string.IsNullOrEmpty(path)) return;

            try
            {
                File.WriteAllText(path, text, new UTF8Encoding(false));
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Debug.LogError($"[FolderTreeExporter] 保存に失敗しました: {path}\n{e.Message}");
                SetStatus(Loc("ファイルの保存に失敗しました: ", "Failed to save file: ") + e.Message, StatusType.Error);
                return;
            }

            if (!string.IsNullOrEmpty(FileUtil.GetProjectRelativePath(path.Replace('\\', '/'))))
                AssetDatabase.Refresh();

            SetStatus(Loc("保存しました: ", "Saved: ") + Path.GetFileName(path), StatusType.Success);
        }

        private void BrowseFolder()
        {
            string start = GetValidFolderPath(_folder) ?? "Assets";
            string selected = EditorUtility.OpenFolderPanel(Loc("フォルダを選択", "Select Folder"), start, string.Empty);
            if (string.IsNullOrEmpty(selected)) return;

            DefaultAsset folder = LoadFolder(FileUtil.GetProjectRelativePath(selected.Replace('\\', '/')));
            if (folder == null)
            {
                EditorUtility.DisplayDialog(
                    WindowTitle,
                    Loc("プロジェクト内（Assets 配下）のフォルダを選択してください。", "Please select a folder within the project (under Assets)."),
                    "OK");
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
                    text = $"{baseText}  " + Loc($"更新あり {r.LatestVersion}", $"Update available {r.LatestVersion}");
                    update = true;
                    break;
                case DennokoVersionChecker.State.Error:
                    text = $"{baseText}  " + Loc("最新版を取得できません", "Unable to check update");
                    error = true;
                    break;
                case DennokoVersionChecker.State.Checking:
                    text = $"{baseText}  " + Loc("確認中...", "Checking...");
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

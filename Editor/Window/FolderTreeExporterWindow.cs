using System;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEditor;

namespace dennokoworks.FolderTreeExporter
{
    /// <summary>
    /// 指定フォルダの階層構造をテキストツリーとしてプレビュー・コピー・保存するウィンドウ。
    /// ツリー生成は Core/FolderTreeBuilder に委譲する。
    /// </summary>
    public class FolderTreeExporterWindow : EditorWindow
    {
        private const string WindowTitle = "FolderTreeExporter";

        // IMGUI のテキスト描画は 1 要素あたりの頂点数に上限があるため、プレビューのみ切り詰める。
        // コピー・保存は常に全文を対象とする。
        private const int PreviewMaxChars = 12000;

        private static readonly string[] ModeLabels = { "フォルダ + アセットタイプ", "フォルダのみ", "ファイル込み" };
        private static readonly string[] StyleLabels = { "標準ツリー（Unicode罫線）", "ASCIIツリー", "Markdownリスト" };
        private static readonly string[] MonospaceFontNames = { "Consolas", "Menlo", "Courier New" };

        // ─── 設定（ドメインリロードをまたいで保持） ──────────────────────────
        [SerializeField] private DefaultAsset _folder;
        [SerializeField] private TreeOutputMode _mode = TreeOutputMode.FoldersWithAssetTypes;
        [SerializeField] private TreeStyle _style = TreeStyle.Unicode;
        [SerializeField] private bool _showAssetCounts;
        [SerializeField] private bool _wrapInCodeBlock;

        // ─── 生成結果 ────────────────────────────────────────────────────────
        private string _treeText = string.Empty;
        private string _previewText = string.Empty;
        private bool _previewTruncated;
        private bool _previewSizeDirty = true;
        private Vector2 _previewSize;
        private Vector2 _previewScroll;

        private GUIStyle _previewStyle;
        private GUIStyle _dropAreaStyle;

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
            window.minSize = new Vector2(380, 360);
            window.Show();
            return window;
        }

        // Project ウィンドウ左ペイン（2 カラム時）の選択も拾えるよう assetGUIDs を使う。
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

        // ─── Lifecycle ───────────────────────────────────────────────────────
        private void OnEnable()
        {
            Regenerate();
        }

        // プロジェクト内のファイル構成が変わったらプレビューを追従させる。
        private void OnProjectChange()
        {
            Regenerate();
            Repaint();
        }

        // ─── ビジネスロジック ───────────────────────────────────────────────
        private void SetFolder(DefaultAsset folder)
        {
            _folder = folder;
            _previewScroll = Vector2.zero;
            Regenerate();
            Repaint();
        }

        private void Regenerate()
        {
            string path = GetValidFolderPath(_folder);
            _treeText = path == null
                ? string.Empty
                : FolderTreeBuilder.Build(path, new TreeBuildOptions
                {
                    Mode = _mode,
                    Style = _style,
                    ShowAssetCounts = _showAssetCounts,
                    WrapInCodeBlock = _wrapInCodeBlock,
                });

            _previewTruncated = _treeText.Length > PreviewMaxChars;
            if (_previewTruncated)
            {
                // 行の途中で切れないよう、上限以前の最後の改行までを表示する。
                int cut = _treeText.LastIndexOf('\n', PreviewMaxChars);
                _previewText = _treeText.Substring(0, cut > 0 ? cut : PreviewMaxChars);
            }
            else
            {
                _previewText = _treeText.TrimEnd('\n');
            }
            _previewSizeDirty = true;
        }

        private void CopyToClipboard()
        {
            EditorGUIUtility.systemCopyBuffer = _treeText;
            ShowNotification(new GUIContent("クリップボードにコピーしました"), 1.5);
        }

        private void SaveToFile(string extension)
        {
            string defaultName = Path.GetFileName(GetValidFolderPath(_folder)) + "_tree";
            string path = EditorUtility.SaveFilePanel("フォルダツリーを保存", string.Empty, defaultName, extension);
            if (string.IsNullOrEmpty(path)) return;

            try
            {
                File.WriteAllText(path, _treeText, new UTF8Encoding(false));
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Debug.LogError($"[FolderTreeExporter] 保存に失敗しました: {path}\n{e.Message}");
                EditorUtility.DisplayDialog(WindowTitle, "ファイルの保存に失敗しました。\n" + e.Message, "OK");
                return;
            }

            // プロジェクト内へ保存した場合は Project ウィンドウに反映する。
            if (!string.IsNullOrEmpty(FileUtil.GetProjectRelativePath(path.Replace('\\', '/'))))
                AssetDatabase.Refresh();

            ShowNotification(new GUIContent("保存しました: " + Path.GetFileName(path)), 2.0);
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

        // ─── OnGUI ───────────────────────────────────────────────────────────
        private void OnGUI()
        {
            InitStyles();

            EditorGUILayout.Space(6);
            DrawHeader();
            EditorGUILayout.Space(6);
            DrawOptions();
            EditorGUILayout.Space(6);
            DrawActions();
            EditorGUILayout.Space(6);
            DrawPreview();
        }

        private void InitStyles()
        {
            if (_previewStyle != null) return;

            _previewStyle = new GUIStyle(EditorStyles.textArea) { wordWrap = false, richText = false };
            // 罫線の桁が揃うよう、OS に等幅フォントがあれば使う。
            Font font = Font.CreateDynamicFontFromOSFont(MonospaceFontNames, 12);
            if (font != null)
            {
                _previewStyle.font = font;
                _previewStyle.fontSize = 12;
            }

            _dropAreaStyle = new GUIStyle(EditorStyles.helpBox)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 11,
            };
        }

        // ─── ヘッダー部: フォルダ選択 ────────────────────────────────────────
        private void DrawHeader()
        {
            GUILayout.BeginHorizontal();
            EditorGUI.BeginChangeCheck();
            var folder = (DefaultAsset)EditorGUILayout.ObjectField("対象フォルダ", _folder, typeof(DefaultAsset), false);
            if (EditorGUI.EndChangeCheck()) SetFolder(folder);
            if (GUILayout.Button("参照", GUILayout.Width(48))) BrowseFolder();
            GUILayout.EndHorizontal();

            if (_folder != null && GetValidFolderPath(_folder) == null)
                EditorGUILayout.HelpBox("フォルダを指定してください。", MessageType.Warning);

            Rect dropRect = GUILayoutUtility.GetRect(0, 36, GUILayout.ExpandWidth(true));
            GUI.Box(dropRect, "ここに Project ウィンドウからフォルダをドラッグ＆ドロップ", _dropAreaStyle);
            HandleDragAndDrop(dropRect);
        }

        private void HandleDragAndDrop(Rect dropRect)
        {
            Event evt = Event.current;
            if (evt.type != EventType.DragUpdated && evt.type != EventType.DragPerform) return;
            if (!dropRect.Contains(evt.mousePosition)) return;

            DefaultAsset folder = null;
            foreach (string path in DragAndDrop.paths)
            {
                folder = LoadFolder(path);
                if (folder != null) break;
            }

            DragAndDrop.visualMode = folder != null ? DragAndDropVisualMode.Copy : DragAndDropVisualMode.Rejected;
            if (evt.type == EventType.DragPerform && folder != null)
            {
                DragAndDrop.AcceptDrag();
                SetFolder(folder);
            }
            evt.Use();
        }

        // ─── 設定・オプション部 ──────────────────────────────────────────────
        private void DrawOptions()
        {
            EditorGUI.BeginChangeCheck();

            var mode = (TreeOutputMode)EditorGUILayout.Popup("出力モード", (int)_mode, ModeLabels);
            var style = (TreeStyle)EditorGUILayout.Popup("ツリータイプ", (int)_style, StyleLabels);

            bool showCounts;
            using (new EditorGUI.DisabledGroupScope(mode != TreeOutputMode.FoldersWithAssetTypes))
            {
                showCounts = EditorGUILayout.ToggleLeft(
                    new GUIContent("アセット件数を表示", "「フォルダ + アセットタイプ」モードで、種別ごとのファイル件数を併記します。"),
                    _showAssetCounts);
            }

            bool wrapInCodeBlock = EditorGUILayout.ToggleLeft(
                new GUIContent("Markdownコードブロックで囲む (```)", "出力全体を ``` で囲みます。Discord や GitHub 等へ貼り付ける際の桁ズレ防止用です。"),
                _wrapInCodeBlock);

            if (EditorGUI.EndChangeCheck())
            {
                _mode = mode;
                _style = style;
                _showAssetCounts = showCounts;
                _wrapInCodeBlock = wrapInCodeBlock;
                Regenerate();
            }
        }

        // ─── アクション部 ────────────────────────────────────────────────────
        private void DrawActions()
        {
            using (new EditorGUI.DisabledGroupScope(string.IsNullOrEmpty(_treeText)))
            {
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("クリップボードにコピー", GUILayout.Height(26)))
                    CopyToClipboard();

                // OS の保存ダイアログは拡張子を 1 つしか指定できないため、先に形式を選ばせる。
                if (GUILayout.Button("ファイルに保存", GUILayout.Height(26)))
                {
                    var menu = new GenericMenu();
                    menu.AddItem(new GUIContent("テキスト (.txt)"), false, () => SaveToFile("txt"));
                    menu.AddItem(new GUIContent("Markdown (.md)"), false, () => SaveToFile("md"));
                    menu.ShowAsContext();
                }
                GUILayout.EndHorizontal();
            }
        }

        // ─── プレビュー部 ────────────────────────────────────────────────────
        private void DrawPreview()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("プレビュー", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            using (new EditorGUI.DisabledGroupScope(GetValidFolderPath(_folder) == null))
            {
                if (GUILayout.Button("更新", EditorStyles.miniButton, GUILayout.Width(48)))
                    Regenerate();
            }
            GUILayout.EndHorizontal();

            if (string.IsNullOrEmpty(_treeText))
            {
                EditorGUILayout.HelpBox("フォルダを指定するとツリーが表示されます。", MessageType.Info);
                return;
            }

            if (_previewTruncated)
            {
                EditorGUILayout.HelpBox(
                    "ツリーが大きいため、プレビューは先頭部分のみ表示しています。コピー・保存は全体が対象です。",
                    MessageType.Info);
            }

            if (_previewSizeDirty)
            {
                _previewSize = _previewStyle.CalcSize(new GUIContent(_previewText));
                _previewSizeDirty = false;
            }

            _previewScroll = EditorGUILayout.BeginScrollView(_previewScroll, GUILayout.ExpandHeight(true));
            // SelectableLabel は読み取り専用のまま、範囲選択・全選択 (Ctrl/Cmd+A)・コピーができる。
            Rect rect = GUILayoutUtility.GetRect(
                _previewSize.x, _previewSize.y, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            EditorGUI.SelectableLabel(rect, _previewText, _previewStyle);
            EditorGUILayout.EndScrollView();
        }
    }
}

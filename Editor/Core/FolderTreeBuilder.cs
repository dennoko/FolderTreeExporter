using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;

namespace dennokoworks.FolderTreeExporter
{
    /// <summary>ツリーの出力モード。</summary>
    public enum TreeOutputMode
    {
        FoldersWithAssetTypes, // フォルダ + アセットタイプ（デフォルト）
        FoldersOnly,           // フォルダのみ
        WithFiles,             // ファイル込み
    }

    /// <summary>ツリーの表現スタイル。</summary>
    public enum TreeStyle
    {
        Unicode,      // 標準ツリー（Unicode 罫線・デフォルト）
        Ascii,        // ASCII ツリー（半角英数記号のみ）
        MarkdownList, // Markdown の箇条書きリスト
    }

    /// <summary>ツリー生成のオプション。</summary>
    public struct TreeBuildOptions
    {
        public TreeOutputMode Mode;
        public TreeStyle Style;
        public bool ShowAssetCounts; // FoldersWithAssetTypes 時のみ有効
        public bool WrapInCodeBlock; // 全体を Markdown コードブロックで囲む
    }

    /// <summary>指定フォルダ配下の階層構造をプレーンテキストのツリーとして生成する。</summary>
    public static class FolderTreeBuilder
    {
        // スタイルごとの記号。Markdown リストは枝・終端とも「- 」、インデントは半角スペース 2 文字。
        private readonly struct Symbols
        {
            public readonly string Branch;
            public readonly string LastBranch;
            public readonly string Vertical;
            public readonly string Indent;

            public Symbols(string branch, string lastBranch, string vertical, string indent)
            {
                Branch = branch;
                LastBranch = lastBranch;
                Vertical = vertical;
                Indent = indent;
            }
        }

        private static readonly Symbols UnicodeSymbols  = new Symbols("├── ", "└── ", "│   ", "    ");
        private static readonly Symbols AsciiSymbols    = new Symbols("+-- ", @"\-- ", "|   ", "    ");
        private static readonly Symbols MarkdownSymbols = new Symbols("- ", "- ", "  ", "  ");

        private const string CodeFence = "```";

        private static Symbols GetSymbols(TreeStyle style)
        {
            switch (style)
            {
                case TreeStyle.Ascii:        return AsciiSymbols;
                case TreeStyle.MarkdownList: return MarkdownSymbols;
                default:                     return UnicodeSymbols;
            }
        }

        /// <summary>
        /// フォルダのアセットパス（Assets/... または Packages/...）からツリーテキストを生成する。
        /// 有効なフォルダでなければ空文字列を返す。
        /// </summary>
        public static string Build(string folderAssetPath, TreeBuildOptions options)
        {
            if (string.IsNullOrEmpty(folderAssetPath) || !AssetDatabase.IsValidFolder(folderAssetPath))
                return string.Empty;

            folderAssetPath = folderAssetPath.TrimEnd('/');

            Symbols symbols = GetSymbols(options.Style);
            bool isList = options.Style == TreeStyle.MarkdownList;

            var sb = new StringBuilder();
            if (options.WrapInCodeBlock) sb.AppendLine(CodeFence);

            // Markdown リストではルートも箇条書きの項目とし、子要素を 1 段下げる。
            if (isList) sb.Append(symbols.Branch);
            sb.Append(Path.GetFileName(folderAssetPath));
            AppendFolderContents(sb, folderAssetPath, isList ? symbols.Indent : string.Empty, options, symbols);

            if (options.WrapInCodeBlock) sb.AppendLine(CodeFence);
            return sb.ToString().Replace("\r\n", "\n");
        }

        // フォルダ名は呼び出し側が出力済み。ここではタグ（行末）と子要素を追記する。
        private static void AppendFolderContents(
            StringBuilder sb, string folderAssetPath, string prefix, TreeBuildOptions options, Symbols symbols)
        {
            ListEntries(folderAssetPath, out List<string> folders, out List<string> files);

            if (options.Mode == TreeOutputMode.FoldersWithAssetTypes)
                AppendAssetTypeTags(sb, folderAssetPath, files, options.ShowAssetCounts);
            sb.Append('\n');

            bool listFiles = options.Mode == TreeOutputMode.WithFiles;
            int total = folders.Count + (listFiles ? files.Count : 0);
            int index = 0;

            foreach (string folder in folders)
            {
                bool isLast = ++index == total;
                sb.Append(prefix).Append(isLast ? symbols.LastBranch : symbols.Branch).Append(folder);
                AppendFolderContents(
                    sb, folderAssetPath + "/" + folder,
                    prefix + (isLast ? symbols.Indent : symbols.Vertical), options, symbols);
            }

            if (!listFiles) return;
            foreach (string file in files)
            {
                bool isLast = ++index == total;
                sb.Append(prefix).Append(isLast ? symbols.LastBranch : symbols.Branch).Append(file).Append('\n');
            }
        }

        private static void AppendAssetTypeTags(
            StringBuilder sb, string folderAssetPath, List<string> files, bool showCounts)
        {
            if (files.Count == 0) return;

            var counts = new Dictionary<string, int>();
            foreach (string file in files)
            {
                string label = AssetTypeClassifier.Classify(folderAssetPath + "/" + file);
                counts.TryGetValue(label, out int count);
                counts[label] = count + 1;
            }

            var labels = new List<string>(counts.Keys);
            labels.Sort(AssetTypeClassifier.CompareLabels);

            sb.Append(" [");
            for (int i = 0; i < labels.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(labels[i]);
                if (showCounts) sb.Append(": ").Append(counts[labels[i]]);
            }
            sb.Append(']');
        }

        /// <summary>
        /// フォルダ直下のサブフォルダ名・ファイル名を名前順で列挙する。
        /// .meta と、「.」で始まる隠しファイル／フォルダは除外する。
        /// </summary>
        private static void ListEntries(string folderAssetPath, out List<string> folders, out List<string> files)
        {
            folders = new List<string>();
            files = new List<string>();

            // Packages/ 配下は実体が Library/PackageCache にある場合があるため物理パスへ変換する。
            string physicalPath = FileUtil.GetPhysicalPath(folderAssetPath);
            if (string.IsNullOrEmpty(physicalPath) || !Directory.Exists(physicalPath)) return;

            try
            {
                foreach (string dir in Directory.GetDirectories(physicalPath))
                {
                    string name = Path.GetFileName(dir);
                    if (!IsHidden(name)) folders.Add(name);
                }
                foreach (string file in Directory.GetFiles(physicalPath))
                {
                    string name = Path.GetFileName(file);
                    if (IsHidden(name)) continue;
                    if (name.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
                    files.Add(name);
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                // 読み取れないフォルダは空として扱う。
            }

            folders.Sort(StringComparer.OrdinalIgnoreCase);
            files.Sort(StringComparer.OrdinalIgnoreCase);
        }

        private static bool IsHidden(string name)
        {
            return string.IsNullOrEmpty(name) || name[0] == '.';
        }
    }
}

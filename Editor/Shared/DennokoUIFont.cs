using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using FontAsset = UnityEngine.TextCore.Text.FontAsset;

namespace dennokoworks.FolderTreeExporter
{
    /// <summary>
    /// dennokoworks UI の標準フォント（OS のメイリオ）を SDF FontAsset として生成・保持し、
    /// フォントキャッシュ消失によるテキスト崩壊を自己修復する共通クラス。
    ///
    /// ⚠ EditorWindow / Inspector 側でフォントを直接生成しないこと。
    ///   ルート要素に対して <see cref="Apply"/> を 1 回呼ぶだけでよい。
    /// </summary>
    [InitializeOnLoad]
    internal static class DennokoUIFont
    {
        private const string FamilyName = "Meiryo";
        private const string StyleName = "Regular";

        // ドメインリロード後に前回生成したインスタンスを拾い直すための識別名。
        // 同じプロジェクト内の複数ツールで 1 つの FontAsset を共有するためでもあるので変更しない。
        private const string AssetName = "Dennoko_UIFont_Meiryo";

        // アトラス増加の検出間隔（秒）。短くしすぎない。
        private const double TickIntervalSec = 2.0;

        // ─── ウォームアップ文字 ─────────────────────────────────────────────
        // ここに列挙した文字は生成時にまとめてアトラスへ焼き込む。実行中のアトラス追加
        // （＝保護漏れの隙）を減らすため、UXML / Label に出る日本語をここへ書き足すこと。
        private const string WarmupAscii =
            " !\"#$%&'()*+,-./0123456789:;<=>?@" +
            "ABCDEFGHIJKLMNOPQRSTUVWXYZ[\\]^_`" +
            "abcdefghijklmnopqrstuvwxyz{|}~";

        private const string WarmupJapanese =
            "適用保存解除追加削除設定選択中対象有効無効表示非切替更新確認取消閉開始了" +
            "はいいえ完了失敗警告情報エラー成功準備実行処理読込書出" +
            "フォルダーツリー構造アセットタイプ罫線プレビュー参照クリップボードコピー" +
            "ファイル最新版取得できませ確認中再更新あり件数囲む標準形式個直下配置言語英手動編集破棄" +
            "かがくこさしすただつてとなにのみらるれをんィウェキグジステデブモロン・一体全内力指見部" +
            "。＆（）↻├└│─" +
            "、「」うけごどへやよズャ上付併別利合場容接日本桁止注生種等終記貼釈防際？";

        private static FontAsset _font;
        private static bool _unavailable;     // 未搭載環境。CreateFontAsset を再試行しない
        private static readonly List<VisualElement> _roots = new List<VisualElement>();
        private static bool _tickHooked;
        private static double _nextTick;

        static DennokoUIFont()
        {
            // UnloadUnusedAssets を暗黙的に挟みうるタイミングで再点検する
            AssemblyReloadEvents.afterAssemblyReload += Revalidate;
            EditorApplication.playModeStateChanged += _ => Revalidate();
            EditorApplication.projectChanged += Revalidate;   // AssetDatabase.Refresh 後
        }

        // ─── 公開 API ───────────────────────────────────────────────────────

        /// <summary>
        /// ルート要素に標準フォントを適用し、以後の自己修復対象として登録する。
        /// EditorWindow の CreateGUI() / Inspector の CreateInspectorGUI() から 1 回呼ぶ。
        /// </summary>
        public static void Apply(VisualElement root)
        {
            if (root == null) return;

            if (!_roots.Contains(root))
            {
                _roots.Add(root);
                // 再ドック・レイアウト変更・リロード後の再アタッチでも確実に貼り直す。
                root.RegisterCallback<AttachToPanelEvent>(_ => { Track(root); ApplyTo(root); });
                root.RegisterCallback<DetachFromPanelEvent>(_ => _roots.Remove(root));
            }

            HookTick(true);
            ApplyTo(root);
        }

        /// <summary>自己修復の監視対象に入れる（冪等）。</summary>
        private static void Track(VisualElement root)
        {
            if (!_roots.Contains(root)) _roots.Add(root);
            HookTick(true);
        }

        // ─── 適用 ───────────────────────────────────────────────────────────

        private static void ApplyTo(VisualElement root)
        {
            var font = Get();

            // 取得できないときは破棄済みへのダングリング参照を残さず、
            // インラインスタイルを消してエディタ標準フォントへ確実に戻す。
            root.style.unityFontDefinition = font != null
                ? new StyleFontDefinition(FontDefinition.FromSDFFont(font))
                : new StyleFontDefinition(StyleKeyword.Null);
        }

        // ─── 取得・生成 ─────────────────────────────────────────────────────

        private static FontAsset Get()
        {
            if (IsAlive(_font))
            {
                Protect(_font);   // 実行中に増えたアトラスをここで拾う
                return _font;
            }

            if (_unavailable) return null;

            _font = FindExisting() ?? Create();
            if (_font == null)
            {
                _unavailable = true;   // フォント未搭載環境。毎フレーム再試行しない
                return null;
            }
            return _font;
        }

        /// <summary>ドメインリロードを生き延びた前回生成分を拾う（leak 防止）。</summary>
        private static FontAsset FindExisting()
        {
            foreach (var fa in Resources.FindObjectsOfTypeAll<FontAsset>())
            {
                if (fa == null || fa.name != AssetName || !IsAlive(fa)) continue;
                Protect(fa);
                return fa;
            }
            return null;
        }

        private static FontAsset Create()
        {
            try
            {
                var fa = FontAsset.CreateFontAsset(FamilyName, StyleName);
                if (fa == null) return null;   // 未搭載環境。エディタ標準フォントへフォールバック

                fa.name = AssetName;
                Protect(fa);
                PreWarm(fa);
                Protect(fa);   // PreWarm で増えたアトラスを守る
                return fa;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>UI に出る文字を先に焼き、操作中のアトラス追加を減らす。</summary>
        private static void PreWarm(FontAsset fa)
        {
            try { fa.TryAddCharacters(WarmupAscii + WarmupJapanese, out _); }
            catch { /* 焼けなくても動作はする（実行中に動的追加される） */ }
        }

        // ─── 保護・検証 ─────────────────────────────────────────────────────

        /// <summary>
        /// FontAsset 本体・アトラス material・全 atlasTextures に HideAndDontSave を伝播させる。
        /// 冪等なので何度呼んでもよい。実行中に追加されたアトラスを拾うため定期的に呼ぶこと。
        /// </summary>
        private static void Protect(FontAsset fa)
        {
            if (fa == null) return;

            fa.hideFlags = HideFlags.HideAndDontSave;

            if (fa.material != null)
                fa.material.hideFlags = HideFlags.HideAndDontSave;

            var atlasTextures = fa.atlasTextures;
            if (atlasTextures == null) return;
            foreach (var tex in atlasTextures)
            {
                if (tex != null)
                    tex.hideFlags = HideFlags.HideAndDontSave;
            }
        }

        /// <summary>本体だけでなくアトラスまで生きているかを判定する（fake-null 対応）。</summary>
        private static bool IsAlive(FontAsset fa)
        {
            if (fa == null) return false;
            if (fa.material == null) return false;
            var atlasTextures = fa.atlasTextures;
            return atlasTextures != null && atlasTextures.Length > 0 && atlasTextures[0] != null;
        }

        /// <summary>破棄検知 → 作り直し → 開いている全ルートへ再適用。</summary>
        private static void Revalidate()
        {
            if (IsAlive(_font))
            {
                Protect(_font);   // 増えたアトラスを守るだけでよい
                return;
            }

            _font = null;         // 次の Get() で再生成させる
            for (int i = _roots.Count - 1; i >= 0; i--)
            {
                var root = _roots[i];
                if (root == null || root.panel == null) { _roots.RemoveAt(i); continue; }
                ApplyTo(root);
            }
        }

        // ─── 監視ティック ───────────────────────────────────────────────────

        private static void HookTick(bool on)
        {
            if (on == _tickHooked) return;
            if (on) EditorApplication.update += Tick;
            else EditorApplication.update -= Tick;
            _tickHooked = on;
        }

        private static void Tick()
        {
            for (int i = _roots.Count - 1; i >= 0; i--)
            {
                var root = _roots[i];
                if (root == null || root.panel == null) _roots.RemoveAt(i);
            }
            if (_roots.Count == 0) { HookTick(false); return; }

            if (EditorApplication.timeSinceStartup < _nextTick) return;
            _nextTick = EditorApplication.timeSinceStartup + TickIntervalSec;

            Revalidate();
        }
    }
}

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

namespace dennokoworks.FolderTreeExporter
{
    /// <summary>
    /// アセットパスから Unity のアセット種別ラベル（Prefab / Texture / Model ...）を求める。
    /// 拡張子ではなく AssetDatabase が返すメインアセットの型に基づいて分類する。
    /// </summary>
    public static class AssetTypeClassifier
    {
        public const string Other = "Other";

        // タグの表示順。ここに無いラベルは末尾に名前順で並ぶ。
        private static readonly string[] DisplayOrder =
        {
            "Scene", "Prefab", "Model", "Mesh", "Material", "Texture", "Shader",
            "Animation", "Animator", "AvatarMask", "Audio", "Video", "Font",
            "Script", "AssemblyDefinition", "ScriptableObject", "Text",
        };

        // 型名で判定するもの（モジュール参照を増やさないため、および internal 型のため）。
        private static readonly Dictionary<string, string> LabelByTypeName = new Dictionary<string, string>
        {
            { "AnimatorController",                  "Animator" },
            { "AnimatorOverrideController",          "Animator" },
            { "AudioMixerController",                "Audio" },
            { "VideoClip",                           "Video" },
            { "ComputeShader",                       "Shader" },
            { "RayTracingShader",                    "Shader" },
            { "ShaderInclude",                       "Shader" },
            { "ShaderVariantCollection",             "Shader" },
            { "AssemblyDefinitionAsset",             "AssemblyDefinition" },
            { "AssemblyDefinitionReferenceAsset",    "AssemblyDefinition" },
        };

        /// <summary>アセットパスの種別ラベルを返す。Unity が認識していないファイルは Other。</summary>
        public static string Classify(string assetPath)
        {
            Type type = AssetDatabase.GetMainAssetTypeAtPath(assetPath);
            if (type == null) return Other;

            if (LabelByTypeName.TryGetValue(type.Name, out string label)) return label;

            if (type == typeof(GameObject)) return IsPrefab(assetPath) ? "Prefab" : "Model";
            if (type == typeof(SceneAsset)) return "Scene";
            if (type == typeof(DefaultAsset)) return Other;
            if (typeof(Texture).IsAssignableFrom(type)) return "Texture";
            if (typeof(Material).IsAssignableFrom(type)) return "Material";
            if (typeof(Shader).IsAssignableFrom(type)) return "Shader";
            if (typeof(MonoScript).IsAssignableFrom(type)) return "Script";
            if (typeof(AudioClip).IsAssignableFrom(type)) return "Audio";
            if (typeof(AnimationClip).IsAssignableFrom(type)) return "Animation";
            if (typeof(AvatarMask).IsAssignableFrom(type)) return "AvatarMask";
            if (typeof(Mesh).IsAssignableFrom(type)) return "Mesh";
            if (typeof(Font).IsAssignableFrom(type)) return "Font";
            if (typeof(TextAsset).IsAssignableFrom(type)) return "Text";
            if (typeof(ScriptableObject).IsAssignableFrom(type)) return "ScriptableObject";

            return type.Name;
        }

        /// <summary>種別ラベルの表示順を比較する。</summary>
        public static int CompareLabels(string a, string b)
        {
            int ia = OrderIndex(a);
            int ib = OrderIndex(b);
            return ia != ib ? ia.CompareTo(ib) : string.CompareOrdinal(a, b);
        }

        private static int OrderIndex(string label)
        {
            if (label == Other) return int.MaxValue;
            int index = Array.IndexOf(DisplayOrder, label);
            return index >= 0 ? index : DisplayOrder.Length;
        }

        // メインアセットが GameObject のもののうち、PrefabImporter 以外
        // （ModelImporter や VRM / glTF 等の ScriptedImporter）はモデル扱いとする。
        private static bool IsPrefab(string assetPath)
        {
            Type importerType = AssetDatabase.GetImporterType(assetPath);
            return importerType == null || importerType.Name == "PrefabImporter";
        }
    }
}

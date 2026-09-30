using System.IO;
using TMPro;
using UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Builds the restyled control panel INTO the open scene so it's visible, editable and saved
/// (ControlPanelStyler otherwise only applies it at runtime). Menu: Tools > Pipeline > Bake Control
/// Panel Layout. Run it once on the scene that contains the panel, then save the scene.
/// To undo, reload the scene without saving (or revert it in git).
/// </summary>
public static class ControlPanelBaker
{
    private const string SpritePath = "Assets/UI/RoundedRect.png";

    [MenuItem("Tools/Pipeline/Bake Control Panel Layout")]
    private static void Bake()
    {
        if (Application.isPlaying)
        {
            EditorUtility.DisplayDialog("Bake Control Panel", "Exit Play mode first.", "OK");
            return;
        }

        var panel = Object.FindAnyObjectByType<PipelineConfigPanel>(FindObjectsInactive.Include);
        Canvas canvas = panel != null ? panel.GetComponentInParent<Canvas>(true) : null;
        if (canvas == null)
        {
            EditorUtility.DisplayDialog("Bake Control Panel", "No PipelineConfigPanel canvas found in the open scene.", "OK");
            return;
        }

        var root = (RectTransform)canvas.transform;
        if (root.Find("ControlPanelBackground") != null)
        {
            EditorUtility.DisplayDialog("Bake Control Panel", "This panel is already baked.", "OK");
            return;
        }

        Sprite rounded = EnsureRoundedSprite();
        ControlPanelStyler.Build(root, rounded);

        EditorSceneManager.MarkSceneDirty(root.gameObject.scene);
        Selection.activeGameObject = root.gameObject;
        Debug.Log("[ControlPanelBaker] Control panel baked into the scene. Save the scene (Ctrl+S) to keep it.");
    }

    private static Sprite EnsureRoundedSprite()
    {
        var existing = AssetDatabase.LoadAssetAtPath<Sprite>(SpritePath);
        if (existing != null) return existing;

        Directory.CreateDirectory(Path.GetDirectoryName(SpritePath));
        Texture2D tex = ControlPanelStyler.MakeRoundedTexture();
        File.WriteAllBytes(SpritePath, tex.EncodeToPNG());
        Object.DestroyImmediate(tex);
        AssetDatabase.ImportAsset(SpritePath, ImportAssetOptions.ForceSynchronousImport);

        var importer = (TextureImporter)AssetImporter.GetAtPath(SpritePath);
        importer.textureType = TextureImporterType.Sprite;
        importer.spriteImportMode = SpriteImportMode.Single;
        importer.spritePixelsPerUnit = 100f;
        importer.spriteBorder = new Vector4(ControlPanelStyler.RoundedRadius, ControlPanelStyler.RoundedRadius,
                                            ControlPanelStyler.RoundedRadius, ControlPanelStyler.RoundedRadius);
        importer.mipmapEnabled = false;
        importer.alphaIsTransparency = true;
        importer.filterMode = FilterMode.Bilinear;
        importer.textureCompression = TextureImporterCompression.Uncompressed;
        importer.SaveAndReimport();

        return AssetDatabase.LoadAssetAtPath<Sprite>(SpritePath);
    }
}

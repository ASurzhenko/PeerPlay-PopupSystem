using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using PeerPlay.Popups.Sourcing;
using PeerPlay.Popups.View;
using TMPro;
using UnityEditor;
using UnityEditor.U2D;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;
using UnityEngine.U2D;
using UnityEngine.UI;

namespace PeerPlay.Popups.EditorTools
{
    /// <summary>
    /// One-shot authoring of the artefacts the popup system needs but source control cannot generate:
    /// sprite import settings, the atlas, the TMP font asset, the four prefabs, the catalog, the built-in
    /// default and the remote sentinel.
    ///
    /// It is idempotent — every step overwrites rather than appends — so re-running after a change to the
    /// art is the normal way to use it, not a hazard. Every run writes a report next to the project so the
    /// outcome is a file on disk rather than a console message that scrolls away.
    /// </summary>
    internal static class UIKitAssetBuilder
    {
        private const string ArtRoot = "Assets/Art/UIKit";
        private const string GeneratedRoot = "Assets/Art/Generated";
        private const string PrefabRoot = "Assets/Prefabs/Popups";
        private const string ConfigRoot = "Assets/Config";
        private const string AtlasPath = GeneratedRoot + "/UIKit.spriteatlasv2";
        private const string FontTtfPath = "Assets/Fonts/Fredoka-Variable.ttf";
        private const string FontAssetPath = "Assets/Fonts/Fredoka SDF.asset";
        private const string CatalogPath = ConfigRoot + "/PopupCatalog.asset";
        private const string ProbePath = ConfigRoot + "/popup_remote_probe.txt";
        private const string ReportPath = "Tools/uikit-build-report.json";

        /// <summary>Canvas is 1080 wide at Match=0, and the 3:4 tablet leaves only 1440 units of height.</summary>
        private const float DialogWidth = 900f;

        private static readonly List<string> Log = new List<string>();

        [MenuItem("Tools/PeerPlay/Build UI Kit Assets")]
        internal static void BuildAll()
        {
            Log.Clear();

            // The report is written on EVERY exit, failure included. A tool whose only failure signal is an
            // exception in a console nobody is watching is the shape that reads as "it ran fine".
            try
            {
                try
                {
                    AssetDatabase.StartAssetEditing();

                    EnsureFolders();
                    ConfigureSprites();
                }
                finally
                {
                    AssetDatabase.StopAssetEditing();
                    AssetDatabase.Refresh();
                }

                BuildAtlas();
                BuildFontAsset();
                BuildProbeAsset();
                BuildCatalog();
                BuildPrefabs();

                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                WriteReport("ok", null);
                Debug.Log($"{nameof(UIKitAssetBuilder)}.{nameof(BuildAll)} done — report at {ReportPath}");
            }
            catch (Exception e)
            {
                Log.Add($"FAILED at step {Log.Count}: {e.GetType().Name}");
                WriteReport("failed", $"{e.GetType().Name}: {e.Message}");
                Debug.LogException(e);
                throw;
            }
        }

        // ---------------------------------------------------------------- sprites

        /// <summary>
        /// Nine-slice borders, given as left / right / top / bottom and written as Unity's
        /// Vector4(left, bottom, right, top) — the two orders differ, and swapping them produces a
        /// plausible-looking sprite that stretches the wrong edges.
        /// </summary>
        private struct SpriteSpec
        {
            internal string File;
            internal float Left;
            internal float Right;
            internal float Top;
            internal float Bottom;

            internal bool IsSliced => Left > 0f || Right > 0f || Top > 0f || Bottom > 0f;
        }

        private static readonly SpriteSpec[] Sprites =
        {
            new SpriteSpec { File = "panel",            Left = 64, Right = 64, Top = 64, Bottom = 64 },
            // Horizontal only: the ribbon's caps run the full height, so a vertical border would smear them.
            new SpriteSpec { File = "ribbon",           Left = 70, Right = 70, Top = 0,  Bottom = 0 },
            new SpriteSpec { File = "button_primary",   Left = 40, Right = 40, Top = 26, Bottom = 26 },
            new SpriteSpec { File = "button_secondary", Left = 40, Right = 40, Top = 26, Bottom = 26 },
            new SpriteSpec { File = "close" },
            new SpriteSpec { File = "ring" },
            new SpriteSpec { File = "icon_coins" },
            new SpriteSpec { File = "icon_info" },
            new SpriteSpec { File = "icon_download" },
            new SpriteSpec { File = "icon_warning" },
            new SpriteSpec { File = "burst_gen" },
            new SpriteSpec { File = "Effects/burst_512" },
            new SpriteSpec { File = "Effects/glow_small" },
            new SpriteSpec { File = "Effects/ray_burst_128" },
            new SpriteSpec { File = "Effects/ring_glow_128" },
            new SpriteSpec { File = "Effects/shockwave_128" },
            new SpriteSpec { File = "Effects/soft_glow_128" }
        };

        private static void ConfigureSprites()
        {
            for (int i = 0; i < Sprites.Length; i++)
            {
                SpriteSpec spec = Sprites[i];
                string path = $"{ArtRoot}/{spec.File}.png";

                TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;
                if (importer == null)
                {
                    Log.Add($"MISSING {path}");
                    continue;
                }

                TextureImporterSettings settings = new TextureImporterSettings();
                importer.ReadTextureSettings(settings);

                settings.textureType = TextureImporterType.Sprite;
                settings.spriteMode = (int)SpriteImportMode.Single;
                settings.spriteAlignment = (int)SpriteAlignment.Center;
                settings.spritePivot = new Vector2(0.5f, 0.5f);
                settings.alphaIsTransparency = true;
                settings.mipmapEnabled = false;
                settings.readable = false;

                // FULL RECT on every sliced sprite. The default Tight mesh silently breaks Sliced rendering:
                // the generated mesh does not cover the border regions, so the frame tears at the corners.
                settings.spriteMeshType = spec.IsSliced ? SpriteMeshType.FullRect : SpriteMeshType.Tight;

                importer.SetTextureSettings(settings);
                importer.spriteBorder = new Vector4(spec.Left, spec.Bottom, spec.Right, spec.Top);
                importer.spritePixelsPerUnit = 100f;

                // Compression is set ON THE ATLAS. Per-sprite settings are ignored once a sprite is packed,
                // so leaving them at the default here is correct rather than sloppy.
                importer.SaveAndReimport();

                // Read back off the importer, for the same reason the atlas does: SetTextureSettings takes
                // a copy, and what a reimport actually persisted is the only thing that matters.
                TextureImporter reloaded = AssetImporter.GetAtPath(path) as TextureImporter;
                TextureImporterSettings persisted = new TextureImporterSettings();
                reloaded.ReadTextureSettings(persisted);

                Vector4 expectedBorder = new Vector4(spec.Left, spec.Bottom, spec.Right, spec.Top);

                if (reloaded.spriteBorder != expectedBorder)
                {
                    throw new InvalidOperationException(
                        $"{spec.File} border reads back as {reloaded.spriteBorder}, expected {expectedBorder}");
                }

                SpriteMeshType expectedMesh = spec.IsSliced ? SpriteMeshType.FullRect : SpriteMeshType.Tight;

                if (persisted.spriteMeshType != expectedMesh)
                {
                    throw new InvalidOperationException(
                        $"{spec.File} mesh reads back as {persisted.spriteMeshType}, expected {expectedMesh}");
                }

                Log.Add($"sprite {spec.File} verified border={reloaded.spriteBorder} mesh={persisted.spriteMeshType}");
            }
        }

        // ---------------------------------------------------------------- atlas

        private static void BuildAtlas()
        {
            // Built from scratch every run rather than edited in place: there is no "clear members" call on
            // SpriteAtlasAsset, so overwriting wholesale is what makes a re-run idempotent.
            SpriteAtlasAsset atlas = new SpriteAtlasAsset();

            // Rotation and tight packing OFF, and not as a default worth skimming: with tight packing a
            // concave sprite's bounding rectangle can contain a slice of its neighbour, and a UI Image
            // samples the rectangle — so foreign artwork appears inside an icon. Alpha dilation kills the
            // matching bleed at the transparent edge under bilinear filtering.
            SpriteAtlasPackingSettings packing = new SpriteAtlasPackingSettings
            {
                enableRotation = false,
                enableTightPacking = false,
                enableAlphaDilation = true,
                padding = 4,
                blockOffset = 1
            };

            SpriteAtlasTextureSettings texture = new SpriteAtlasTextureSettings
            {
                sRGB = true,
                filterMode = FilterMode.Bilinear,
                generateMipMaps = false
            };

            atlas.SetPackingSettings(packing);
            atlas.SetTextureSettings(texture);
            atlas.SetIncludeInBuild(true);

            List<UnityEngine.Object> members = new List<UnityEngine.Object>(Sprites.Length);
            for (int i = 0; i < Sprites.Length; i++)
            {
                string path = $"{ArtRoot}/{Sprites[i].File}.png";
                Sprite sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
                if (sprite != null)
                {
                    members.Add(sprite);
                }
            }

            atlas.Add(members.ToArray());

            // ASTC 6x6 on both mobile targets. 4x4 is the fallback if the golden bevel bands.
            TextureImporterPlatformSettings android = new TextureImporterPlatformSettings
            {
                name = "Android",
                overridden = true,
                maxTextureSize = 2048,
                format = TextureImporterFormat.ASTC_6x6,
                textureCompression = TextureImporterCompression.Compressed
            };

            TextureImporterPlatformSettings ios = new TextureImporterPlatformSettings
            {
                name = "iPhone",
                overridden = true,
                maxTextureSize = 2048,
                format = TextureImporterFormat.ASTC_6x6,
                textureCompression = TextureImporterCompression.Compressed
            };

            atlas.SetPlatformSettings(android);
            atlas.SetPlatformSettings(ios);

            SpriteAtlasAsset.Save(atlas, AtlasPath);
            AssetDatabase.ImportAsset(AtlasPath, ImportAssetOptions.ForceUpdate);

            VerifyAtlas(members.Count);
        }

        /// <summary>
        /// Reads the atlas settings back off the imported asset and refuses on any mismatch.
        ///
        /// Without this the tool reports what it ASKED for, not what landed — and the two diverged in
        /// exactly this file: the report said "ASTC 6x6 on Android and iPhone" while the importer held
        /// overridden=false and AutomaticCompressed, and it said "ok" while tight packing and rotation
        /// were on, which let neighbouring sprites nest into each other's concave areas and bleed into the
        /// rectangle a UI Image samples. Every one of those is silent in the report and visible only as
        /// wrong pixels in a prefab.
        /// </summary>
        private static void VerifyAtlas(int expectedMembers)
        {
            SpriteAtlas imported = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(AtlasPath);

            if (imported == null)
            {
                throw new InvalidOperationException($"{AtlasPath} did not import as a {nameof(SpriteAtlas)}");
            }

            if (imported.spriteCount != expectedMembers)
            {
                throw new InvalidOperationException(
                    $"atlas holds {imported.spriteCount} sprites, expected {expectedMembers}");
            }

            SpriteAtlasPackingSettings packing = SpriteAtlasExtensions.GetPackingSettings(imported);

            // Tight packing plus rotation is what produced foreign icon fragments inside other sprites:
            // a concave silhouette lets a neighbour sit inside its bounding rectangle, and an Image samples
            // the rectangle.
            if (packing.enableTightPacking)
            {
                throw new InvalidOperationException("atlas packing has enableTightPacking on");
            }

            if (packing.enableRotation)
            {
                throw new InvalidOperationException("atlas packing has enableRotation on");
            }

            if (!packing.enableAlphaDilation)
            {
                throw new InvalidOperationException("atlas packing has enableAlphaDilation off");
            }

            foreach (string platform in new[] { "Android", "iPhone" })
            {
                TextureImporterPlatformSettings settings = SpriteAtlasExtensions.GetPlatformSettings(imported, platform);

                if (!settings.overridden)
                {
                    throw new InvalidOperationException($"atlas {platform} settings are not overridden");
                }

                if (settings.format != TextureImporterFormat.ASTC_6x6)
                {
                    throw new InvalidOperationException(
                        $"atlas {platform} format reads back as {settings.format}, not ASTC_6x6");
                }

                Log.Add($"atlas verified {platform}: overridden={settings.overridden} format={settings.format}");
            }

            Log.Add($"atlas verified {AtlasPath} sprites={imported.spriteCount} " +
                    $"tightPacking={packing.enableTightPacking} rotation={packing.enableRotation}");
        }

        // ---------------------------------------------------------------- font

        private static void BuildFontAsset()
        {
            Font font = AssetDatabase.LoadAssetAtPath<Font>(FontTtfPath);
            if (font == null)
            {
                Log.Add($"MISSING {FontTtfPath}");
                return;
            }

            if (AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FontAssetPath) != null)
            {
                Log.Add($"font asset already present at {FontAssetPath}");
                return;
            }

            TMP_FontAsset asset = TMP_FontAsset.CreateFontAsset(font, 90, 9, GlyphRenderMode.SDFAA,
                                                                1024, 1024, AtlasPopulationMode.Dynamic);
            asset.name = "Fredoka SDF";
            AssetDatabase.CreateAsset(asset, FontAssetPath);

            // The atlas texture and material are sub-assets; without this they are lost on reload.
            if (asset.atlasTextures != null && asset.atlasTextures.Length > 0)
            {
                asset.atlasTextures[0].name = "Fredoka SDF Atlas";
                AssetDatabase.AddObjectToAsset(asset.atlasTextures[0], asset);
            }

            if (asset.material != null)
            {
                asset.material.name = "Fredoka SDF Material";
                AssetDatabase.AddObjectToAsset(asset.material, asset);
            }

            AssetDatabase.SaveAssets();
            Log.Add($"font asset {FontAssetPath}");
        }

        // ---------------------------------------------------------------- config assets

        private static void BuildProbeAsset()
        {
            // One entry in the remote group, so "did the remote catalog load" has a direct answer instead
            // of an inference from init succeeding.
            File.WriteAllText(ProbePath,
                "peerplay remote catalog sentinel — presence of this address means the remote catalog loaded\n");
            AssetDatabase.ImportAsset(ProbePath, ImportAssetOptions.ForceUpdate);
            Log.Add($"probe {ProbePath}");
        }

        private static void BuildCatalog()
        {
            PopupCatalog catalog = AssetDatabase.LoadAssetAtPath<PopupCatalog>(CatalogPath);

            if (catalog == null)
            {
                catalog = ScriptableObject.CreateInstance<PopupCatalog>();
                AssetDatabase.CreateAsset(catalog, CatalogPath);
            }

            SerializedObject so = new SerializedObject(catalog);
            SerializedProperty entries = so.FindProperty("_entries");
            entries.ClearArray();

            AuthorEntry(entries, 0, "info", "popup_info", "", "info.title", "info.body");
            AuthorEntry(entries, 1, "confirm", "popup_confirm", "", "confirm.title", "confirm.body");
            AuthorEntry(entries, 2, "reward", "popup_reward", "", "reward.title", "reward.body");
            AuthorEntry(entries, 3, "offer_weekend", "popup_offer",
                        "https://d2eupgfrfppc7x.cloudfront.net/peerplay/content/offer_weekend.png",
                        "offer.weekend.title", "offer.weekend.body");

            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(catalog);
            Log.Add($"catalog {CatalogPath} entries=4");
        }

        private static void AuthorEntry(SerializedProperty entries, int index, string keyId, string assetId,
                                        string imageUrl, string titleKey, string bodyKey)
        {
            entries.InsertArrayElementAtIndex(index);
            SerializedProperty entry = entries.GetArrayElementAtIndex(index);
            entry.FindPropertyRelative("KeyId").stringValue = keyId;
            entry.FindPropertyRelative("AssetId").stringValue = assetId;
            entry.FindPropertyRelative("ImageUrl").stringValue = imageUrl;
            entry.FindPropertyRelative("TitleKey").stringValue = titleKey;
            entry.FindPropertyRelative("BodyKey").stringValue = bodyKey;
        }

        // ---------------------------------------------------------------- prefabs

        private static void BuildPrefabs()
        {
            BuildPrefab<InfoPopupView>("popup_info", PopupModality.Modal, PopupSuspendBehaviour.Hide,
                                       "fade", "icon_info", false, false);
            BuildPrefab<ConfirmPopupView>("popup_confirm", PopupModality.Modal, PopupSuspendBehaviour.Hide,
                                          "fade", "icon_warning", true, false);
            BuildPrefab<RewardPopupView>("popup_reward", PopupModality.Modal, PopupSuspendBehaviour.Hide,
                                         "scale_pop", "icon_coins", false, false);
            BuildPrefab<OfferPopupView>("popup_offer", PopupModality.Modal, PopupSuspendBehaviour.StayVisible,
                                        "scale_pop", "icon_download", false, true);
        }

        private static void BuildPrefab<TView>(string assetName, PopupModality modality,
                                               PopupSuspendBehaviour suspend, string transitionId,
                                               string iconSprite, bool twoButtons, bool remoteContent)
            where TView : PopupView
        {
            GameObject root = NewRect(assetName, null, DialogWidth, 0f);
            CanvasGroup group = root.AddComponent<CanvasGroup>();
            TView view = root.AddComponent<TView>();

            GameObject panel = NewRect("Panel", root.transform, DialogWidth, 0f);
            Image panelImage = panel.AddComponent<Image>();
            panelImage.sprite = LoadSprite("panel");
            panelImage.type = Image.Type.Sliced;

            // Height comes from the layout group, never from a sizeDelta measured against 1920: at 3:4 the
            // canvas is only 1440 units tall, and a dialog authored against 9:16 would not fit.
            VerticalLayoutGroup layout = panel.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(48, 48, 72, 48);
            layout.spacing = 24f;
            layout.childAlignment = TextAnchor.UpperCenter;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            ContentSizeFitter fitter = panel.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;

            GameObject ribbon = NewRect("Ribbon", panel.transform, DialogWidth - 160f, 128f);
            Image ribbonImage = ribbon.AddComponent<Image>();
            ribbonImage.sprite = LoadSprite("ribbon");
            ribbonImage.type = Image.Type.Sliced;
            AddLayoutHeight(ribbon, 128f);

            TMP_Text title = NewLabel("Title", ribbon.transform, 52f, FontStyles.Bold);
            Stretch((RectTransform)title.transform);

            TMP_Text body = NewLabel("Body", panel.transform, 40f, FontStyles.Normal);
            body.alignment = TextAlignmentOptions.Top;

            // A SEPARATE label for the payload's own line. Aliasing it onto Body would make every Bind
            // overwrite the authored copy it had just written one statement earlier — the two have
            // different owners (the config authors Body, the caller authors this) and so need two labels.
            TMP_Text extra = NewLabel("Extra", panel.transform, 36f, FontStyles.Bold);

            GameObject icon = NewRect("Icon", panel.transform, 160f, 160f);
            Image iconImage = icon.AddComponent<Image>();
            iconImage.sprite = LoadSprite(iconSprite);
            iconImage.preserveAspect = true;
            AddLayoutHeight(icon, 160f);

            Image contentImage = null;
            TMP_Text unavailable = null;
            RectTransform spinner = null;

            if (remoteContent)
            {
                GameObject content = NewRect("ContentImage", panel.transform, DialogWidth - 160f, 360f);
                contentImage = content.AddComponent<Image>();
                contentImage.sprite = LoadSprite("burst_gen");
                contentImage.preserveAspect = true;
                AddLayoutHeight(content, 360f);

                GameObject spin = NewRect("Spinner", content.transform, 96f, 96f);
                Image spinImage = spin.AddComponent<Image>();
                spinImage.sprite = LoadSprite("ring");
                spinImage.raycastTarget = false;
                spinner = (RectTransform)spin.transform;
                spin.SetActive(false);

                unavailable = NewLabel("Unavailable", panel.transform, 32f, FontStyles.Italic);
                unavailable.gameObject.SetActive(false);
            }

            GameObject buttonRow = NewRect("Buttons", panel.transform, DialogWidth - 96f, 128f);
            HorizontalLayoutGroup row = buttonRow.AddComponent<HorizontalLayoutGroup>();
            row.spacing = 24f;
            row.childAlignment = TextAnchor.MiddleCenter;
            row.childControlWidth = true;
            row.childControlHeight = true;
            row.childForceExpandWidth = true;
            AddLayoutHeight(buttonRow, 128f);

            Button primary = NewButton("Primary", buttonRow.transform, "button_primary", twoButtons ? "Confirm" : "OK");
            Button secondary = twoButtons ? NewButton("Secondary", buttonRow.transform, "button_secondary", "Cancel") : null;

            GameObject close = NewRect("Close", root.transform, 96f, 96f);
            RectTransform closeRect = (RectTransform)close.transform;
            closeRect.anchorMin = new Vector2(1f, 1f);
            closeRect.anchorMax = new Vector2(1f, 1f);
            closeRect.pivot = new Vector2(1f, 1f);
            closeRect.anchoredPosition = new Vector2(-24f, -24f);
            Image closeImage = close.AddComponent<Image>();
            closeImage.sprite = LoadSprite("close");
            Button closeButton = close.AddComponent<Button>();

            // A two-button popup answers through its own confirm/cancel handlers, so only the corner X is a
            // generic close. A one-button popup's primary IS the close.
            Button[] closeButtons = twoButtons
                ? new[] { closeButton }
                : new[] { closeButton, primary };

            WireView(view, group, (RectTransform)root.transform, (RectTransform)panel.transform, spinner,
                     contentImage, unavailable, modality, suspend, transitionId, closeButtons,
                     title, body, extra, twoButtons ? primary : null, secondary);

            string path = $"{PrefabRoot}/{assetName}.prefab";
            PrefabUtility.SaveAsPrefabAsset(root, path);
            UnityEngine.Object.DestroyImmediate(root);
            Log.Add($"prefab {path} view={typeof(TView).Name} modality={modality} suspend={suspend} transition={transitionId}");
        }

        /// <summary>
        /// Serialized private fields written through SerializedObject rather than through a runtime setter:
        /// the runtime type stays free of authoring API, and a renamed field fails here loudly instead of
        /// leaving a silent {fileID: 0}.
        /// </summary>
        private static void WireView(PopupView view, CanvasGroup group, RectTransform root, RectTransform content,
                                     RectTransform spinner, Image contentImage, TMP_Text unavailable,
                                     PopupModality modality, PopupSuspendBehaviour suspend, string transitionId,
                                     Button[] closeButtons, TMP_Text title, TMP_Text body, TMP_Text extra,
                                     Button confirmButton, Button cancelButton)
        {
            SerializedObject so = new SerializedObject(view);

            Set(so, "_canvasGroup", group);
            Set(so, "_root", root);
            Set(so, "_contentRoot", content);
            Set(so, "_spinner", spinner);
            Set(so, "_contentImage", contentImage);
            Set(so, "_unavailableLabel", unavailable);
            Set(so, "_placeholderSprite", contentImage != null ? contentImage.sprite : null);

            so.FindProperty("_modality").enumValueIndex = (int)modality;
            so.FindProperty("_suspendBehaviour").enumValueIndex = (int)suspend;
            so.FindProperty("_transitionId").stringValue = transitionId;
            so.FindProperty("_dismissOnBackdropTap").boolValue = true;
            so.FindProperty("_dismissible").boolValue = true;

            SerializedProperty buttons = so.FindProperty("_closeButtons");
            buttons.ClearArray();
            for (int i = 0; i < closeButtons.Length; i++)
            {
                buttons.InsertArrayElementAtIndex(i);
                buttons.GetArrayElementAtIndex(i).objectReferenceValue = closeButtons[i];
            }

            Set(so, "_title", title);
            Set(so, "_body", body);

            // Exactly one of these exists per concrete view; Set is a no-op for the others.
            Set(so, "_detail", extra);
            Set(so, "_amount", extra);
            Set(so, "_price", extra);
            Set(so, "_confirmButton", confirmButton);
            Set(so, "_cancelButton", cancelButton);

            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void Set(SerializedObject so, string field, UnityEngine.Object value)
        {
            SerializedProperty property = so.FindProperty(field);
            if (property != null)
            {
                property.objectReferenceValue = value;
            }
        }

        // ---------------------------------------------------------------- small builders

        private static GameObject NewRect(string name, Transform parent, float width, float height)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            RectTransform rect = (RectTransform)go.transform;

            if (parent != null)
            {
                rect.SetParent(parent, false);
            }

            rect.localScale = Vector3.one;
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = new Vector2(width, height);
            return go;
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        private static void AddLayoutHeight(GameObject go, float height)
        {
            LayoutElement element = go.AddComponent<LayoutElement>();
            element.preferredHeight = height;
            element.minHeight = height;
        }

        private static TMP_Text NewLabel(string name, Transform parent, float size, FontStyles style)
        {
            GameObject go = NewRect(name, parent, DialogWidth - 160f, size * 1.4f);
            TextMeshProUGUI label = go.AddComponent<TextMeshProUGUI>();
            label.fontSize = size;
            label.fontStyle = style;
            label.alignment = TextAlignmentOptions.Center;
            label.enableWordWrapping = true;
            label.raycastTarget = false;
            label.text = name;

            TMP_FontAsset font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FontAssetPath);
            if (font != null)
            {
                label.font = font;
            }

            return label;
        }

        private static Button NewButton(string name, Transform parent, string sprite, string caption)
        {
            GameObject go = NewRect(name, parent, 420f, 128f);
            Image image = go.AddComponent<Image>();
            image.sprite = LoadSprite(sprite);
            image.type = Image.Type.Sliced;

            Button button = go.AddComponent<Button>();
            button.targetGraphic = image;

            TMP_Text label = NewLabel("Label", go.transform, 40f, FontStyles.Bold);
            Stretch((RectTransform)label.transform);
            label.text = caption;
            label.alignment = TextAlignmentOptions.Center;

            return button;
        }

        private static Sprite LoadSprite(string file)
        {
            return AssetDatabase.LoadAssetAtPath<Sprite>($"{ArtRoot}/{file}.png");
        }

        private static void EnsureFolders()
        {
            EnsureFolder("Assets/Art", "Generated");
            EnsureFolder("Assets", "Prefabs");
            EnsureFolder("Assets/Prefabs", "Popups");
            EnsureFolder("Assets", "Config");
        }

        private static void EnsureFolder(string parent, string child)
        {
            if (!AssetDatabase.IsValidFolder($"{parent}/{child}"))
            {
                AssetDatabase.CreateFolder(parent, child);
            }
        }

        private static void WriteReport(string status, string error)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("{\n  \"status\": \"").Append(status).Append("\",\n");
            sb.Append("  \"error\": ").Append(error == null ? "null" : "\"" + error.Replace("\"", "'") + "\"").Append(",\n");
            sb.Append("  \"steps\": [\n");

            for (int i = 0; i < Log.Count; i++)
            {
                sb.Append("    \"").Append(Log[i].Replace("\\", "/").Replace("\"", "'")).Append('"');
                sb.Append(i == Log.Count - 1 ? "\n" : ",\n");
            }

            sb.Append("  ]\n}\n");

            Directory.CreateDirectory("Tools");
            File.WriteAllText(ReportPath, sb.ToString());
        }
    }
}

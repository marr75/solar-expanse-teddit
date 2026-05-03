using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Data.ScriptableObject;
using Game.CompanyScripts;
using Game.ObjectInfoDataScripts;
using Game.ObjectInfoDataScripts.CustomFacilitiesAndModules;
using Game.UI.Windows.Elements.SpaceCraftConstructElements;
using Language;
using Manager;
using Newtonsoft.Json.Linq;
using ScriptableObjectScripts;
using TMPro;
using UnityEngine;

namespace Teddit
{
    internal static class FacilityCreator
    {
        static readonly string[] CurrentLangCandidates =
            { "_currentLaungage", "_currentLanguage", "currentLaungage", "currentLanguage" };
        static readonly string[] DefaultLangCandidates =
            { "_defaultLanguage", "_defaultLaungage", "defaultLanguage", "defaultLaungage" };

        /// <summary>
        /// Creates a new GroundFacilityDescriptor from a field dict and injects it into AllFacility.
        /// Called by ScriptableObjectPatcher.RunFacilities when an ID is not found in the game's list.
        /// </summary>
        internal static void CreateAndInjectFacility(string id, Dictionary<string, JToken> def, string modDir)
        {
            var desc = ScriptableObject.CreateInstance<GroundFacilityDescriptor>();
            desc.name = id;
            SetField(desc, "id", id);

            // Icon
            Sprite sprite = ResolveConfiguredSprite(def, modDir, id, null);
            if (sprite == null)
            {
                var fallbackList = SerializedMonoBehaviourSingleton<AllScriptableObjectManager>.Instance
                    .AllFacility.List;
                if (fallbackList != null && fallbackList.Count > 0)
                    sprite = fallbackList[0].Sprite;
            }

            // Required non-null fields
            SetField(desc, "canBuildParameter", new CanBuildParameter());
            double buildCost = GetVal<double>(def, "buildCost", 0.0);
            SetField(desc, "price", new ResourcePrice(new List<ResourcePriceOne>(), buildCost));

            SetField(desc, "sprite", sprite);
            SetField(desc, "showOnUI", true);
            SetField(desc, "canBeScrapped",  GetVal<bool>(def, "canBeScrapped",  true));
            SetField(desc, "canBeTurnedOff", GetVal<bool>(def, "canBeTurnedOff", true));
            SetField(desc, "isLocked",       GetVal<bool>(def, "isLocked",       false));
            SetField(desc, "sortingShop",    GetVal<int>(def,  "sortingShop",    999));
            SetField(desc, "blockStacking",  GetVal<bool>(def, "blockStacking",  false));

            JToken tok;
            if (def.TryGetValue("maintenanceCostPerDay", out tok)) SetField(desc, "maintenanceCostPerDay", tok.Value<float>());
            if (def.TryGetValue("energyConsumption",    out tok)) SetField(desc, "energyConsumption",    tok.Value<double>());
            if (def.TryGetValue("needWorkersToWork",    out tok)) SetField(desc, "needWorkersToWork",    tok.Value<int>());
            if (def.TryGetValue("upkeepStacking",                    out tok)) desc.upkeepStacking      = tok.Value<bool>();
            if (def.TryGetValue("upkeepStackingValue",              out tok)) desc.upkeepStackingValue = tok.Value<float>();
            if (def.TryGetValue("timeToBuildInDays",               out tok)) SetField(desc, "timeToBuildInDays", tok.Value<float>());
            if (def.TryGetValue("constructionEquipmentCountIsRequired", out tok)) SetField(desc, "constructionEquipmentCountIsRequired", tok.Value<bool>());

            string ftStr = GetVal<string>(def, "facilityType", "Other");
            FacilityBaseDescriptor.EFacilityType ft;
            if (Enum.TryParse(ftStr, out ft))
                SetField(desc, "facilityType", ft);
            else
                Plugin.Log.LogWarning($"[FacilityCreator] Unknown facilityType '{ftStr}' for {id}");

            string placeStr = GetVal<string>(def, "possiblePlacement", "Both");
            SetField(desc, "possiblePlacement", ParsePlacement(placeStr));

            string specialStr = GetVal<string>(def, "specialAbility", null);
            if (!string.IsNullOrEmpty(specialStr))
            {
                try { desc.specialAbilityFacilityNew = (ESpecialAbilityFacilityNew)Enum.Parse(typeof(ESpecialAbilityFacilityNew), specialStr); }
                catch { Plugin.Log.LogWarning($"[FacilityCreator] Unknown specialAbility '{specialStr}' for {id}"); }
            }
            if (def.TryGetValue("specialAbilityParameter", out tok)) desc.specialAbilityParameter = tok.Value<float>();
            if (def.TryGetValue("resourceExplorationBonus", out tok)) desc.resourceExplorationBonus = tok.Value<float>();

            double energyProd = GetVal<double>(def, "energyProduction", 0.0);
            bool solar = GetVal<bool>(def, "solarPanels", false);
            bool wind  = GetVal<bool>(def, "windPower",   false);
            bool geo   = GetVal<bool>(def, "geothermal",  false);
            if (energyProd > 0.0 || solar || wind || geo)
            {
                desc.energyProductionData = new EnergyProductionData
                {
                    energyProduction  = energyProd,
                    solarPanels       = solar,
                    windPower         = wind,
                    geothermalPower   = geo,
                };
            }

            string name         = GetVal<string>(def, "name",         null);
            string description  = GetVal<string>(def, "description",  null);
            string capabilities = GetVal<string>(def, "capabilities", null);
            if (!string.IsNullOrEmpty(name) || !string.IsNullOrEmpty(description) || !string.IsNullOrEmpty(capabilities))
                InjectTranslations(id, name, description, capabilities);

            string facilityItemClassName = GetVal<string>(def, "facilityItemClass", null);
            bool hasLabData = def.ContainsKey("labBonusToResearchInPerHour") || def.ContainsKey("labResearchSubTypeId") || def.ContainsKey("labIdToBonus");
            bool hasResourcesToMine = def.ContainsKey("resourcesToMine");
            if (string.Equals(facilityItemClassName, "LabFacility", StringComparison.OrdinalIgnoreCase)
                || desc.specialAbilityFacilityNew == ESpecialAbilityFacilityNew.Lab
                || hasLabData)
            {
                SetField(desc, "facilityItemClass", typeof(LabFacility));
                if (desc.bonusData == null) desc.bonusData = new BonusData();
                if (desc.labData == null) desc.labData = new LabData();
                if (def.TryGetValue("labBonusToResearchInPerHour", out tok) && tok.Type != JTokenType.Null)
                    desc.labData.bonusToResearchInPerHour = tok.Value<int>();
                desc.labData.idResearchSubType = string.Empty;
                desc.labData.idToBonus = Array.Empty<string>();
                if (def.TryGetValue("labResearchSubTypeId", out tok))
                    desc.labData.idResearchSubType = tok.Type == JTokenType.Null ? string.Empty : (tok.Value<string>() ?? string.Empty);
                if (def.TryGetValue("labIdToBonus", out tok) && tok.Type == JTokenType.Array)
                    desc.labData.idToBonus = ((JArray)tok).Select(x => x.Value<string>()).Where(x => !string.IsNullOrEmpty(x)).ToArray();
            }
            else if (string.Equals(facilityItemClassName, "MiningFacility", StringComparison.OrdinalIgnoreCase)
                || desc.specialAbilityFacilityNew == ESpecialAbilityFacilityNew.Mining
                || hasResourcesToMine)
            {
                SetField(desc, "facilityItemClass", typeof(MiningFacility));
            }

            if (desc.bonusData == null)
                desc.bonusData = new BonusData();

            // Complex fields (buildResources, resourcesToMine, refinerInput/Output, byproducts)
            ScriptableObjectPatcher.ApplyFacilityComplexFields(desc, def, id);

            // Register in AllFacility
            var allFacility = SerializedMonoBehaviourSingleton<AllScriptableObjectManager>.Instance.AllFacility;
            Type baseType = allFacility.GetType().BaseType;
            var listFI   = FindField(baseType, "list");
            var listNEFI = FindField(baseType, "listNotEmpty");
            if (listFI == null || listNEFI == null)
                throw new Exception("Could not find AllFacility list fields via reflection.");

            ((List<FacilityBaseDescriptor>)listFI.GetValue(allFacility)).Add(desc);
            ((List<FacilityBaseDescriptor>)listNEFI.GetValue(allFacility)).Add(desc);

            try { SerializedMonoBehaviourSingleton<AllScriptableObjectManager>.Instance.AllMyIDScriptableObjects.Add(desc); }
            catch (Exception ex) { Plugin.Log.LogWarning($"[FacilityCreator] AllMyIDScriptableObjects.Add failed: {ex.Message}"); }

            Plugin.Log.LogInfo($"[FacilityCreator] + {id} ({ft}, {placeStr})");
        }

        // ── TMP sprite registration ───────────────────────────────────────────────

        internal static void RegisterSpriteWithTMP(string spriteName, Texture2D tex)
        {
            try
            {
                // Find an already-upgraded facility sprite asset to inject into.
                // Each built-in facility is its own TMP_SpriteAsset with a single sprite.
                // We find the first one with a valid material+texture, duplicate its texture
                // into a Texture2DArray alongside ours, then add our glyph/character to it.
                // Simpler: just find any asset that is already upgraded and add our character
                // to it directly, pointing to a new atlas index with our texture.
                //
                // Simplest working approach: find an existing upgraded asset, add our
                // character to its tables, then call UpdateLookupTables (safe on upgraded assets).

                var all = Resources.FindObjectsOfTypeAll<TMP_SpriteAsset>();
                TMP_SpriteAsset target = null;
                foreach (var a in all)
                {
                    if (a == null || a.material == null) continue;
                    // Access via reflection to avoid triggering upgrade on potentially broken assets
                    var charFi = typeof(TMP_SpriteAsset).GetField("m_SpriteCharacterTable", BindingFlags.NonPublic | BindingFlags.Instance)
                              ?? typeof(TMP_SpriteAsset).GetField("spriteCharacterTable",   BindingFlags.NonPublic | BindingFlags.Instance);
                    var chars = charFi?.GetValue(a) as List<TMP_SpriteCharacter>;
                    if (chars != null && chars.Count > 0) { target = a; break; }
                }

                if (target == null)
                {
                    Plugin.Log.LogWarning("[FacilityCreator] No suitable TMP_SpriteAsset found — tooltip icon will be missing.");
                    return;
                }

                var assetType   = typeof(TMP_SpriteAsset);
                var glyphListFi = assetType.GetField("m_SpriteGlyphTable",     BindingFlags.NonPublic | BindingFlags.Instance)
                               ?? assetType.GetField("spriteGlyphTable",        BindingFlags.NonPublic | BindingFlags.Instance);
                var charListFi  = assetType.GetField("m_SpriteCharacterTable",  BindingFlags.NonPublic | BindingFlags.Instance)
                               ?? assetType.GetField("spriteCharacterTable",    BindingFlags.NonPublic | BindingFlags.Instance);
                if (glyphListFi == null || charListFi == null) return;

                var glyphs = (List<TMP_SpriteGlyph>)glyphListFi.GetValue(target);
                var chars2  = (List<TMP_SpriteCharacter>)charListFi.GetValue(target);

                // Check not already registered
                foreach (var ch in chars2)
                    if (ch.name == spriteName) return;

                // The existing asset uses a Texture2D spriteSheet. We can't easily share one
                // atlas texture. Instead: create a new per-sprite asset (no CreateInstance —
                // use Instantiate to clone an already-upgraded asset, then replace its data).
                var clone = UnityEngine.Object.Instantiate(target);
                clone.name = spriteName;
                clone.spriteSheet = tex;
                clone.material = new Material(target.material);
                clone.material.mainTexture = tex;

                var cloneGlyphs = new List<TMP_SpriteGlyph>();
                var cloneChars  = new List<TMP_SpriteCharacter>();

                // Borrow metrics/scale from the existing glyph so sizing matches built-in sprites
                var existingGlyphs = (List<TMP_SpriteGlyph>)glyphListFi.GetValue(target);
                var refGlyph = existingGlyphs != null && existingGlyphs.Count > 0 ? existingGlyphs[0] : null;

                var glyph = new TMP_SpriteGlyph();
                glyph.index      = 0;
                glyph.metrics    = refGlyph != null ? refGlyph.metrics : new UnityEngine.TextCore.GlyphMetrics(tex.width, tex.height, 0, tex.height, tex.width);
                glyph.glyphRect  = new UnityEngine.TextCore.GlyphRect(0, 0, tex.width, tex.height);
                glyph.scale      = refGlyph != null ? refGlyph.scale : 1f;
                glyph.atlasIndex = 0;
                cloneGlyphs.Add(glyph);

                var character = new TMP_SpriteCharacter(0, glyph);
                character.name  = spriteName;
                character.scale = 1f;
                cloneChars.Add(character);

                glyphListFi.SetValue(clone, cloneGlyphs);
                charListFi.SetValue(clone,  cloneChars);
                clone.UpdateLookupTables();

                var defaultAsset = TMP_Settings.defaultSpriteAsset;
                if (defaultAsset != null)
                {
                    if (defaultAsset.fallbackSpriteAssets == null)
                        defaultAsset.fallbackSpriteAssets = new List<TMP_SpriteAsset>();
                    defaultAsset.fallbackSpriteAssets.Add(clone);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[FacilityCreator] TMP sprite registration failed for {spriteName}: {ex.Message}");
            }
        }

        internal static void EnsureSpriteRegisteredWithTMP(Sprite sprite, string spriteNameOverride = null)
        {
            if (sprite == null) return;
            string spriteName = string.IsNullOrEmpty(spriteNameOverride) ? sprite.name : spriteNameOverride;
            if (string.IsNullOrEmpty(spriteName)) return;

            var existingAssets = Resources.FindObjectsOfTypeAll<TMP_SpriteAsset>();
            foreach (var asset in existingAssets)
            {
                if (asset == null) continue;
                try
                {
                    var chars = asset.spriteCharacterTable;
                    if (chars == null) continue;
                    foreach (var ch in chars)
                    {
                        if (ch != null && string.Equals(ch.name, spriteName, StringComparison.OrdinalIgnoreCase))
                            return;
                    }
                }
                catch
                {
                    // Ignore broken/partial assets and keep scanning.
                }
            }

            Texture2D tex = ExtractTexture(sprite, spriteName);
            if (tex != null)
                RegisterSpriteWithTMP(spriteName, tex);
        }

        static Texture2D ExtractTexture(Sprite sprite, string textureName)
        {
            try
            {
                Rect rect = sprite.rect;
                Texture2D source = sprite.texture;
                int width = Mathf.RoundToInt(rect.width);
                int height = Mathf.RoundToInt(rect.height);
                if (source == null || width <= 0 || height <= 0)
                    return null;

                var tex = new Texture2D(width, height, TextureFormat.ARGB32, mipChain: false);
                tex.name = textureName;

                try
                {
                    Color[] pixels = source.GetPixels(
                        Mathf.RoundToInt(rect.x),
                        Mathf.RoundToInt(rect.y),
                        width,
                        height);
                    tex.SetPixels(pixels);
                    tex.Apply();
                    return tex;
                }
                catch
                {
                    var rt = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
                    var prev = RenderTexture.active;
                    Graphics.Blit(source, rt);
                    RenderTexture.active = rt;
                    tex.ReadPixels(new Rect(rect.x, rect.y, width, height), 0, 0);
                    tex.Apply();
                    RenderTexture.active = prev;
                    RenderTexture.ReleaseTemporary(rt);
                    return tex;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[FacilityCreator] Failed to extract sprite texture '{textureName}': {ex.Message}");
                return null;
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        internal static string GetSpriteReference(Sprite sprite)
        {
            return string.IsNullOrEmpty(sprite?.name) ? null : sprite.name;
        }

        internal static Sprite ResolveConfiguredSprite(Dictionary<string, JToken> def, string modDir, string spriteName, Sprite fallback)
        {
            string iconRef = GetVal<string>(def, "iconRef", null);
            if (!string.IsNullOrEmpty(iconRef))
            {
                var referenced = FindBaseGameSprite(iconRef);
                if (referenced != null)
                    return referenced;

                Plugin.Log.LogWarning($"[FacilityCreator] Base-game iconRef not found: {iconRef}");
            }

            string iconRelPath = GetVal<string>(def, "icon", null);
            if (!string.IsNullOrEmpty(iconRelPath))
            {
                string iconFull = Path.IsPathRooted(iconRelPath) || string.IsNullOrEmpty(modDir)
                    ? iconRelPath
                    : Path.Combine(modDir, iconRelPath);
                var loaded = LoadSprite(iconFull, spriteName);
                if (loaded != null)
                    return loaded;
            }

            return fallback;
        }

        internal static Sprite FindBaseGameSprite(string spriteRef)
        {
            if (string.IsNullOrWhiteSpace(spriteRef))
                return null;

            var allSO = SerializedMonoBehaviourSingleton<AllScriptableObjectManager>.Instance;
            if (allSO == null)
                return null;

            var sprite = FindSpriteInFacilities(allSO.AllFacility?.List, spriteRef);
            if (sprite != null) return sprite;

            sprite = FindSpriteInSpacecraft(allSO.AllSpacecraftType?.List, spriteRef);
            if (sprite != null) return sprite;

            sprite = FindSpriteInLaunchVehicles(allSO.AllLaunchVehicleType?.List, spriteRef);
            if (sprite != null) return sprite;

            foreach (var candidate in Resources.FindObjectsOfTypeAll<Sprite>())
            {
                if (candidate != null && string.Equals(candidate.name, spriteRef, StringComparison.OrdinalIgnoreCase))
                    return candidate;
            }
            return null;
        }

        static Sprite FindSpriteInFacilities(IList<FacilityBaseDescriptor> list, string spriteRef)
        {
            if (list == null) return null;
            foreach (var facility in list)
            {
                var sprite = facility?.Sprite;
                if (sprite != null && string.Equals(sprite.name, spriteRef, StringComparison.OrdinalIgnoreCase))
                    return sprite;
            }
            return null;
        }

        static Sprite FindSpriteInSpacecraft(IList<SpacecraftType> list, string spriteRef)
        {
            if (list == null) return null;
            foreach (var spacecraft in list)
            {
                var sprite = spacecraft?.RocketBackGround;
                if (sprite != null && string.Equals(sprite.name, spriteRef, StringComparison.OrdinalIgnoreCase))
                    return sprite;
            }
            return null;
        }

        static Sprite FindSpriteInLaunchVehicles(IList<LaunchVehicleType> list, string spriteRef)
        {
            if (list == null) return null;
            var rocketBgFi = FindField(typeof(LaunchVehicleType), "rocketBackGround");
            foreach (var vehicle in list)
            {
                var sprite = rocketBgFi?.GetValue(vehicle) as Sprite;
                if (sprite != null && string.Equals(sprite.name, spriteRef, StringComparison.OrdinalIgnoreCase))
                    return sprite;
            }
            return null;
        }

        internal static T GetVal<T>(Dictionary<string, JToken> d, string key, T fallback = default(T))
        {
            JToken t;
            if (!d.TryGetValue(key, out t) || t == null || t.Type == JTokenType.Null) return fallback;
            try { return t.Value<T>(); } catch { return fallback; }
        }

        internal static Sprite LoadSprite(string fullPath, string spriteName)
        {
            if (!File.Exists(fullPath))
            {
                Plugin.Log.LogWarning($"[FacilityCreator] Icon not found: {fullPath}");
                return null;
            }
            try
            {
                byte[] bytes = File.ReadAllBytes(fullPath);
                string fmt = DetectImageFormat(bytes);
                if (fmt != "PNG" && fmt != "JPEG")
                {
                    Plugin.Log.LogWarning($"[FacilityCreator] Unsupported image format ({fmt}) for icon: {fullPath}. Convert to PNG or JPEG.");
                    return null;
                }
                var tex = new Texture2D(2, 2, TextureFormat.ARGB32, mipChain: false);
                tex.name = spriteName;
                if (!ImageConversion.LoadImage(tex, bytes))
                {
                    Plugin.Log.LogWarning($"[FacilityCreator] Failed to decode {fmt} image: {fullPath}");
                    return null;
                }
                var sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
                sprite.name = spriteName;
                Plugin.Log.LogInfo($"[FacilityCreator] Loaded icon ({tex.width}x{tex.height} {fmt}): {Path.GetFileName(fullPath)}");
                RegisterSpriteWithTMP(spriteName, tex);
                return sprite;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[FacilityCreator] Icon load error for {fullPath}: {ex.Message}");
                return null;
            }
        }

        static string DetectImageFormat(byte[] b)
        {
            if (b.Length >= 4 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return "PNG";
            if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "JPEG";
            if (b.Length >= 12 && b[0] == 0x52 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x46 &&
                b[8] == 0x57 && b[9] == 0x45 && b[10] == 0x42 && b[11] == 0x50) return "WEBP";
            if (b.Length >= 2 && b[0] == 0x42 && b[1] == 0x4D) return "BMP";
            if (b.Length >= 3 && b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46) return "GIF";
            return "UNKNOWN";
        }

        internal static void InjectTranslations(string id, string name, string description, string capabilities = null)
        {
            try
            {
                var le = MonoBehaviourSingleton<LEManager>.Instance;
                var leType = typeof(LEManager);
                object currentLang = GetFieldValueByNames(le, leType, CurrentLangCandidates);
                object defaultLang  = GetFieldValueByNames(le, leType, DefaultLangCandidates);
                foreach (var lang in new[] { currentLang, defaultLang })
                {
                    if (lang == null) continue;
                    var dictFI = lang.GetType().GetField("translations", BindingFlags.Public | BindingFlags.Instance);
                    if (dictFI == null) continue;
                    var dictRaw = dictFI.GetValue(lang);
                    if (!(dictRaw is Dictionary<string, string>)) continue;
                    var dict = (Dictionary<string, string>)dictRaw;
                    if (!string.IsNullOrEmpty(name))         dict[id] = name;
                    if (!string.IsNullOrEmpty(description))  dict[id + "_Description"] = description;
                    if (!string.IsNullOrEmpty(capabilities)) dict[id + FacilityBaseDescriptor.CapabilitiesString] = capabilities;
                }

                le?.InvokeTranslationChanged();
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[FacilityCreator] Translation injection failed for {id}: {ex.Message}");
            }
        }

        internal static object GetFieldValueByNames(object target, Type type, string[] candidates)
        {
            foreach (var candidate in candidates)
            {
                var fi = type.GetField(candidate, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                if (fi != null) return fi.GetValue(target);
            }
            return null;
        }

        static FacilityBaseDescriptor.EPossiblePlacement ParsePlacement(string s)
        {
            if (s == "Both" || s == "Surface|Orbit" || s == "Orbit|Surface")
                return FacilityBaseDescriptor.EPossiblePlacement.Surface | FacilityBaseDescriptor.EPossiblePlacement.Orbit;
            FacilityBaseDescriptor.EPossiblePlacement p;
            if (Enum.TryParse(s, out p)) return p;
            return FacilityBaseDescriptor.EPossiblePlacement.Surface | FacilityBaseDescriptor.EPossiblePlacement.Orbit;
        }

        internal static FieldInfo FindField(Type type, string fieldName)
        {
            while (type != null)
            {
                var fi = type.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                if (fi != null) return fi;
                type = type.BaseType;
            }
            return null;
        }

        internal static void SetField(object obj, string fieldName, object value)
        {
            var fi = FindField(obj.GetType(), fieldName);
            if (fi != null)
                fi.SetValue(obj, value);
            else
                Plugin.Log.LogWarning($"[FacilityCreator] Field not found: {fieldName} on {obj.GetType().Name}");
        }
    }
}

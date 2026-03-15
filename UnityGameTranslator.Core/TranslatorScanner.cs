using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// Handles component scanning for both Mono and IL2CPP runtimes.
    /// Uses a unified RegisteredTextType system to eliminate Mono/IL2CPP code duplication.
    /// </summary>
    public static class TranslatorScanner
    {
        #region IL2CPP Reflection Cache

        private static MethodInfo il2cppTypeOfMethod;
        private static MethodInfo resourcesFindAllMethod;
        private static MethodInfo tryCastMethod;
        private static bool il2cppMethodsInitialized = false;
        private static bool il2cppScanAvailable = false;

        #endregion

        #region Registered Types

        // Unified list of all text component types to scan
        private static readonly List<RegisteredTextType> _registeredTypes = new List<RegisteredTextType>();
        private static bool _typesRegistered = false;

        /// <summary>
        /// Find a component by instance ID across all registered type caches.
        /// Used by FontManager for font restore operations.
        /// </summary>
        public static object FindCachedComponentById(int instanceId)
        {
            foreach (var type in _registeredTypes)
            {
                if (type.CachedComponents == null) continue;
                foreach (var obj in type.CachedComponents)
                {
                    if (obj != null && obj.GetInstanceID() == instanceId)
                        return obj;
                }
            }
            return null;
        }

        /// <summary>
        /// Register a type for scanning. Called by RegisterBuiltInTypes and TranslatorPatches.
        /// </summary>
        public static void RegisterType(RegisteredTextType type)
        {
            if (type == null || type.ComponentType == null) return;

            // Avoid duplicate registration
            foreach (var existing in _registeredTypes)
            {
                if (existing.ComponentType == type.ComponentType)
                    return;
            }

            // Set up IL2CPP cache for this type if available
            if (il2cppScanAvailable && il2cppTypeOfMethod != null)
            {
                try
                {
                    type.IL2CPPType = il2cppTypeOfMethod.MakeGenericMethod(type.ComponentType).Invoke(null, null);
                }
                catch { }

                if (tryCastMethod != null)
                {
                    try
                    {
                        type.TryCastMethod = tryCastMethod.MakeGenericMethod(type.ComponentType);
                    }
                    catch { }
                }
            }

            _registeredTypes.Add(type);
            TranslatorCore.LogInfo($"[Scanner] Registered type: {type.Name} (Category={type.Category})");
        }

        #endregion

        #region Batch Processing

        private const int BATCH_SIZE = 200; // Process 200 components per scan cycle
        private static bool scanCycleComplete = true;

        #endregion

        #region Component Cache

        private static float lastComponentCacheTime = 0f;
        private const float COMPONENT_CACHE_DURATION = 2f;
        private static bool cacheRefreshPending = false;

        #endregion

        #region Quick Skip Cache

        // Track objects that have been processed and haven't changed
        // Key: instanceId, Value: last processed text hash
        private static Dictionary<int, int> processedTextHashes = new Dictionary<int, int>();

        // Track InputField text components (user input, not placeholder) - never translate these
        private static HashSet<int> inputFieldTextIds = new HashSet<int>();

        // Track original text per component (before translation was applied)
        // Key: component InstanceID, Value: original text before translation
        // Used to restore originals when translations are disabled at runtime
        private static Dictionary<int, string> componentOriginals = new Dictionary<int, string>();

        #endregion

        #region Pending Updates Queue (thread-safe)

        // Queue for translations completed by worker thread, to be applied on main thread
        private static readonly object pendingUpdatesLock = new object();
        private static Queue<PendingUpdate> pendingUpdates = new Queue<PendingUpdate>();

        private struct PendingUpdate
        {
            public string OriginalText;
            public string Translation;
            public List<object> Components;
        }

        #endregion

        /// <summary>
        /// Reset caches on scene change.
        /// </summary>
        public static void OnSceneChange()
        {
            lastComponentCacheTime = 0f;
            scanCycleComplete = true;
            cacheRefreshPending = false;
            processedTextHashes.Clear();
            inputFieldTextIds.Clear();
            componentOriginals.Clear();

            // Clear all per-type caches
            foreach (var type in _registeredTypes)
            {
                type.CachedComponents = null;
                type.BatchIndex = 0;
                type.LoggedOnce = false;
            }

            // Clear pending updates (components from old scene are invalid)
            lock (pendingUpdatesLock)
            {
                pendingUpdates.Clear();
            }
        }

        /// <summary>
        /// Force refresh of component caches.
        /// Use before RefreshForFont to ensure all components are captured.
        /// </summary>
        public static void ForceRefreshCache()
        {
            try
            {
                if (!_typesRegistered) RegisterBuiltInTypes();

                int total = 0;
                foreach (var type in _registeredTypes)
                {
                    type.CachedComponents = RefreshTypeCache(type);
                    total += type.CachedComponents?.Length ?? 0;
                }

                lastComponentCacheTime = Time.time;
                TranslatorCore.LogInfo($"[Scanner] Force refreshed cache: {total} total components across {_registeredTypes.Count} types");
            }
            catch (Exception ex)
            {
                TranslatorCore.LogWarning($"[Scanner] ForceRefreshCache error: {ex.Message}");
            }
        }

        /// <summary>
        /// Clear the processed text cache. Call when settings change to force re-evaluation.
        /// </summary>
        public static void ClearProcessedCache()
        {
            processedTextHashes.Clear();
            // Reset batch indices to start fresh
            foreach (var type in _registeredTypes)
            {
                type.BatchIndex = 0;
            }
            scanCycleComplete = true;
        }

        #region Unified Scan

        /// <summary>
        /// Unified scan method - replaces ScanMono() and ScanIL2CPP().
        /// Works for both Mono and IL2CPP runtimes.
        /// </summary>
        public static void Scan()
        {
            // Apply any pending translations from AI (main thread) - always do this
            ProcessPendingUpdates();

            // Register types on first invocation (after TypeHelper.Initialize() + InitializeIL2CPP())
            if (!_typesRegistered) RegisterBuiltInTypes();

            // Skip scanning if there's no useful work to do
            if (ShouldSkipScanning())
                return;

            float currentTime = Time.realtimeSinceStartup;

            // Check if cache refresh is needed
            bool needsRefresh = (currentTime - lastComponentCacheTime > COMPONENT_CACHE_DURATION);

            // Some types may have null caches on first run
            if (!needsRefresh)
            {
                foreach (var type in _registeredTypes)
                {
                    if (type.CachedComponents == null)
                    {
                        needsRefresh = true;
                        break;
                    }
                }
            }

            if (needsRefresh)
            {
                if (scanCycleComplete)
                {
                    RefreshAllCaches();
                    lastComponentCacheTime = currentTime;
                    cacheRefreshPending = false;
                }
                else
                {
                    cacheRefreshPending = true;
                }
            }

            if (cacheRefreshPending && scanCycleComplete)
            {
                RefreshAllCaches();
                lastComponentCacheTime = currentTime;
                cacheRefreshPending = false;
            }

            try
            {
                bool allDone = true;
                foreach (var type in _registeredTypes)
                {
                    if (type.CachedComponents == null || type.CachedComponents.Length == 0) continue;
                    bool typeDone = ProcessBatch(type);
                    if (!typeDone) allDone = false;
                }
                scanCycleComplete = allDone;
            }
            catch { }
        }

        /// <summary>
        /// Refresh caches for all registered types.
        /// </summary>
        private static void RefreshAllCaches()
        {
            foreach (var type in _registeredTypes)
            {
                type.CachedComponents = RefreshTypeCache(type);
                if (!type.LoggedOnce && type.CachedComponents != null && type.CachedComponents.Length > 0)
                {
                    TranslatorCore.LogInfo($"Scan: Found {type.CachedComponents.Length} {type.Name} components");
                    type.LoggedOnce = true;
                }
            }
        }

        /// <summary>
        /// Process a batch of components for a registered type.
        /// Returns true when the full array has been processed (cycle complete for this type).
        /// </summary>
        private static bool ProcessBatch(RegisteredTextType type)
        {
            if (type.BatchIndex >= type.CachedComponents.Length)
                type.BatchIndex = 0;

            int endIndex = Math.Min(type.BatchIndex + BATCH_SIZE, type.CachedComponents.Length);

            for (int i = type.BatchIndex; i < endIndex; i++)
            {
                var obj = type.CachedComponents[i];
                if (obj == null) continue;

                // For IL2CPP native scan results, need TryCast
                if (type.TryCastMethod != null && TranslatorCore.Adapter?.IsIL2CPP == true)
                {
                    // Check if already the right type
                    if (!type.ComponentType.IsInstanceOfType(obj))
                    {
                        var cast = TryCastToType(obj, type.TryCastMethod);
                        if (cast != null)
                        {
                            ProcessComponentForType(cast, type);
                        }
                        continue;
                    }
                }

                ProcessComponentForType(obj, type);
            }

            if (endIndex >= type.CachedComponents.Length)
            {
                type.BatchIndex = 0;
                return true;
            }
            else
            {
                type.BatchIndex = endIndex;
                return false;
            }
        }

        #endregion

        #region Type Registration

        /// <summary>
        /// Register built-in text types (TMP, UI.Text, TextMesh) and generic types from TranslatorPatches.
        /// Called on first Scan() invocation, after TypeHelper.Initialize() and InitializeIL2CPP().
        /// </summary>
        private static void RegisterBuiltInTypes()
        {
            _typesRegistered = true;

            // Ensure IL2CPP is initialized for IL2CPP runtimes
            if (TranslatorCore.Adapter?.IsIL2CPP == true && !il2cppMethodsInitialized)
                InitializeIL2CPP();

            var pubInst = BindingFlags.Public | BindingFlags.Instance;

            // TMP_Text (base type)
            if (TypeHelper.TMP_TextType != null)
            {
                RegisterType(new RegisteredTextType
                {
                    Name = TypeHelper.TMP_TextType.Name,
                    Category = "TMP",
                    ComponentType = TypeHelper.TMP_TextType,
                    TextProp = TypeHelper.TMP_TextProp,
                    FontProp = TypeHelper.TMP_FontProp,
                    FontSizeProp = TypeHelper.TMP_FontSizeProp,
                    ColorProp = TypeHelper.TMP_TextType.GetProperty("color", pubInst),
                    FontTypeName = "TMP",
                    NeedsForceMeshUpdate = true,
                    NeedsSetAllDirty = false
                });

                // TextMeshProUGUI - register only if different from TMP_TextType
                Type tmproUGUI = FindType("TMPro.TextMeshProUGUI");
                if (tmproUGUI != null && tmproUGUI != TypeHelper.TMP_TextType)
                {
                    RegisterType(new RegisteredTextType
                    {
                        Name = "TextMeshProUGUI",
                        Category = "TMP",
                        ComponentType = tmproUGUI,
                        TextProp = tmproUGUI.GetProperty("text", pubInst),
                        FontProp = tmproUGUI.GetProperty("font", pubInst),
                        FontSizeProp = tmproUGUI.GetProperty("fontSize", pubInst),
                        ColorProp = tmproUGUI.GetProperty("color", pubInst),
                        FontTypeName = "TMP",
                        NeedsForceMeshUpdate = true,
                        NeedsSetAllDirty = false
                    });
                }

                // TextMeshPro (3D) - register only if different from TMP_TextType
                Type tmproPro = FindType("TMPro.TextMeshPro");
                if (tmproPro != null && tmproPro != TypeHelper.TMP_TextType && tmproPro != tmproUGUI)
                {
                    RegisterType(new RegisteredTextType
                    {
                        Name = "TextMeshPro",
                        Category = "TMP",
                        ComponentType = tmproPro,
                        TextProp = tmproPro.GetProperty("text", pubInst),
                        FontProp = tmproPro.GetProperty("font", pubInst),
                        FontSizeProp = tmproPro.GetProperty("fontSize", pubInst),
                        ColorProp = tmproPro.GetProperty("color", pubInst),
                        FontTypeName = "TMP",
                        NeedsForceMeshUpdate = true,
                        NeedsSetAllDirty = false
                    });
                }
            }

            // UI.Text
            if (TypeHelper.UI_TextType != null)
            {
                RegisterType(new RegisteredTextType
                {
                    Name = "UI.Text",
                    Category = "Unity",
                    ComponentType = TypeHelper.UI_TextType,
                    TextProp = TypeHelper.UI_TextProp,
                    FontProp = TypeHelper.UI_FontProp,
                    FontSizeProp = TypeHelper.UI_FontSizeProp,
                    ColorProp = TypeHelper.UI_TextType.GetProperty("color", pubInst),
                    FontTypeName = "Unity",
                    NeedsForceMeshUpdate = false,
                    NeedsSetAllDirty = true
                });
            }

            // TextMesh (legacy 3D text)
            if (TypeHelper.TextMeshType != null)
            {
                RegisterType(new RegisteredTextType
                {
                    Name = "TextMesh",
                    Category = "TextMesh",
                    ComponentType = TypeHelper.TextMeshType,
                    TextProp = TypeHelper.TextMesh_TextProp,
                    FontProp = TypeHelper.TextMesh_FontProp,
                    FontSizeProp = null, // TextMesh uses characterSize, not fontSize
                    ColorProp = TypeHelper.TextMeshType.GetProperty("color", pubInst),
                    FontTypeName = "TextMesh",
                    NeedsForceMeshUpdate = false,
                    NeedsSetAllDirty = false
                });
            }

            // Alternate TMP types (TMProOld, etc.) - only if not already covered
            RegisterAlternateTMPTypes();

            // Register generic types already detected by TranslatorPatches
            foreach (var genericType in TranslatorPatches.GenericTextTypes)
            {
                RegisterType(genericType);
            }

            TranslatorCore.LogInfo($"[Scanner] Registered {_registeredTypes.Count} text component types");
        }

        /// <summary>
        /// Register alternate TMP implementations from different namespaces (TMProOld, etc.)
        /// </summary>
        private static void RegisterAlternateTMPTypes()
        {
            if (TypeHelper.UseAlternateTMP) return; // Already handled by TMP_TextType

            string[] altNamespaces = { "TMProOld", "TextMeshPro", "TMPro.Old" };
            string[] tmpTypeNames = { "TMP_Text", "TextMeshPro", "TextMeshProUGUI" };
            var pubInst = BindingFlags.Public | BindingFlags.Instance;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    string asmName = asm.GetName().Name;
                    if (asmName.StartsWith("System") || asmName.StartsWith("mscorlib")) continue;

                    foreach (var type in asm.GetTypes())
                    {
                        try
                        {
                            string typeName = type.Name;
                            string typeNamespace = type.Namespace ?? "";

                            // Skip standard TMPro types (already registered above)
                            if (TypeHelper.TMP_TextType != null && (type == TypeHelper.TMP_TextType || type.IsSubclassOf(TypeHelper.TMP_TextType)))
                                continue;

                            // Check if it's a TMP-like type name
                            bool isTmpType = false;
                            foreach (var name in tmpTypeNames)
                            {
                                if (typeName == name || typeName.EndsWith(name)) { isTmpType = true; break; }
                            }
                            if (!isTmpType) continue;

                            // Must be in an alternate namespace
                            if (typeNamespace == "TMPro") continue;
                            bool isAlt = false;
                            foreach (var ns in altNamespaces)
                            {
                                if (typeNamespace.Contains(ns)) { isAlt = true; break; }
                            }
                            if (!isAlt && typeNamespace == "TMPro") continue;

                            // Must have text property and be a Component
                            var textProp = type.GetProperty("text", pubInst);
                            if (textProp?.SetMethod == null) continue;
                            if (!typeof(Component).IsAssignableFrom(type)) continue;

                            // Already registered?
                            bool alreadyRegistered = false;
                            foreach (var existing in _registeredTypes)
                            {
                                if (existing.ComponentType == type) { alreadyRegistered = true; break; }
                            }
                            if (alreadyRegistered) continue;

                            RegisterType(new RegisteredTextType
                            {
                                Name = $"{typeNamespace}.{typeName}",
                                Category = "TMP",
                                ComponentType = type,
                                TextProp = textProp,
                                FontProp = type.GetProperty("font", pubInst),
                                FontSizeProp = type.GetProperty("fontSize", pubInst),
                                ColorProp = type.GetProperty("color", pubInst),
                                FontTypeName = "TMP (alt)",
                                NeedsForceMeshUpdate = true,
                                NeedsSetAllDirty = false
                            });
                            TranslatorCore.LogInfo($"[Scanner] Registered alternate TMP type: {type.FullName}");
                        }
                        catch { }
                    }
                }
                catch { }
            }
        }

        /// <summary>
        /// Find a type by full name across all loaded assemblies.
        /// Handles IL2CPP prefixed names (e.g., Il2CppTMPro.TextMeshProUGUI).
        /// </summary>
        private static Type FindType(string fullName)
        {
            // Extract class name for IL2CPP prefix search
            string className = fullName.Contains(".") ? fullName.Substring(fullName.LastIndexOf('.') + 1) : fullName;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    // Try exact name first
                    var type = asm.GetType(fullName);
                    if (type != null) return type;

                    // On IL2CPP, types may have Il2Cpp prefix on namespace
                    // e.g., TMPro.TextMeshProUGUI -> Il2CppTMPro.TextMeshProUGUI
                    if (fullName.Contains("."))
                    {
                        string il2cppName = "Il2Cpp" + fullName;
                        type = asm.GetType(il2cppName);
                        if (type != null) return type;
                    }
                }
                catch { }
            }

            // Last resort: scan all types by class name (handles any namespace prefix)
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (var type in asm.GetTypes())
                    {
                        if (type.Name == className && !type.IsAbstract)
                            return type;
                    }
                }
                catch { }
            }
            return null;
        }

        #endregion

        #region Cache Refresh

        /// <summary>
        /// Refresh the component cache for a registered type.
        /// Tries multiple strategies and merges results by instance ID to avoid duplicates.
        /// </summary>
        private static UnityEngine.Object[] RefreshTypeCache(RegisteredTextType type)
        {
            var seenIds = new HashSet<int>();
            var results = new List<UnityEngine.Object>();

            // Strategy 1: IL2CPP native scan
            TryAddFromIL2CPPNative(type, results, seenIds);

            // Strategy 2: TypeHelper (works for both Mono and IL2CPP)
            TryAddFromTypeHelper(type, results, seenIds);

            // Strategy 3: Static list fields (for NGUI etc.)
            TryAddFromStaticLists(type, results, seenIds);

            // Strategy 4: MonoBehaviour filter fallback
            // Used when all other strategies fail (common on IL2CPP for TMP and third-party types)
            if (results.Count == 0)
            {
                TryAddFromMonoBehaviourFilter(type, results, seenIds);
            }

            return results.Count > 0 ? results.ToArray() : null;
        }

        private static void TryAddFromIL2CPPNative(RegisteredTextType type, List<UnityEngine.Object> results, HashSet<int> seenIds)
        {
            if (!il2cppScanAvailable || type.IL2CPPType == null) return;

            try
            {
                var found = FindAllComponentsIL2CPPCached(type.IL2CPPType);
                if (found == null) return;
                foreach (var obj in found)
                {
                    if (obj == null) continue;
                    int id = obj.GetInstanceID();
                    if (seenIds.Add(id))
                        results.Add(obj);
                }
            }
            catch { }
        }

        private static void TryAddFromTypeHelper(RegisteredTextType type, List<UnityEngine.Object> results, HashSet<int> seenIds)
        {
            try
            {
                var found = TypeHelper.FindAllObjectsOfType(type.ComponentType);
                if (found == null) return;
                foreach (var obj in found)
                {
                    if (obj == null) continue;
                    int id = obj.GetInstanceID();
                    if (seenIds.Add(id))
                        results.Add(obj);
                }
            }
            catch { }
        }

        private static void TryAddFromStaticLists(RegisteredTextType type, List<UnityEngine.Object> results, HashSet<int> seenIds)
        {
            try
            {
                var componentType = type.ComponentType;
                var staticFields = componentType.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

                string[] listFieldNames = { "mList", "sList", "s_Instances", "instances", "allInstances", "s_list" };

                foreach (var field in staticFields)
                {
                    var fieldType = field.FieldType;
                    if (fieldType == null) continue;

                    bool nameMatch = false;
                    foreach (var knownName in listFieldNames)
                    {
                        if (string.Equals(field.Name, knownName, StringComparison.OrdinalIgnoreCase))
                        {
                            nameMatch = true;
                            break;
                        }
                    }

                    if (!nameMatch)
                    {
                        string ftName = fieldType.Name ?? "";
                        if (!ftName.Contains("List") && !ftName.Contains("Array") &&
                            !ftName.Contains("Collection") && !ftName.Contains("HashSet"))
                            continue;
                    }

                    object listObj = null;
                    try { listObj = field.GetValue(null); }
                    catch { continue; }
                    if (listObj == null) continue;

                    var items = ExtractObjectsFromList(listObj, componentType);
                    if (items == null) continue;

                    foreach (var obj in items)
                    {
                        if (obj == null) continue;
                        int id = obj.GetInstanceID();
                        if (seenIds.Add(id))
                            results.Add(obj);
                    }
                }
            }
            catch { }
        }

        private static void TryAddFromMonoBehaviourFilter(RegisteredTextType type, List<UnityEngine.Object> results, HashSet<int> seenIds)
        {
            try
            {
                // On IL2CPP, IsInstanceOfType may fail for proxy types.
                // Use type name matching as fallback: check if the runtime type name
                // matches or inherits from the target type name.
                string targetName = type.ComponentType.Name;
                // Strip Il2Cpp prefix for matching
                if (targetName.StartsWith("Il2Cpp")) targetName = targetName.Substring(6);

                var allComponents = TypeHelper.FindAllObjectsOfType(typeof(Component));
                if (allComponents == null) return;

                foreach (var obj in allComponents)
                {
                    if (obj == null) continue;

                    // Try IsInstanceOfType first (works on Mono)
                    if (type.ComponentType.IsInstanceOfType(obj))
                    {
                        int id = obj.GetInstanceID();
                        if (seenIds.Add(id))
                            results.Add(obj);
                        continue;
                    }

                    // Fallback: match by type name (handles IL2CPP proxy types)
                    var objType = obj.GetType();
                    var current = objType;
                    while (current != null)
                    {
                        string name = current.Name;
                        if (name.StartsWith("Il2Cpp")) name = name.Substring(6);
                        if (name == targetName)
                        {
                            int id = obj.GetInstanceID();
                            if (seenIds.Add(id))
                                results.Add(obj);
                            break;
                        }
                        current = current.BaseType;
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// Extract UnityEngine.Object instances from a list/collection object.
        /// Handles BetterList (NGUI), List, arrays, etc.
        /// </summary>
        private static UnityEngine.Object[] ExtractObjectsFromList(object listObj, Type expectedType)
        {
            try
            {
                var results = new List<UnityEngine.Object>();

                // Try 'buffer' field + 'size' field (BetterList pattern from NGUI)
                var bufferField = listObj.GetType().GetField("buffer", BindingFlags.Public | BindingFlags.Instance);
                var sizeField = listObj.GetType().GetField("size", BindingFlags.Public | BindingFlags.Instance);
                if (bufferField != null && sizeField != null)
                {
                    var buffer = bufferField.GetValue(listObj);
                    var size = sizeField.GetValue(listObj);
                    if (buffer is System.Collections.IEnumerable enumerable && size is int count)
                    {
                        int idx = 0;
                        foreach (var item in enumerable)
                        {
                            if (idx >= count) break;
                            if (item is UnityEngine.Object uobj)
                                results.Add(uobj);
                            idx++;
                        }
                        if (results.Count > 0) return results.ToArray();
                    }
                }

                // Try IEnumerable directly (List<T>, etc.)
                if (listObj is System.Collections.IEnumerable directEnum)
                {
                    foreach (var item in directEnum)
                    {
                        if (item is UnityEngine.Object uobj)
                            results.Add(uobj);
                    }
                    if (results.Count > 0) return results.ToArray();
                }

                // Try Count + Item indexer pattern
                var countProp = listObj.GetType().GetProperty("Count");
                var itemProp = listObj.GetType().GetProperty("Item");
                if (countProp != null && itemProp != null)
                {
                    int count2 = (int)countProp.GetValue(listObj, null);
                    for (int i = 0; i < count2; i++)
                    {
                        var item = itemProp.GetValue(listObj, new object[] { i });
                        if (item is UnityEngine.Object uobj)
                            results.Add(uobj);
                    }
                    if (results.Count > 0) return results.ToArray();
                }
            }
            catch { }

            return null;
        }

        #endregion

        #region Component Processing

        /// <summary>
        /// Get text from a component using the appropriate method for its type.
        /// Built-in types use TypeHelper (handles IL2CPP casting); generic types use direct property access.
        /// </summary>
        private static string GetTextForType(object component, RegisteredTextType type)
        {
            if (type.Category == "TMP" || type.Category == "Unity" || type.Category == "TextMesh")
                return TypeHelper.GetText(component);
            // Generic types use direct property access
            return type.TextProp?.GetValue(component, null) as string;
        }

        /// <summary>
        /// Set text on a component using the appropriate method for its type.
        /// </summary>
        private static void SetTextForType(object component, RegisteredTextType type, string text)
        {
            if (type.Category == "TMP" || type.Category == "Unity" || type.Category == "TextMesh")
                TypeHelper.SetText(component, text);
            else
                type.TextProp?.SetValue(component, text, null);
        }

        /// <summary>
        /// Get font name from a component, using TypeHelper for built-in types.
        /// </summary>
        private static string GetFontNameForType(object component, RegisteredTextType type)
        {
            if (type.Category == "TMP" || type.Category == "Unity" || type.Category == "TextMesh")
                return TypeHelper.GetFontName(component);

            // Generic types: use font property directly
            if (type.FontProp == null) return null;
            try
            {
                var fontObj = type.FontProp.GetValue(component, null);
                if (fontObj is UnityEngine.Object uobj && !string.IsNullOrEmpty(uobj.name))
                    return uobj.name;
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Process a single component for a registered text type.
        /// </summary>
        private static void ProcessComponentForType(object component, RegisteredTextType type)
        {
            try
            {
                var comp = component as Component;
                if (comp == null) return;

                int instanceId = comp.GetInstanceID();

                // Skip if own UI and should not be translated
                if (TranslatorCore.ShouldSkipTranslation(comp))
                    return;

                // Skip if translation disabled for this font
                string fontName = GetFontNameForType(component, type);
                if (!string.IsNullOrEmpty(fontName) && !FontManager.IsTranslationEnabled(fontName))
                    return;

                // Skip if already identified as InputField user text
                if (inputFieldTextIds.Contains(instanceId))
                    return;

                // First-time check: is this the textComponent of an InputField?
                if (type.Category != "TextMesh" && TypeHelper.IsInputFieldTextComponent(component))
                {
                    inputFieldTextIds.Add(instanceId);
                    TranslatorCore.LogInfo($"[Scanner] Excluded InputField textComponent: {comp.gameObject.name}");
                    return;
                }

                string currentText = GetTextForType(component, type);

                if (string.IsNullOrEmpty(currentText) || currentText.Length < 2) return;

                int textHash = currentText.GetHashCode();

                // Quick skip: already processed with same text
                if (processedTextHashes.TryGetValue(instanceId, out int lastHash) && lastHash == textHash)
                    return;

                // Check if text changed since last seen
                if (TranslatorCore.HasSeenText(instanceId, currentText, out _))
                {
                    processedTextHashes[instanceId] = textHash;
                    return;
                }

                // Check if own UI (use UI-specific prompt)
                bool isOwnUI = TranslatorCore.IsOwnUITranslatable(comp);
                string translated = TranslatorCore.TranslateTextWithTracking(currentText, comp, isOwnUI);
                if (translated != currentText)
                {
                    SetTextForType(component, type, translated);

                    // Force refresh based on type flags
                    if (type.NeedsForceMeshUpdate)
                        TypeHelper.ForceMeshUpdate(component);
                    else if (type.NeedsSetAllDirty)
                        TypeHelper.SetAllDirty(component);

                    TranslatorCore.UpdateSeenText(instanceId, translated);
                    processedTextHashes[instanceId] = translated.GetHashCode();
                }
                else
                {
                    TranslatorCore.UpdateSeenText(instanceId, currentText);
                    processedTextHashes[instanceId] = textHash;
                }
            }
            catch { }
        }

        #endregion

        #region ForceRefreshAllText

        /// <summary>
        /// Force refresh all text components by re-assigning their text.
        /// If translations are disabled, restores original texts from cache.
        /// This triggers Harmony patches to re-process and apply translations/fonts.
        /// </summary>
        public static void ForceRefreshAllText()
        {
            int refreshed = 0;
            int restored = 0;

            bool globalRestore = !TranslatorCore.Config.enable_translations;

            try
            {
                foreach (var type in _registeredTypes)
                {
                    if (type.CachedComponents == null) continue;
                    foreach (var obj in type.CachedComponents)
                    {
                        if (obj == null) continue;
                        RefreshComponent(obj, type, globalRestore, ref refreshed, ref restored);
                    }
                }

                if (restored > 0)
                    TranslatorCore.LogInfo($"[Scanner] Restored {restored} original texts, refreshed {refreshed} components");
                else
                    TranslatorCore.LogInfo($"[Scanner] Force refreshed {refreshed} text components");
            }
            catch (Exception ex)
            {
                TranslatorCore.LogWarning($"[Scanner] ForceRefreshAllText error: {ex.Message}");
            }
        }

        /// <summary>
        /// Refresh a single component: either restore original or trigger re-translation.
        /// </summary>
        private static void RefreshComponent(UnityEngine.Object obj, RegisteredTextType type, bool globalRestore, ref int refreshed, ref int restored)
        {
            try
            {
                // For IL2CPP native scan results, need TryCast to get the typed component
                object component = obj;
                if (type.TryCastMethod != null && TranslatorCore.Adapter?.IsIL2CPP == true)
                {
                    if (!type.ComponentType.IsInstanceOfType(obj))
                    {
                        component = TryCastToType(obj, type.TryCastMethod);
                        if (component == null) return;
                    }
                }

                int instanceId = TypeHelper.GetInstanceID(component);
                if (instanceId == -1) return;

                // Check per-font translation state
                string compFontName = GetFontNameForType(component, type);
                string origFontName = FontManager.GetOriginalFontName(instanceId);
                bool fontDisabled = (!string.IsNullOrEmpty(origFontName) && !FontManager.IsTranslationEnabled(origFontName))
                    || (!string.IsNullOrEmpty(compFontName) && !FontManager.IsTranslationEnabled(compFontName));
                bool shouldRestore = globalRestore || fontDisabled;

                if (shouldRestore)
                {
                    // Restore original font (for built-in types)
                    if (type.Category == "TMP" || type.Category == "Unity" || type.Category == "TextMesh")
                        FontManager.RestoreOriginalFont(component);

                    string original = GetOriginalText(instanceId);
                    if (original != null)
                    {
                        SetTextForType(component, type, original);
                        ClearOriginalText(instanceId);
                        processedTextHashes.Remove(instanceId);
                        restored++;
                        return;
                    }
                }

                // Normal refresh path (trigger Harmony patch)
                string currentText = GetTextForType(component, type);
                if (!string.IsNullOrEmpty(currentText))
                {
                    if (type.NeedsForceMeshUpdate)
                    {
                        // TMP needs empty-then-restore to force re-render
                        SetTextForType(component, type, "");
                        SetTextForType(component, type, currentText);
                        TypeHelper.ForceMeshUpdate(component);
                    }
                    else
                    {
                        SetTextForType(component, type, currentText);
                    }
                    refreshed++;
                }
            }
            catch { }
        }

        #endregion

        #region Original Text Tracking

        /// <summary>
        /// Store the original (untranslated) text for a component.
        /// Called when translation is applied. Only stores on first translation (preserves true original).
        /// </summary>
        public static void StoreOriginalText(object component, string originalText)
        {
            if (component == null || string.IsNullOrEmpty(originalText)) return;

            int instanceId = GetComponentInstanceId(component);
            if (instanceId == -1) return;

            // Only store if not already stored (first translation wins - preserves true original)
            if (!componentOriginals.ContainsKey(instanceId))
            {
                componentOriginals[instanceId] = originalText;
            }
        }

        /// <summary>
        /// Get the original (untranslated) text for a component.
        /// Returns null if no original stored (component was never translated).
        /// </summary>
        public static string GetOriginalText(object component)
        {
            if (component == null) return null;

            int instanceId = GetComponentInstanceId(component);
            if (instanceId == -1) return null;

            if (componentOriginals.TryGetValue(instanceId, out string original))
                return original;

            return null;
        }

        /// <summary>
        /// Get the original text by instance ID directly.
        /// </summary>
        public static string GetOriginalText(int instanceId)
        {
            if (componentOriginals.TryGetValue(instanceId, out string original))
                return original;
            return null;
        }

        /// <summary>
        /// Clear the stored original for a component (after restoration).
        /// </summary>
        public static void ClearOriginalText(int instanceId)
        {
            componentOriginals.Remove(instanceId);
        }

        /// <summary>
        /// Helper to get InstanceID from various component types.
        /// </summary>
        private static int GetComponentInstanceId(object component)
        {
            return TypeHelper.GetInstanceID(component);
        }

        #endregion

        #region Font Operations (Restore/Refresh/Highlight)

        /// <summary>
        /// Restore originals for components using a specific font.
        /// Call when per-font translation is disabled.
        /// </summary>
        public static void RestoreOriginalsForFont(string fontName, string oldFallback = null)
        {
            if (string.IsNullOrEmpty(fontName)) return;
            int restored = 0;

            try
            {
                var fontNamesToMatch = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { fontName };
                string newFallback = FontManager.GetConfiguredFallback(fontName);
                if (!string.IsNullOrEmpty(newFallback))
                    fontNamesToMatch.Add(newFallback);
                if (!string.IsNullOrEmpty(oldFallback))
                    fontNamesToMatch.Add(oldFallback);

                foreach (var type in _registeredTypes)
                {
                    if (type.CachedComponents == null) continue;
                    foreach (var obj in type.CachedComponents)
                    {
                        if (obj == null) continue;
                        restored += RestoreComponentForFont(obj, type, fontNamesToMatch);
                    }
                }

                if (restored > 0)
                    TranslatorCore.LogInfo($"[Scanner] Restored {restored} originals for font: {fontName}");
            }
            catch (Exception ex)
            {
                TranslatorCore.LogWarning($"[Scanner] RestoreOriginalsForFont error: {ex.Message}");
            }
        }

        private static int RestoreComponentForFont(UnityEngine.Object obj, RegisteredTextType type, HashSet<string> fontNames)
        {
            try
            {
                object component = ResolveComponent(obj, type);
                if (component == null) return 0;

                int id = TypeHelper.GetInstanceID(component);
                if (id == -1) return 0;

                // Match against both current font name AND tracked original font name
                string compFont = GetFontNameForType(component, type);
                string origFont = FontManager.GetOriginalFontName(id);
                bool matches = (!string.IsNullOrEmpty(compFont) && fontNames.Contains(compFont))
                    || (!string.IsNullOrEmpty(origFont) && fontNames.Contains(origFont));
                if (!matches) return 0;

                // Restore font before text
                if (type.Category == "TMP" || type.Category == "Unity" || type.Category == "TextMesh")
                    FontManager.RestoreOriginalFont(component);

                string original = GetOriginalText(id);
                if (original != null)
                {
                    SetTextForType(component, type, original);
                    ClearOriginalText(id);
                    processedTextHashes.Remove(id);
                    return 1;
                }
            }
            catch { }
            return 0;
        }

        /// <summary>
        /// Re-translate components for a specific font.
        /// Call when per-font translation is re-enabled.
        /// </summary>
        public static void RefreshForFont(string fontName, string oldFallback = null)
        {
            if (string.IsNullOrEmpty(fontName)) return;
            int refreshed = 0;

            try
            {
                ForceRefreshCache();

                var fontNamesToMatch = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { fontName };
                string newFallback = FontManager.GetConfiguredFallback(fontName);
                if (!string.IsNullOrEmpty(newFallback))
                    fontNamesToMatch.Add(newFallback);
                if (!string.IsNullOrEmpty(oldFallback))
                    fontNamesToMatch.Add(oldFallback);

                TranslatorCore.LogInfo($"[Scanner] RefreshForFont: matching fonts [{string.Join(", ", fontNamesToMatch)}]");

                foreach (var type in _registeredTypes)
                {
                    if (type.CachedComponents == null) continue;
                    foreach (var obj in type.CachedComponents)
                    {
                        if (obj == null) continue;
                        refreshed += RefreshComponentForFont(obj, type, fontNamesToMatch);
                    }
                }

                TranslatorCore.LogInfo($"[Scanner] Refreshed {refreshed} components for font: {fontName}");
            }
            catch (Exception ex)
            {
                TranslatorCore.LogWarning($"[Scanner] RefreshForFont error: {ex.Message}");
            }
        }

        private static int RefreshComponentForFont(UnityEngine.Object obj, RegisteredTextType type, HashSet<string> fontNames)
        {
            try
            {
                object component = ResolveComponent(obj, type);
                if (component == null) return 0;

                int id = TypeHelper.GetInstanceID(component);
                if (id == -1) return 0;

                string compFont = GetFontNameForType(component, type);
                string origFont = FontManager.GetOriginalFontName(id);
                bool matches = (!string.IsNullOrEmpty(compFont) && fontNames.Contains(compFont))
                    || (!string.IsNullOrEmpty(origFont) && fontNames.Contains(origFont));
                if (!matches) return 0;

                processedTextHashes.Remove(id);

                string currentText = GetTextForType(component, type);
                if (!string.IsNullOrEmpty(currentText))
                {
                    // Set empty then back to force re-render with potentially new font
                    SetTextForType(component, type, "");
                    SetTextForType(component, type, currentText);
                    if (type.NeedsForceMeshUpdate)
                        TypeHelper.ForceMeshUpdate(component);
                    return 1;
                }
            }
            catch { }
            return 0;
        }

        /// <summary>
        /// Resolve a Unity Object to its typed component (handles IL2CPP TryCast).
        /// </summary>
        private static object ResolveComponent(UnityEngine.Object obj, RegisteredTextType type)
        {
            if (type.TryCastMethod != null && TranslatorCore.Adapter?.IsIL2CPP == true)
            {
                if (!type.ComponentType.IsInstanceOfType(obj))
                    return TryCastToType(obj, type.TryCastMethod);
            }
            return obj;
        }

        #endregion

        #region Font Highlight (in-game identification)

        // Stores original colors per instance ID for restore after highlight
        private static readonly Dictionary<int, Color> _highlightOriginalColors = new Dictionary<int, Color>();
        private static string _highlightedFontName = null;

        // Highlight color for matching font, dim color for non-matching
        private static readonly Color HighlightColor = new Color(1f, 0f, 0.8f, 1f); // Magenta
        private static readonly Color DimColor = new Color(1f, 1f, 1f, 0.15f); // Very transparent

        /// <summary>
        /// Highlight all text components using a specific font.
        /// Matching components get a bright color, others get dimmed.
        /// </summary>
        public static void HighlightFont(string fontName)
        {
            if (_highlightedFontName != null)
                ClearHighlight();

            _highlightedFontName = fontName;

            try
            {
                ForceRefreshCache();

                foreach (var type in _registeredTypes)
                {
                    if (type.CachedComponents == null) continue;
                    foreach (var obj in type.CachedComponents)
                    {
                        if (obj == null) continue;
                        try
                        {
                            object component = ResolveComponent(obj, type);
                            if (component == null) continue;
                            int id = TypeHelper.GetInstanceID(component);
                            if (id == -1) continue;
                            HighlightComponent(component, id, fontName);
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                TranslatorCore.LogWarning($"[Scanner] HighlightFont error: {ex.Message}");
            }
        }

        /// <summary>
        /// Clear all font highlights, restoring original colors.
        /// </summary>
        public static void ClearHighlight()
        {
            if (_highlightedFontName == null && _highlightOriginalColors.Count == 0) return;

            try
            {
                foreach (var type in _registeredTypes)
                {
                    if (type.CachedComponents == null) continue;
                    foreach (var obj in type.CachedComponents)
                    {
                        if (obj == null) continue;
                        try
                        {
                            object component = ResolveComponent(obj, type);
                            if (component == null) continue;
                            int id = TypeHelper.GetInstanceID(component);
                            if (id == -1) continue;
                            RestoreComponentColor(component, id);
                        }
                        catch { }
                    }
                }
            }
            catch { }

            _highlightOriginalColors.Clear();
            _highlightedFontName = null;
        }

        private static void HighlightComponent(object component, int id, string targetFontName)
        {
            // Skip our own UI components
            var comp = component as Component;
            if (comp != null && TranslatorCore.ShouldSkipTranslation(comp)) return;

            // Determine if this component's font matches (check both current and original name)
            string compFont = TypeHelper.GetFontName(component);
            string origFont = FontManager.GetOriginalFontName(id);
            bool matches = (!string.IsNullOrEmpty(compFont) && string.Equals(compFont, targetFontName, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrEmpty(origFont) && string.Equals(origFont, targetFontName, StringComparison.OrdinalIgnoreCase));

            // Store original color
            Color originalColor = TypeHelper.GetTextColor(component);
            if (!_highlightOriginalColors.ContainsKey(id))
                _highlightOriginalColors[id] = originalColor;

            // Apply highlight or dim
            TypeHelper.SetTextColor(component, matches ? HighlightColor : DimColor);
        }

        private static void RestoreComponentColor(object component, int id)
        {
            if (_highlightOriginalColors.TryGetValue(id, out var originalColor))
            {
                TypeHelper.SetTextColor(component, originalColor);
            }
        }

        #endregion

        #region Scanning Control

        /// <summary>
        /// Check if scanning should be skipped (no useful work to do).
        /// </summary>
        private static bool ShouldSkipScanning()
        {
            if (!TranslatorCore.Config.enable_translations)
                return true;

            if (TranslatorCore.TranslationCache.Count > 0)
                return false;

            if (TranslatorCore.Config.enable_ai)
                return false;

            if (TranslatorCore.Config.capture_keys_only)
                return false;

            return true;
        }

        #endregion

        #region IL2CPP Initialization

        /// <summary>
        /// Initialize IL2CPP methods via reflection. Call once at startup for IL2CPP games.
        /// </summary>
        public static void InitializeIL2CPP()
        {
            if (il2cppMethodsInitialized) return;
            il2cppMethodsInitialized = true;

            // On IL2CPP, TMP assemblies may be loaded after initial TypeHelper.Initialize()
            TypeHelper.TryResolveIfNeeded();

            try
            {
                // Find Il2CppType.Of<T>()
                var il2cppTypeClass = Type.GetType("Il2CppInterop.Runtime.Il2CppType, Il2CppInterop.Runtime");
                if (il2cppTypeClass != null)
                {
                    foreach (var method in il2cppTypeClass.GetMethods(BindingFlags.Public | BindingFlags.Static))
                    {
                        if (method.Name == "Of" && method.IsGenericMethodDefinition && method.GetParameters().Length == 0)
                        {
                            il2cppTypeOfMethod = method;
                            TranslatorCore.LogInfo("Found Il2CppType.Of<T>() method");
                            break;
                        }
                    }
                }

                // Find Resources.FindObjectsOfTypeAll(Il2CppSystem.Type)
                var resourcesType = typeof(Resources);
                foreach (var method in resourcesType.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (method.Name == "FindObjectsOfTypeAll" && method.GetParameters().Length == 1)
                    {
                        var paramType = method.GetParameters()[0].ParameterType;
                        if (paramType.FullName?.Contains("Il2Cpp") == true)
                        {
                            resourcesFindAllMethod = method;
                            TranslatorCore.LogInfo($"Found Resources.FindObjectsOfTypeAll({paramType.Name})");
                            break;
                        }
                    }
                }

                // Find TryCast - try static IL2CPP class first
                var il2cppClass = Type.GetType("Il2CppInterop.Runtime.IL2CPP, Il2CppInterop.Runtime");
                if (il2cppClass != null)
                {
                    foreach (var method in il2cppClass.GetMethods(BindingFlags.Public | BindingFlags.Static))
                    {
                        if (method.Name == "TryCast" && method.IsGenericMethodDefinition)
                        {
                            tryCastMethod = method;
                            TranslatorCore.LogInfo("Found IL2CPP.TryCast<T>() method");
                            break;
                        }
                    }
                }

                // Fallback: Il2CppObjectBase instance method
                if (tryCastMethod == null)
                {
                    var il2cppObjectBase = Type.GetType("Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase, Il2CppInterop.Runtime");
                    if (il2cppObjectBase != null)
                    {
                        foreach (var method in il2cppObjectBase.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                        {
                            if (method.Name == "TryCast" && method.IsGenericMethodDefinition)
                            {
                                tryCastMethod = method;
                                TranslatorCore.LogInfo("Found Il2CppObjectBase.TryCast<T>() method");
                                break;
                            }
                        }
                    }
                }

                il2cppScanAvailable = il2cppTypeOfMethod != null && resourcesFindAllMethod != null;

                // Share IL2CPP methods with TypeHelper for use by other components
                if (il2cppScanAvailable)
                    TypeHelper.SetIL2CPPMethods(il2cppTypeOfMethod, resourcesFindAllMethod,
                        tryCastMethod, tryCastMethod != null && tryCastMethod.IsStatic);

                if (il2cppScanAvailable)
                    TranslatorCore.LogInfo($"IL2CPP scan initialized (TryCast={tryCastMethod != null})");
                else
                    TranslatorCore.LogWarning($"IL2CPP scan not available");
            }
            catch (Exception e)
            {
                TranslatorCore.LogError($"Failed to initialize IL2CPP methods: {e.Message}");
            }
        }

        /// <summary>
        /// Check if IL2CPP scanning is available.
        /// </summary>
        public static bool IsIL2CPPScanAvailable => il2cppScanAvailable;

        #endregion

        #region Translation Callback

        /// <summary>
        /// Callback for when async translation completes. Queues update for main thread.
        /// Called from worker thread - must NOT access Unity objects directly!
        /// </summary>
        public static void OnTranslationComplete(string originalText, string translation, List<object> components)
        {
            if (components == null || components.Count == 0) return;

            lock (pendingUpdatesLock)
            {
                pendingUpdates.Enqueue(new PendingUpdate
                {
                    OriginalText = originalText,
                    Translation = translation,
                    Components = components
                });
            }
        }

        /// <summary>
        /// Process pending translation updates on the main thread.
        /// Call this from Update() or scan methods.
        /// </summary>
        public static void ProcessPendingUpdates()
        {
            // Check if a font was just created - clear processed cache
            if (FontManager.ConsumePendingRefresh())
            {
                ClearProcessedCache();
            }

            // Process all pending updates immediately
            while (true)
            {
                PendingUpdate update;
                lock (pendingUpdatesLock)
                {
                    if (pendingUpdates.Count == 0) break;
                    update = pendingUpdates.Dequeue();
                }

                ApplyTranslationToComponents(update.OriginalText, update.Translation, update.Components);
            }
        }

        private static void ApplyTranslationToComponents(string originalText, string translation, List<object> components)
        {
            foreach (var comp in components)
            {
                try
                {
                    string actualText = TypeHelper.GetText(comp);
                    if (actualText == null) continue;

                    string expectedPreview = originalText.Length > 40 ? originalText.Substring(0, 40) + "..." : originalText;
                    string actualPreview = actualText.Length > 40 ? actualText.Substring(0, 40) + "..." : actualText;

                    if (actualText == originalText)
                    {
                        // Store original before applying translation (enables runtime toggle restoration)
                        StoreOriginalText(comp, originalText);

                        TypeHelper.SetText(comp, translation);

                        // Force visual refresh by toggling enabled state
                        TypeHelper.ToggleEnabled(comp);

                        int id = TypeHelper.GetInstanceID(comp);
                        if (id != -1)
                        {
                            TranslatorCore.UpdateSeenText(id, translation);
                            processedTextHashes[id] = translation.GetHashCode();
                        }
                        TranslatorCore.LogInfo($"[Apply OK] {expectedPreview}");
                    }
                    else
                    {
                        // Clear tracking so scanner retries this component on next cycle
                        int skipId = TypeHelper.GetInstanceID(comp);
                        if (skipId != -1)
                        {
                            processedTextHashes.Remove(skipId);
                            TranslatorCore.ClearSeenText(skipId);
                        }
                        TranslatorCore.LogWarning($"[Apply SKIP] expected='{expectedPreview}' actual='{actualPreview}'");
                    }
                }
                catch { }
            }
        }

        #endregion

        #region IL2CPP Helpers

        /// <summary>
        /// Try to cast an IL2CPP object to a specific type using a cached TryCast method.
        /// </summary>
        private static object TryCastToType(object obj, MethodInfo typedTryCastMethod)
        {
            if (obj == null || typedTryCastMethod == null) return null;

            try
            {
                if (tryCastMethod != null && tryCastMethod.IsStatic)
                    return typedTryCastMethod.Invoke(null, new[] { obj });
                else
                    return typedTryCastMethod.Invoke(obj, null);
            }
            catch { }

            return null;
        }

        private static UnityEngine.Object[] FindAllComponentsIL2CPPCached(object il2cppType)
        {
            if (!il2cppScanAvailable || il2cppType == null) return null;

            try
            {
                var result = resourcesFindAllMethod.Invoke(null, new[] { il2cppType });
                if (result == null) return null;

                var asArray = result as UnityEngine.Object[];
                if (asArray == null)
                {
                    var enumerable = result as System.Collections.IEnumerable;
                    if (enumerable != null)
                    {
                        var list = new List<UnityEngine.Object>();
                        foreach (var item in enumerable)
                        {
                            if (item is UnityEngine.Object uobj)
                                list.Add(uobj);
                        }
                        return list.ToArray();
                    }
                    return null;
                }

                return asArray;
            }
            catch
            {
                return null;
            }
        }

        #endregion

        #region Legacy API (backward compatibility)

        /// <summary>
        /// Legacy method - now delegates to Scan().
        /// Kept for backward compatibility during migration.
        /// </summary>
        public static void ScanMono()
        {
            Scan();
        }

        /// <summary>
        /// Legacy method - now delegates to Scan().
        /// Kept for backward compatibility during migration.
        /// </summary>
        public static void ScanIL2CPP()
        {
            if (!il2cppMethodsInitialized) InitializeIL2CPP();
            Scan();
        }

        #endregion
    }
}

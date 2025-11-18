using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.NET.Common;
using BepisResoniteWrapper;
using FrooxEngine;
using HarmonyLib;
using InterprocessLib;
using ResoniteSpout.Shared;

namespace ResoniteSpout.Engine;

[BepInExResoniteShim.ResonitePlugin(PluginMetadata.GUID, PluginMetadata.NAME, PluginMetadata.VERSION, PluginMetadata.AUTHORS, PluginMetadata.REPOSITORY_URL)]
[BepInDependency(BepInExResoniteShim.PluginMetadata.GUID, BepInDependency.DependencyFlags.HardDependency)]
public class ResoniteSpout : BasePlugin
{
    internal static new ManualLogSource Log = null!;
    private static ConfigFile config;
    
    public static Dictionary<string, int> spoutCameras = new();
    
    public static Messenger? _messenger;
    public static ConfigEntry<string> ConfigSpacePrefix;
    private static string TargetVariableSpaceName => ConfigSpacePrefix.Value;

    public override void Load()
    {
        Log = base.Log;
        config = Config;

        ConfigSpacePrefix = config.Bind(
            "General",
            "SpacePrefixes", 
            "",
            "Comma-separated list of suffixes to monitor.\n" +
            "Empty: Monitor all 'ResoniteSpout.*'\n" +
            "Example: 'Zozokasu, kokoa, aetoriz' → Monitor 'ResoniteSpout.Zozokasu', 'ResoniteSpout.kokoa', and 'ResoniteSpout.aetoriz'"
        );
        
        ResoniteHooks.OnEngineReady += OnEngineReady;
        Log.LogInfo($"Plugin {PluginMetadata.GUID} loaded.");
        
        _messenger = new Messenger("Zozokasu.ResoniteSpout", [typeof(SpoutCommand)]);
    }

    private void OnEngineReady()
    {
        try
        {
            Harmony harmony = new Harmony("Zozokasu.ResoniteSpout.Engine");
            harmony.PatchAll();
            Log.LogInfo("Harmony patches installed.");
            var patches = Harmony.GetAllPatchedMethods();
            Log.LogInfo("=== Patched Methods ===");
            foreach (var method in patches)
            {
                Log.LogInfo($"Patched: {method.DeclaringType?.FullName}.{method.Name}");
                var patchInfo = Harmony.GetPatchInfo(method);
                if (patchInfo != null)
                {
                    Log.LogInfo($"  Prefixes: {patchInfo.Prefixes.Count}");
                    Log.LogInfo($"  Postfixes: {patchInfo.Postfixes.Count}");
                }
            }
            Log.LogInfo("=== End of Patched Methods ===");
        
            Log.LogInfo("Harmony patches installed.");
        }
        catch (Exception ex)
        {
            Log.LogError($"Failed to install patches: {ex}");
        }
    }
    
    [HarmonyPatch]
public static class DynamicVariableSpacePatch
{
    // ★ DynamicVariableSpace のインスタンス → SpaceMonitor のマッピング
    private static Dictionary<DynamicVariableSpace, SpaceMonitor> _monitors = new Dictionary<DynamicVariableSpace, SpaceMonitor>();
    
    private class SpoutCameraInfo
    {
        public string SpoutName;
        public int AssetId;
        public string Suffix;
        public string CameraName;
        public DynamicVariableSpace Space;  // ★ どの Space に属するか
    }
    
    private static Dictionary<string, SpoutCameraInfo> spoutCameras = new Dictionary<string, SpoutCameraInfo>();
    
    public class SpaceMonitor
    {
        public DynamicVariableSpace Space;
        public string SpaceName;
        public Dictionary<string, object> LastValues = new Dictionary<string, object>();
        private bool _isMonitoring = false;
        private string _cachedCameraName = null;
        private HashSet<string> _ownedSpoutNames = new HashSet<string>();
        
        // ★ このモニターのユニークID（ログ用）
        private string _monitorId;

        public SpaceMonitor(DynamicVariableSpace space, string spaceName)
        {
            Space = space;
            SpaceName = spaceName;
            _monitorId = $"{spaceName}@{space.ReferenceID}";
        }

        public void StartMonitoring()
        {
            if (_isMonitoring || Space == null)
                return;

            _isMonitoring = true;
            Log.LogInfo($"[{_monitorId}] Starting monitoring...");

            var dynamicValuesField = typeof(DynamicVariableSpace).GetField("_dynamicValues", 
                BindingFlags.Instance | BindingFlags.NonPublic);

            if (dynamicValuesField == null)
            {
                Log.LogError($"[{_monitorId}] Could not find _dynamicValues field!");
                return;
            }

            var dynamicValues = dynamicValuesField.GetValue(Space);
            var dict = dynamicValues as System.Collections.IDictionary;

            if (dict == null)
            {
                Log.LogError($"[{_monitorId}] _dynamicValues is not a dictionary!");
                return;
            }

            foreach (System.Collections.DictionaryEntry entry in dict)
            {
                var key = entry.Key;
                var valueManager = entry.Value;

                var keyType = key.GetType();
                var nameField = keyType.GetField("name", BindingFlags.Instance | BindingFlags.Public);
                string varName = (string)nameField.GetValue(key);

                var typeField = keyType.GetField("type", BindingFlags.Instance | BindingFlags.Public);
                Type varType = (Type)typeField.GetValue(key);

                Log.LogInfo($"[{_monitorId}] Found variable: '{varName}' (Type: {varType.Name})");

                SetupMonitoringForManager(varName, varType, valueManager);
            }

            Log.LogInfo($"[{_monitorId}] Monitoring started");
        }

        private void SetupMonitoringForManager(string varName, Type varType, object valueManager)
        {
            var valueProperty = valueManager.GetType().GetProperty("Value");
            if (valueProperty == null)
            {
                Log.LogWarning($"[{_monitorId}] Could not find Value property for '{varName}'");
                return;
            }

            object initialValue = valueProperty.GetValue(valueManager);
            LastValues[varName] = initialValue;
            Log.LogInfo($"[{_monitorId}] Initial value: '{varName}' = {initialValue}");

            if (varName == "CameraName" && initialValue is string cameraName)
            {
                _cachedCameraName = cameraName;
            }

            if (initialValue != null && !IsDefaultValue(initialValue, varType))
            {
                Log.LogInfo($"[{_monitorId}] Processing initial value for '{varName}'");
                OnVariableChanged(Space, SpaceName, varName, initialValue, _cachedCameraName, this);
            }

            StartPolling(varName, valueProperty, valueManager);
        }

        private void StartPolling(string varName, PropertyInfo valueProperty, object valueManager)
        {
            void Poll()
            {
                if (Space == null || Space.IsRemoved || !_isMonitoring)
                    return;

                try
                {
                    object currentValue = valueProperty.GetValue(valueManager);
                    object lastValue = LastValues.ContainsKey(varName) ? LastValues[varName] : null;

                    bool changed = false;

                    if (varName == "TargetRTP" && currentValue is RenderTextureProvider rtpCurrent)
                    {
                        if (lastValue is RenderTextureProvider rtpLast)
                        {
                            int currentAssetId = rtpCurrent.Asset?.AssetId ?? -1;
                            int lastAssetId = rtpLast.Asset?.AssetId ?? -1;
                            
                            if (currentAssetId != lastAssetId)
                            {
                                Log.LogInfo($"[{_monitorId}] RTP AssetId changed: {lastAssetId} → {currentAssetId}");
                                changed = true;
                            }
                        }
                        else
                        {
                            changed = true;
                        }
                    }
                    else if (varName == "CameraName" && currentValue is string)
                    {
                        if (currentValue as string != lastValue as string)
                        {
                            _cachedCameraName = currentValue as string;
                            Log.LogInfo($"[{_monitorId}] CameraName changed to: {_cachedCameraName}");
                            changed = true;
                        }
                    }
                    else
                    {
                        if (currentValue == null && lastValue != null)
                        {
                            changed = true;
                        }
                        else if (currentValue != null && lastValue == null)
                        {
                            changed = true;
                        }
                        else if (currentValue != null && lastValue != null && !currentValue.Equals(lastValue))
                        {
                            changed = true;
                        }
                    }

                    if (changed)
                    {
                        Log.LogInfo($"[{_monitorId}] Variable '{varName}' changed");
                        LastValues[varName] = currentValue;
                        OnVariableChanged(Space, SpaceName, varName, currentValue, _cachedCameraName, this);
                    }
                }
                catch (Exception ex)
                {
                    Log.LogError($"Error polling variable '{varName}' in [{_monitorId}]: {ex.Message}");
                }

                Space.RunInSeconds(0.1f, Poll);
            }

            Space.RunInSeconds(0.1f, Poll);
        }

        private bool IsDefaultValue(object value, Type type)
        {
            if (value == null)
                return true;

            if (type.IsValueType)
            {
                object defaultValue = Activator.CreateInstance(type);
                return value.Equals(defaultValue);
            }

            if (type == typeof(string))
            {
                return string.IsNullOrEmpty((string)value);
            }

            return false;
        }

        public void RegisterSpoutName(string spoutName)
        {
            _ownedSpoutNames.Add(spoutName);
        }

        public void UnregisterSpoutName(string spoutName)
        {
            _ownedSpoutNames.Remove(spoutName);
        }

        public void Dispose()
        {
            Log.LogInfo($"[{_monitorId}] Monitor disposed, cleaning up {_ownedSpoutNames.Count} Spout cameras");
            
            foreach (var spoutName in _ownedSpoutNames.ToList())
            {
                if (spoutCameras.ContainsKey(spoutName))
                {
                    var command = new SpoutCommand
                    {
                        Type = SpoutCommandType.Delete,
                        SpoutName = spoutName
                    };
                    _messenger.SendObject("SpoutCommand", command);
                    spoutCameras.Remove(spoutName);
                    Log.LogInfo($"Deleted Spout camera: '{spoutName}'");
                }
            }
            
            _ownedSpoutNames.Clear();
            _isMonitoring = false;
            LastValues.Clear();
            Space = null;
        }
    }

    [HarmonyPatch(typeof(DynamicVariableSpace), "UpdateName")]
    [HarmonyPostfix]
    public static void UpdateName(DynamicVariableSpace __instance)
    {
        string spaceName = __instance.SpaceName.Value;
        
        if (!spaceName.StartsWith("ResoniteSpout."))
            return;
        
        if (!string.IsNullOrEmpty(ConfigSpacePrefix.Value))
        {
            string expectedName = TargetVariableSpaceName;
            if (spaceName != expectedName)
            {
                return;
            }
        }

        // ★ インスタンスベースでチェック
        if (_monitors.ContainsKey(__instance))
        {
            Log.LogInfo($"Space '{spaceName}' (ReferenceID: {__instance.ReferenceID}) UpdateName called on already monitored instance, ignoring.");
            return;
        }

        Log.LogInfo($"Target space detected: {spaceName} (ReferenceID: {__instance.ReferenceID})");

        var monitor = new SpaceMonitor(__instance, spaceName);
        _monitors[__instance] = monitor;

        __instance.RunInUpdates(10, () =>
        {
            if (_monitors.ContainsKey(__instance) && _monitors[__instance] == monitor)
            {
                monitor.StartMonitoring();
            }
            else
            {
                Log.LogInfo($"[{spaceName}@{__instance.ReferenceID}] Monitor was replaced before StartMonitoring, skipping.");
            }
        });

        Log.LogInfo($"Monitoring scheduled for space: {spaceName} (ReferenceID: {__instance.ReferenceID})");
    }

    [HarmonyPatch(typeof(DynamicVariableSpace), "OnDispose")]
    [HarmonyPostfix]
    public static void OnDispose(DynamicVariableSpace __instance)
    {
        // ★ インスタンスベースでチェック
        if (_monitors.ContainsKey(__instance))
        {
            string spaceName = __instance.SpaceName.Value;
            _monitors[__instance].Dispose();
            _monitors.Remove(__instance);
            Log.LogInfo($"Stopped monitoring space: {spaceName} (ReferenceID: {__instance.ReferenceID})");
        }
    }

    private static void OnVariableChanged(DynamicVariableSpace space, string spaceName, string varName, object value, string cameraName, SpaceMonitor monitor)
    {
        string monitorId = $"{spaceName}@{space.ReferenceID}";
        Log.LogInfo($"[{monitorId}] Processing change for variable '{varName}'");
        
        if (varName == "TargetRTP" && value is RenderTextureProvider rtp)
        {
            if (rtp?.Asset != null)
            {
                Log.LogInfo($"[{monitorId}] TargetRTP AssetId: {rtp.Asset.AssetId}");
                
                string[] parts = spaceName.Split('.');
                if (parts.Length == 2)
                {
                    string suffix = parts[1];
                    CreateOrUpdateSpoutCamera(space, suffix, rtp.Asset.AssetId, cameraName, monitor);
                }
            }
            else
            {
                Log.LogWarning($"[{monitorId}] TargetRTP has no Asset");
            }
        }
        else if (varName == "CameraName")
        {
            string[] parts = spaceName.Split('.');
            if (parts.Length == 2)
            {
                string suffix = parts[1];
                
                if (_monitors.ContainsKey(space))
                {
                    var mon = _monitors[space];
                    if (mon.LastValues.ContainsKey("TargetRTP") && 
                        mon.LastValues["TargetRTP"] is RenderTextureProvider existingRtp &&
                        existingRtp?.Asset != null)
                    {
                        CreateOrUpdateSpoutCamera(space, suffix, existingRtp.Asset.AssetId, cameraName as string, mon);
                    }
                }
            }
        }
    }

    public static void CreateOrUpdateSpoutCamera(DynamicVariableSpace space, string suffix, int assetId, string cameraName, SpaceMonitor monitor)
    {
        // ★ Spout 名を生成（Space の ReferenceID を含める）
        string spoutName;
        if (!string.IsNullOrEmpty(cameraName))
        {
            spoutName = $"[ResoSpout] {suffix} - {cameraName}";
        }
        else
        {
            spoutName = $"[ResoSpout] {suffix}";
        }
        
        // ★ 同じ名前でも異なる Space の場合は別の Spout として扱う
        // Space の ReferenceID を使ってユニークにする
        string uniqueSpoutKey = $"{spoutName}@{space.ReferenceID}";
        
        var command = new SpoutCommand();
        
        // ★ この Space が既に同じ名前の Spout を持っているかチェック
        var existingSpout = spoutCameras.Values.FirstOrDefault(info => 
            info.Space == space && 
            (info.SpoutName == spoutName || info.Suffix == suffix));
        
        if (existingSpout != null)
        {
            // 名前が変わった場合は古い Spout を削除
            if (existingSpout.SpoutName != spoutName)
            {
                var deleteCommand = new SpoutCommand
                {
                    Type = SpoutCommandType.Delete,
                    SpoutName = existingSpout.SpoutName
                };
                _messenger.SendObject("SpoutCommand", deleteCommand);
                spoutCameras.Remove(existingSpout.SpoutName);
                monitor.UnregisterSpoutName(existingSpout.SpoutName);
                Log.LogInfo($"Deleted old Spout camera: '{existingSpout.SpoutName}'");
            }
            else if (existingSpout.AssetId == assetId)
            {
                // 名前も AssetId も同じならスキップ
                Log.LogInfo($"Spout camera '{spoutName}' unchanged, skipping.");
                return;
            }
        }
        
        if (spoutCameras.ContainsKey(spoutName))
        {
            var existing = spoutCameras[spoutName];
            
            // AssetId が変わった場合は更新
            existing.AssetId = assetId;
            existing.CameraName = cameraName;
            
            command.Type = SpoutCommandType.Update;
            command.SpoutName = spoutName;
            command.AssetId = assetId;
            _messenger.SendObject("SpoutCommand", command);
            
            Log.LogInfo($"Updated Spout camera: '{spoutName}' (AssetId: → {assetId})");
        }
        else
        {
            // 新規作成
            var info = new SpoutCameraInfo
            {
                SpoutName = spoutName,
                AssetId = assetId,
                Suffix = suffix,
                CameraName = cameraName,
                Space = space
            };
            
            spoutCameras.Add(spoutName, info);
            monitor.RegisterSpoutName(spoutName);
            
            command.Type = SpoutCommandType.Create;
            command.SpoutName = spoutName;
            command.AssetId = assetId;
            _messenger.SendObject("SpoutCommand", command);
            
            Log.LogInfo($"Created Spout camera: '{spoutName}' (AssetId: {assetId})");
        }
    }
}
}
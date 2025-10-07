using System;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using BepInEx.NET.Common;
using BepInExResoniteShim;
using BepisResoniteWrapper;
using FrooxEngine;
using HarmonyLib;
using InterprocessLib;

namespace RenderBridge;

[ResonitePlugin(PluginMetadata.GUID, PluginMetadata.NAME, PluginMetadata.VERSION, PluginMetadata.AUTHORS, PluginMetadata.REPOSITORY_URL)]
[BepInDependency(BepInExResoniteShim.PluginMetadata.GUID, BepInDependency.DependencyFlags.HardDependency)]
public class ResoniteSpout : BasePlugin
{
    internal static new ManualLogSource Log = null!;
    
    public static Messenger? _messenger;
    // ★監視したい VariableSpace 名をここに指定
    private const string TargetVariableSpaceName = "Zozokasu";

    public override void Load()
    {
        Log = base.Log;
        ResoniteHooks.OnEngineReady += OnEngineReady;
        Log.LogInfo($"Plugin {PluginMetadata.GUID} loaded (minimal DV<string> tracker).");

        _messenger = new Messenger("ResoniteSpoutEngine");
        HarmonyInstance.PatchAll();
    }

    private void OnEngineReady()
    {
    }

    [HarmonyPatch]
    public static class DynamicVariableSpacePatch
    {
        [HarmonyPatch(typeof(DynamicVariableSpace), "UpdateName")]
        [HarmonyPostfix]
        public static void UpdateName(DynamicVariableSpace __instance)
        {
            RenderTextureProvider rtp;
            if (__instance.TryReadValue("TargetRTP", out rtp))
            {
                __instance.RunInUpdates(3, () =>
                {
                    Log.LogInfo($"TargetRTP found!!! {rtp}");
                    if (rtp.Asset == null) return;
                    Log.LogInfo($"AssetId: {rtp.Asset.AssetId}");
                    _messenger.SendValue<int>("RTAssetId", rtp.Asset.AssetId);
                    Log.LogInfo($"{rtp.Size}");
                });
            }
        }
        
        [HarmonyPatch(typeof(DynamicReferenceVariable<RenderTextureProvider>), "InitializeSyncMembers")]
        [HarmonyPostfix]
        public static void ParentReferencePostfix(DynamicReferenceVariable<RenderTextureProvider> __instance)
        {
            __instance.Reference.OnTargetChange += (target =>
            {
                if (target.Target == null)
                {
                    Log.LogInfo("DynamicVariableBase_Patch: Target changed to null asset");
                    return;
                }
                
                if(target.Target.Asset == null) 
                {
                    Log.LogInfo("DynamicVariableBase_Patch: Target changed to null asset");
                    return;
                }
                
                Log.LogInfo("DynamicVariableBase_Patch: Target changed to " + target.Target.Asset.AssetId);
            });
            
            __instance.Reference.OnReferenceChange += (reference =>
            {
                Log.LogInfo("DynamicVariableBase_Patch: Reference changed to " + reference);
            });

        }
    }
}

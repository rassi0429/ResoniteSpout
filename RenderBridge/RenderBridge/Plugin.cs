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
public class RenderBridge : BasePlugin
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

        _messenger = new Messenger("RenderBridge");
        
    }

    private void OnEngineReady()
    {
        try
        {
            var harmony = new Harmony(PluginMetadata.GUID + ".dvstringtracker");
            HarmonyInstance.PatchAll();
            Log.LogInfo("DV<string> tracker installed.");
        }
        catch (Exception ex)
        {
            Log.LogError($"Failed to install tracker: {ex}");
        }
        
        _messenger.ReceiveValue<int>("EchoReturn", (val) =>
        {
            Log.LogInfo($"EchoReturn: {val}");
        });

        _messenger.ReceiveValueList<byte>("RTData", (list) =>
        {
            Log.LogInfo($"RTData length is {list.Count}!");
            if (list.Count != 1048576) return;
            byte[] data = new byte[512 * 512 * 4];
            // unsafe
            // {
            //     using (DeviceContext deviceContext = DeviceContext.Create())
            //     {
            //         IntPtr glContext = IntPtr.Zero;
            //         glContext = deviceContext.CreateContext(IntPtr.Zero);
            //         deviceContext.MakeCurrent(glContext);
            //         fixed (byte* ptr = data)
            //         {
            //             for (int j = 0; j < 512 * 512 * 4; j++)
            //             {
            //                 data[j] = list[j];
            //             }
            //             spoutSender.SendImage(ptr, 512, 512, Gl.RGBA, true, 0);
            //         }
            //     }
            // }
            Log.LogInfo($"RTData received!");
        });
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

            Camera camera;
            if (__instance.TryReadValue("TargetCamera", out camera))
            {
                __instance.RunInUpdates(2, () =>
                {
                    Log.LogInfo($"Target Camera Found!!!");
                });
            }
        }
        
    }
    
}

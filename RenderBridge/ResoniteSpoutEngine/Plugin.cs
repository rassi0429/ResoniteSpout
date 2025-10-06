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
using Renderite.Shared;

namespace RenderBridge;

public enum SpoutCommandType
{
    Create,
    Delete,
    Update,
}
public class SpoutCommand : RendererCommand
{
    public SpoutCommandType Type;
    public string SpoutName = "";
    public int AssetId;

    public override void Pack(ref MemoryPacker packer)
    {
        packer.Write(Type);
        packer.Write(AssetId);
        packer.Write(SpoutName);
    }

    public override void Unpack(ref MemoryUnpacker unpacker)
    {
        unpacker.Read(ref Type);
        unpacker.Read(ref AssetId);
        unpacker.Read(ref SpoutName);
    }
}

[ResonitePlugin(PluginMetadata.GUID, PluginMetadata.NAME, PluginMetadata.VERSION, PluginMetadata.AUTHORS, PluginMetadata.REPOSITORY_URL)]
[BepInDependency(BepInExResoniteShim.PluginMetadata.GUID, BepInDependency.DependencyFlags.HardDependency)]
public class ResoniteSpout : BasePlugin
{
    internal static new ManualLogSource Log = null!;
    
    public static Messenger? _messenger;
    // ★監視したい VariableSpace 名をここに指定
    private const string TargetVariableSpaceName = "ResoniteSpout";

    public static Dictionary<string, int> spoutCameras = new Dictionary<string, int>();

    public override void Load()
    {
        Log = base.Log;
        ResoniteHooks.OnEngineReady += OnEngineReady;
        Log.LogInfo($"Plugin {PluginMetadata.GUID} loaded (minimal DV<string> tracker).");

        _messenger = new Messenger("Zozokasu.ResoniteSpout",[typeof(SpoutCommand)]);
        
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
        public static void CreateOrUpdateSpoutCamera(string cameraName, int assetId)
        {
            var command = new SpoutCommand();
            if (spoutCameras.ContainsKey(cameraName))
            {
                //IDが重複したら　「cameraName (1)」「cameraName (2)」のような名前にしたい
                spoutCameras[cameraName] = assetId;
                command.Type = SpoutCommandType.Update;
                command.SpoutName = cameraName;
                command.AssetId = assetId;
                _messenger.SendObject("SpoutCommand", command);
                return;
            }
            spoutCameras.Add(cameraName, assetId);
            command.Type = SpoutCommandType.Create;
            command.SpoutName = cameraName;
            command.AssetId = assetId;
            _messenger.SendObject("SpoutCommand", command);
            
        }
        
        [HarmonyPatch(typeof(DynamicVariableSpace), "UpdateName")]
        [HarmonyPostfix]
        public static void UpdateName(DynamicVariableSpace __instance)
        {
            if (!__instance.SpaceName.Value.StartsWith("ResoniteSpout")) return;
            //SpaceName should be "ResoniteSpout.YourName"
            
            string[] spoutIds = __instance.SpaceName.Value.Split('.');
            if (spoutIds.Length != 2) return;
            
            string spoutId = spoutIds[1];
            
            RenderTextureProvider rtp;
            if (__instance.TryReadValue("TargetRTP", out rtp))
            {
                __instance.RunInUpdates(3, () =>
                {
                    Log.LogInfo($"TargetRTP found!!! {rtp}");
                    if (rtp.Asset == null) return;
                    Log.LogInfo($"AssetId: {rtp.Asset.AssetId}");
                    
                    CreateOrUpdateSpoutCamera(spoutId , rtp.Asset.AssetId);
                    _messenger.SendString("DbgMessage", $"Send creation command! ID: {spoutId}");
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

        // [HarmonyPatch(typeof(ComponentBase<Component>), "OnChanges")]
        // [HarmonyPostfix]
        // public static void PostFix(ComponentBase<Component> __instance)
        // {
        //     Type t = __instance.GetType();
        //     Log.LogInfo($"{__instance.GetType().Name} changed!");
        //
        // }
        
    }
    
}

using System;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using InterprocessLib;
using Renderite.Unity;
using UnityEngine;
using Klak.Spout;
using RenderBridge;
using Renderite.Shared;


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


namespace ResoniteSpoutRenderer
{
    [BepInPlugin("zokasu.ResoniteSpout", "ResoniteSpoutRenderer", "0.1.0")]
    public class RenderBridgeRenderer : BaseUnityPlugin
    {
        public static ManualLogSource Log;

        private Messenger _msg;
        private readonly ConcurrentQueue<Action> _mainQueue = new();
        
        

        // Spout
        private const string SpoutName = "RenderBridgeRT";
        

        // もしカメラの最終画像を送りたい時用の簡易ブリッタ（任意）
        private RenderTexture _fallbackRT;

        private static Dictionary<string, SpoutStruct> spouts = new();
        
        public class SpoutStruct
        {
            public int assetId;
            public IntPtr spoutSender;
            public Texture2D sharedTexture;
            
            public void GetOrCreateSharedTexture()
            {
                if (spoutSender == IntPtr.Zero)
                {
                    Log.LogError($"Sender is null");
                    return;
                }

                var ptr = PluginEntry.GetTexturePointer(spoutSender);
                if (ptr != IntPtr.Zero)
                {
                    Texture2D sharedTexture = UnityEngine.Texture2D.CreateExternalTexture(
                        PluginEntry.GetTextureWidth(spoutSender),
                        PluginEntry.GetTextureHeight(spoutSender),
                        UnityEngine.TextureFormat.ARGB32,false,false,ptr);
                    sharedTexture.hideFlags = HideFlags.DontSave;
                    this.sharedTexture = sharedTexture;
                    Log.LogInfo($"Created shared texture {assetId}!");
                }
                else
                {
                    Log.LogInfo($"Texture pointer for ID: {assetId} is null!");
                }
            }
        }


        void Awake()
        {

            Log = Logger;

            // Messenger
            _msg = new Messenger("Zozokasu.ResoniteSpout", [typeof(SpoutCommand)]);
            // SpoutSender をアタッチした GameObject を常駐させる
            // _senderGO = new GameObject("[ResoniteSpoutEngine SpoutSender]");
            // DontDestroyOnLoad(_senderGO);
            // _sender = _senderGO.AddComponent<SpoutSender>();
            // _sender.spoutName = SpoutName;
            // _sender.keepAlpha = true;
            // _sender.sourceTexture = Texture2D.blackTexture;

            // RTAssetId を受けたらメインスレッドで割り当て
            _msg.ReceiveObject<SpoutCommand>("SpoutCommand", (command) =>
            {
                _mainQueue.Enqueue(() => ProcessCommand(command));
                Log.LogInfo($"Command {command.Type} received!, Camera: {command.SpoutName}, ID: {command.AssetId}");
            });

            _msg.ReceiveString("DbgMessage", (s =>
            {
                Log.LogInfo($"[INTERPROCESS DEBUG]: {s}");
            } ));

            Log.LogInfo("[ResoniteSpoutRenderer] Initialized. Waiting for RTAssetId…");
            Log.LogInfo($"{SystemInfo.graphicsDeviceType}");

            // Harmony harmony = new Harmony("dev.zozokasu.renderBridge");
            // harmony.PatchAll();
        }

        void Update()
        {
            // 受信スレッド→メインスレッドへ投げた仕事を捌く
            while (_mainQueue.TryDequeue(out var a))
            {
                try { a(); }
                catch (Exception e) { Log.LogError(e); }
            }
            
            SendRenderTexture();
        }
        
        void OnDestroy()
        {
            try
            {
                if (_fallbackRT != null)
                {
                    _fallbackRT.Release();
                    Destroy(_fallbackRT);
                }
            }
            catch { /* ignore */ }
        }
        
        static SpoutStruct GetOrCreateSpout(string spoutName, int assetId)
        {
            SpoutStruct spout = new();
            spout.assetId = assetId;
            if (!spouts.ContainsKey(spoutName))
            {
                RenderTexture texture = RenderingManager.Instance.RenderTextures.GetAsset(assetId).Texture;
                spout.spoutSender = PluginEntry.CreateSender($"[ResoniteSpoutRenderer]-{spoutName}", texture.width,texture.height);
                if (spout.spoutSender == IntPtr.Zero)
                {
                    Log.LogInfo($"Failed to create Spout sender ID{assetId}");
                }
                Log.LogInfo($"Created Spout sender \"[ResoniteSpoutRenderer]-{spoutName}\"!");
            }
            return spout;
        }
        
        
        // ===== ここが肝：RT をそのまま Spout に渡す =====
        private void ProcessCommand(SpoutCommand command)
        {
            switch (command.Type)
            {
                case SpoutCommandType.Create:
                    if (spouts.ContainsKey(command.SpoutName)) break;
                    
                    SpoutStruct spout = GetOrCreateSpout(command.SpoutName, command.AssetId);
                    spouts.Add(command.SpoutName, spout);
                    break;
                
                case SpoutCommandType.Update:
                    if (spouts.ContainsKey(command.SpoutName))
                    {
                        spouts[command.SpoutName].assetId = command.AssetId;
                        RenderTexture texture = RenderingManager.Instance.RenderTextures.GetAsset(command.AssetId).Texture;
                        PluginEntry.DestroySharedObject(spouts[command.SpoutName].spoutSender);
                        spouts[command.SpoutName].spoutSender = PluginEntry.CreateSender($"[ResoniteSpoutRenderer] - {command.SpoutName}", texture.width, texture.height);
                    }
                    break;
                
                case SpoutCommandType.Delete:
                    if (spouts.ContainsKey(command.SpoutName))
                    {
                        PluginEntry.DestroySharedObject(spouts[command.SpoutName].spoutSender);
                        spouts.Remove(command.SpoutName);
                    }
                    break;
            }
        }
        
        static void SendRenderTexture()
        {
            foreach (var spout in spouts.Values)
            {
                int assetId = spout.assetId;
                RenderTexture source = RenderingManager.Instance.RenderTextures.GetAsset(assetId).Texture;
                if (spout.spoutSender == IntPtr.Zero)
                {
                    Log.LogInfo("spout not ready!");
                    continue;
                }

                if (spout.sharedTexture == null)
                {
                    spout.GetOrCreateSharedTexture();
                    continue;
                }
                
                var tempRt = UnityEngine.RenderTexture.GetTemporary(spout.sharedTexture.width, spout.sharedTexture.height);
                Graphics.Blit(source, tempRt, new Vector2(1.0f, -1.0f), new Vector2(0.0f, 1.0f));
                Graphics.CopyTexture(tempRt, spout.sharedTexture);
                RenderTexture.ReleaseTemporary(tempRt);
            }
            
            foreach (var spout in spouts.Values)
            {
                Util.IssuePluginEvent(PluginEntry.Event.Update, spout.spoutSender);
            }
                
        }
        
        

        // （任意）カメラの最終画像を送りたいだけのとき用

        // Built-in RP 用の簡易キャプチャ（URP/HDRPはRendererFeature等を使ってください）
        private class Blitter : MonoBehaviour
        {
            private RenderTexture _dst;
            public void Init(RenderTexture dst) => _dst = dst;

            void OnRenderImage(RenderTexture src, RenderTexture dst)
            {
                if (_dst != null) Graphics.Blit(src, _dst); // Spout用にコピー
                Graphics.Blit(src, dst);                     // 通常の描画
            }
        }
        
        [HarmonyPatch]
        class SpoutPatch
        {
            [HarmonyPatch(typeof(Camera360Display), "OnRenderImage")]
            [HarmonyPrefix]
            static void _postfix(Camera360Display __instance, RenderTexture src, RenderTexture dest)
            {
                Log.LogInfo("postfix called!");
            }
        }
        
    }
    
}

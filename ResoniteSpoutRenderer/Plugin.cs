using System.Collections.Concurrent;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using InterprocessLib;
using Renderite.Unity;
using UnityEngine;
using RenderBridge;

namespace ResoniteSpoutRenderer
{
    [BepInPlugin("zokasu.ResoniteSpout", "ResoniteSpoutEngine", "1.2.0")]
    public class RenderBridgeRenderer : BaseUnityPlugin
    {
        static ManualLogSource Log;

        private Messenger _msg;
        private readonly ConcurrentQueue<Action> _mainQueue = new();

        static int activeRenderTextureId = -1;
        
        static Dictionary<string, IntPtr> _plugins = new();
        static Dictionary<string, Texture2D> _sharedTextures = new();

        private void Awake()
        {
            var harmony = new Harmony("dev.zozokasu.renderBridge");
            harmony.PatchAll();

            Log = Logger;

            // Messenger
            _msg = new Messenger("ResoniteSpoutEngine");

            // RTAssetId を受けたらメインスレッドで割り当て
            _msg.ReceiveValue<int>("RTAssetId", assetId =>
            {
                _mainQueue.Enqueue(() =>
                {
                    activeRenderTextureId = assetId;
                    Log.LogInfo($"Activated renderer ID{assetId}!");
                });
            });

            Log.LogInfo("[ResoniteSpoutRenderer] Initialized. Waiting for RTAssetId…");
            Log.LogInfo($"{SystemInfo.graphicsDeviceType}");

        }

        private void Update()
        {
            // 受信スレッド→メインスレッドへ投げた仕事を捌く
            while (_mainQueue.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception e) { Log.LogError(e); }
            }
            
            SendRenderTexture(activeRenderTextureId);
            foreach (var p in _plugins)
            {
                Util.IssuePluginEvent(PluginEntry.Event.Update, p.Value);
            }
        }
        
        static IntPtr GetOrCreatePlugin(int assetId)
        {
            var key = assetId.ToString();
            if (!_plugins.ContainsKey(key))
            {
                IntPtr plugin = PluginEntry.CreateSender($"[ResoniteSpoutRenderer]-{assetId}", 512,512);
                if (plugin == IntPtr.Zero)
                {
                    Log.LogInfo($"Failed to create Spout sender ID{assetId}");
                    return IntPtr.Zero;
                }
                _plugins.Add(key, plugin);
                Log.LogInfo($"Created Spout sender ID{assetId}!");
            }
            return _plugins[key];
        }

        static Texture2D GetOrCreateSharedTexture(IntPtr plugin)
        {
            if (plugin == IntPtr.Zero)
            {
                return null;
            }
            var key = _plugins.FirstOrDefault(x => x.Value == plugin).Key;

            if (!_sharedTextures.ContainsKey(key))
            {
                var ptr = PluginEntry.GetTexturePointer(plugin);
                if (ptr != IntPtr.Zero)
                {
                    UnityEngine.Texture2D sharedTexture = UnityEngine.Texture2D.CreateExternalTexture(
                        PluginEntry.GetTextureWidth(plugin),
                        PluginEntry.GetTextureHeight(plugin),
                        UnityEngine.TextureFormat.ARGB32,false,false,ptr);
                    sharedTexture.hideFlags = HideFlags.DontSave;
                    _sharedTextures.Add(key,sharedTexture);
                    Log.LogInfo($"Created shared texture {key}!");
                }
            }
            return _sharedTextures[key];
        }

        static void SendRenderTexture(int assetId)
        {
            var source = RenderingManager.Instance.RenderTextures.GetAsset(assetId).Texture;
            var plugin = GetOrCreatePlugin(assetId);
            Util.IssuePluginEvent(PluginEntry.Event.Update, plugin);
            var sharedTexture = GetOrCreateSharedTexture(plugin);

            if (plugin == IntPtr.Zero || !sharedTexture)
            {
                Log.LogInfo("spout not ready or sharedTexture is null");
                return;
            }
                
            var tempRt = RenderTexture.GetTemporary(sharedTexture.width, sharedTexture.height);
            Graphics.Blit(source, tempRt, new Vector2(1.0f, -1.0f), new Vector2(0.0f, 1.0f));
            Graphics.CopyTexture(tempRt, sharedTexture);
            RenderTexture.ReleaseTemporary(tempRt);
        }
    }
}

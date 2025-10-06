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

namespace ResoniteSpoutRenderer
{
    [BepInPlugin("zokasu.ResoniteSpout", "ResoniteSpoutEngine", "1.2.0")]
    public class RenderBridgeRenderer : BaseUnityPlugin
    {
        public static ManualLogSource Log;

        private Messenger _msg;
        private readonly ConcurrentQueue<Action> _mainQueue = new();

        public static int activeRenderTextureId = -1;

        private static int[] allowedHeight = { 512 };
        

        // Spout
        private GameObject _senderGO;
        private SpoutSender _sender;
        private const string SpoutName = "RenderBridgeRT";
        

        // もしカメラの最終画像を送りたい時用の簡易ブリッタ（任意）
        private Camera _mainCam;
        private RenderTexture _fallbackRT;
        public bool UseCameraBlitIfNoRT = false;
        
        static Dictionary<string, IntPtr> plugins = new Dictionary<string, IntPtr>();
        static Dictionary<string, Texture2D> sharedTextures = new Dictionary<string, Texture2D>();

        void Awake()
        {

            Log = Logger;

            // Messenger
            _msg = new Messenger("ResoniteSpoutEngine");
            // SpoutSender をアタッチした GameObject を常駐させる
            // _senderGO = new GameObject("[ResoniteSpoutEngine SpoutSender]");
            // DontDestroyOnLoad(_senderGO);
            // _sender = _senderGO.AddComponent<SpoutSender>();
            // _sender.spoutName = SpoutName;
            // _sender.keepAlpha = true;
            // _sender.sourceTexture = Texture2D.blackTexture;

            // RTAssetId を受けたらメインスレッドで割り当て
            _msg.ReceiveValue<int>("RTAssetId", assetId =>
            {
                _mainQueue.Enqueue(() => AssignRTToSender(assetId));
            });

            Log.LogInfo("[ResoniteSpoutRenderer] Initialized. Waiting for RTAssetId…");
            Log.LogInfo($"{SystemInfo.graphicsDeviceType}");

            Harmony harmony = new Harmony("dev.zozokasu.renderBridge");
            harmony.PatchAll();
        }

        void Update()
        {
            // 受信スレッド→メインスレッドへ投げた仕事を捌く
            while (_mainQueue.TryDequeue(out var a))
            {
                try { a(); }
                catch (Exception e) { Log.LogError(e); }
            }
            
            SendRenderTexture(activeRenderTextureId);
            foreach (var p in plugins)
            {
                Util.IssuePluginEvent(PluginEntry.Event.Update, p.Value);
            }
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
                if (_senderGO != null) Destroy(_senderGO);
            }
            catch { /* ignore */ }
        }

        static IntPtr GetOrCreatePlugin(int assetId)
        {
            string key = assetId.ToString();
            if (!plugins.ContainsKey(key))
            {
                IntPtr plugin = PluginEntry.CreateSender($"[ResoniteSpoutRenderer]-{assetId}", 512,512);
                if (plugin == IntPtr.Zero)
                {
                    Log.LogInfo($"Failed to create Spout sender ID{assetId}");
                    return IntPtr.Zero;
                }
                plugins.Add(key, plugin);
                Log.LogInfo($"Created Spout sender ID{assetId}!");
            }
            return plugins[key];
        }

        static Texture2D GetOrCreateSharedTexture(IntPtr plugin)
        {
            if (plugin == IntPtr.Zero)
            {
                return null;
            }
            string key = plugins.FirstOrDefault(x => x.Value == plugin).Key;

            if (!sharedTextures.ContainsKey(key))
            {
                var ptr = PluginEntry.GetTexturePointer(plugin);
                if (ptr != IntPtr.Zero)
                {
                    UnityEngine.Texture2D sharedTexture = UnityEngine.Texture2D.CreateExternalTexture(
                        PluginEntry.GetTextureWidth(plugin),
                        PluginEntry.GetTextureHeight(plugin),
                        UnityEngine.TextureFormat.ARGB32,false,false,ptr);
                    sharedTexture.hideFlags = HideFlags.DontSave;
                    sharedTextures.Add(key,sharedTexture);
                    Log.LogInfo($"Created shared texture {key}!");
                }
            }
            return sharedTextures[key];
        }

        // ===== ここが肝：RT をそのまま Spout に渡す =====
        private void AssignRTToSender(int assetId)
        {
            activeRenderTextureId = assetId;
            Log.LogInfo($"Activated renderer ID{assetId}!");
            try
            {
            }
            catch (Exception e)
            {
                Log.LogError(e);
            }
        }
        
        static void SendRenderTexture(int assetId)
        {
            RenderTexture source = RenderingManager.Instance.RenderTextures.GetAsset(assetId).Texture;
            IntPtr plugin = GetOrCreatePlugin(assetId);
            Util.IssuePluginEvent(PluginEntry.Event.Update, plugin);
            Texture2D sharedTexture = GetOrCreateSharedTexture(plugin);

            if (plugin == IntPtr.Zero || sharedTexture == null)
            {
                Log.LogInfo("spout not ready or sharedTexture is null");
                return;
            }
                
            var tempRt = UnityEngine.RenderTexture.GetTemporary(sharedTexture.width, sharedTexture.height);
            Graphics.Blit(source, tempRt, new Vector2(1.0f, -1.0f), new Vector2(0.0f, 1.0f));
            Graphics.CopyTexture(tempRt, sharedTexture);
            RenderTexture.ReleaseTemporary(tempRt);
                
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

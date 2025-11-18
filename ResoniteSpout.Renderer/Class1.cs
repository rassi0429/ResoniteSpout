using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Logging;
using InterprocessLib;
using Renderite.Unity;
using UnityEngine;
using ResoniteSpout.Shared;

namespace ResoniteSpoutRenderer
{
    [BepInPlugin("zozokasu.ResoniteSpout.Renderer", "ResoniteSpoutRenderer", "0.1.0")]
    public class ResoniteSpoutRenderer : BaseUnityPlugin
    {
        public static ManualLogSource Log;

        private Messenger _msg;
        private readonly ConcurrentQueue<Action> _mainQueue = new();

        // ★ SpoutName → SpoutStruct のマッピング（複数管理）
        private static Dictionary<string, SpoutStruct> spouts = new();
        
        public class SpoutStruct
        {
            public string SpoutName;
            public int AssetId;
            public IntPtr SpoutSender;
            public Texture2D SharedTexture;
            public int InitializationAttempts;
            
            public bool IsValid => SpoutSender != IntPtr.Zero;
            public bool IsReady => IsValid && SharedTexture != null;
            
            public bool TryCreateSharedTexture()
            {
                InitializationAttempts++;
                
                if (SpoutSender == IntPtr.Zero)
                {
                    Log.LogError($"[{SpoutName}] Sender is null, cannot create shared texture");
                    return false;
                }

                var ptr = PluginEntry.GetTexturePointer(SpoutSender);
                if (ptr == IntPtr.Zero)
                {
                    if (InitializationAttempts > 100)
                    {
                        Log.LogError($"[{SpoutName}] Failed to get texture pointer after {InitializationAttempts} attempts");
                        return false;
                    }
                    return false;
                }

                int width = PluginEntry.GetTextureWidth(SpoutSender);
                int height = PluginEntry.GetTextureHeight(SpoutSender);
                
                SharedTexture = Texture2D.CreateExternalTexture(
                    width,
                    height,
                    TextureFormat.ARGB32,
                    false,
                    false,
                    ptr);
                SharedTexture.hideFlags = HideFlags.DontSave;
                
                Log.LogInfo($"[{SpoutName}] Created shared texture {width}x{height} after {InitializationAttempts} attempts");
                return true;
            }
            
            public void Dispose()
            {
                if (SpoutSender != IntPtr.Zero)
                {
                    PluginEntry.DestroySharedObject(SpoutSender);
                    SpoutSender = IntPtr.Zero;
                }
                
                if (SharedTexture != null)
                {
                    Destroy(SharedTexture);
                    SharedTexture = null;
                }
                
                Log.LogInfo($"[{SpoutName}] Disposed");
            }
        }

        void Awake()
        {
            Log = Logger;

            _msg = new Messenger("Zozokasu.ResoniteSpout", [typeof(SpoutCommand) ]);

            _msg.ReceiveObject<SpoutCommand>("SpoutCommand", (command) =>
            {
                _mainQueue.Enqueue(() => ProcessCommand(command));
                Log.LogInfo($"Command received: {command.Type}, Name: '{command.SpoutName}', AssetId: {command.AssetId}");
            });

            _msg.ReceiveString("DbgMessage", (s =>
            {
                Log.LogInfo($"[DEBUG]: {s}");
            }));

            Log.LogInfo($"[ResoniteSpoutRenderer] Initialized. Graphics: {SystemInfo.graphicsDeviceType}");
        }

        void Update()
        {
            // メインスレッドでコマンドを処理
            while (_mainQueue.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception e) { Log.LogError($"Error processing command: {e}"); }
            }
            
            SendRenderTextures();
        }
        
        void OnDestroy()
        {
            foreach (var spout in spouts.Values)
            {
                spout.Dispose();
            }
            spouts.Clear();
        }
        
        private void ProcessCommand(SpoutCommand command)
        {
            switch (command.Type)
            {
                case SpoutCommandType.Create:
                    CreateSpout(command.SpoutName, command.AssetId);
                    break;
                
                case SpoutCommandType.Update:
                    UpdateSpout(command.SpoutName, command.AssetId);
                    break;
                
                case SpoutCommandType.Delete:
                    DeleteSpout(command.SpoutName);
                    break;
            }
        }
        
        private void CreateSpout(string spoutName, int assetId)
        {
            Log.LogInfo($"[{spoutName}] CreateSpout called with AssetId: {assetId}");
            
            // ★ 既に存在する場合は削除してから作り直す
            if (spouts.ContainsKey(spoutName))
            {
                Log.LogInfo($"[{spoutName}] Already exists, recreating...");
                spouts[spoutName].Dispose();
                spouts.Remove(spoutName);
            }
            
            // RenderTexture を取得
            var rtAsset = RenderingManager.Instance.RenderTextures.GetAsset(assetId);
            if (rtAsset?.Texture == null)
            {
                Log.LogError($"[{spoutName}] RenderTexture not found for AssetId: {assetId}");
                return;
            }
            
            RenderTexture texture = rtAsset.Texture;
            Log.LogInfo($"[{spoutName}] RenderTexture found: {texture.width}x{texture.height}");
            
            // Spout Sender を作成
            IntPtr sender = PluginEntry.CreateSender(spoutName, texture.width, texture.height);
            
            if (sender == IntPtr.Zero)
            {
                Log.LogError($"[{spoutName}] Failed to create Spout sender");
                return;
            }
            
            Log.LogInfo($"[{spoutName}] Spout sender created: {sender}");
            
            // ★ 初期化のために Update イベントを発行
            Util.IssuePluginEvent(PluginEntry.Event.Update, sender);
            
            // SpoutStruct を作成
            var spout = new SpoutStruct
            {
                SpoutName = spoutName,
                AssetId = assetId,
                SpoutSender = sender,
                InitializationAttempts = 0
            };
            
            spouts[spoutName] = spout;
            Log.LogInfo($"[{spoutName}] Added to spouts dictionary. Total spouts: {spouts.Count}");
        }
        
        private void UpdateSpout(string spoutName, int assetId)
        {
            if (!spouts.ContainsKey(spoutName))
            {
                Log.LogWarning($"[{spoutName}] Not found for update, creating new...");
                CreateSpout(spoutName, assetId);
                return;
            }
            
            var spout = spouts[spoutName];
            
            // AssetId が同じ場合はスキップ
            if (spout.AssetId == assetId)
            {
                Log.LogInfo($"[{spoutName}] AssetId unchanged ({assetId}), skipping update");
                return;
            }
            
            Log.LogInfo($"[{spoutName}] Updating: AssetId {spout.AssetId} → {assetId}");
            
            // 作り直す
            CreateSpout(spoutName, assetId);
        }
        
        private void DeleteSpout(string spoutName)
        {
            if (spouts.ContainsKey(spoutName))
            {
                spouts[spoutName].Dispose();
                spouts.Remove(spoutName);
                Log.LogInfo($"[{spoutName}] Deleted. Remaining spouts: {spouts.Count}");
            }
            else
            {
                Log.LogWarning($"[{spoutName}] Not found for deletion");
            }
        }
        
        private void SendRenderTextures()
        {
            if (spouts.Count == 0)
                return;
            
            foreach (var kvp in spouts)
            {
                var spoutName = kvp.Key;
                var spout = kvp.Value;
                
                try
                {
                    if (!spout.IsValid)
                    {
                        continue;
                    }
                    
                    var rtAsset = RenderingManager.Instance.RenderTextures.GetAsset(spout.AssetId);
                    if (rtAsset?.Texture == null)
                    {
                        if (spout.InitializationAttempts == 1)
                        {
                            Log.LogWarning($"[{spoutName}] RenderTexture not available for AssetId: {spout.AssetId}");
                        }
                        continue;
                    }
                    
                    // Shared Texture がまだない場合は作成を試みる
                    if (spout.SharedTexture == null)
                    {
                        if (spout.InitializationAttempts < 5 || spout.InitializationAttempts % 60 == 0)
                        {
                            Log.LogInfo($"[{spoutName}] Attempting to create shared texture (attempt {spout.InitializationAttempts})...");
                        }
                        
                        if (!spout.TryCreateSharedTexture())
                        {
                            continue;
                        }
                    }
                    
                    RenderTexture source = rtAsset.Texture;
                    
                    // Blit with vertical flip
                    var tempRt = RenderTexture.GetTemporary(
                        spout.SharedTexture.width, 
                        spout.SharedTexture.height,
                        0,
                        RenderTextureFormat.ARGB32);
                    
                    Graphics.Blit(source, tempRt, new Vector2(1.0f, -1.0f), new Vector2(0.0f, 1.0f));
                    Graphics.CopyTexture(tempRt, spout.SharedTexture);
                    RenderTexture.ReleaseTemporary(tempRt);
                }
                catch (Exception e)
                {
                    Log.LogError($"[{spoutName}] Error sending texture: {e}");
                }
            }
            
            // Update すべての Spout
            foreach (var spout in spouts.Values)
            {
                if (spout.IsReady)
                {
                    Util.IssuePluginEvent(PluginEntry.Event.Update, spout.SpoutSender);
                }
            }
        }
    }
}
// KlakSpout - Spout video frame sharing plugin for Unity
// https://github.com/keijiro/KlakSpout

using ResoniteSpoutRenderer;
using UnityEngine;
using UnityEngine.Rendering;

namespace ResoniteSpoutRenderer
{
    // Internal utilities
    static class Util
    {
        internal static void Destroy(UnityEngine.Object obj)
        {
            if (obj == null) return;

            if (Application.isPlaying)
                UnityEngine.Object.Destroy(obj);
            else
                UnityEngine.Object.DestroyImmediate(obj);
        }

        static CommandBuffer _commandBuffer;

        internal static void
            IssuePluginEvent(PluginEntry.Event pluginEvent, System.IntPtr ptr)
        {
            if (_commandBuffer == null) _commandBuffer = new CommandBuffer();

            _commandBuffer.IssuePluginEventAndData(
                PluginEntry.GetRenderEventFunc(), (int)pluginEvent, ptr
            );

            Graphics.ExecuteCommandBuffer(_commandBuffer);

            _commandBuffer.Clear();
        }
    }
}
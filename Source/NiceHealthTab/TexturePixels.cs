using UnityEngine;

namespace NiceHealthTabPatch
{
    /// <summary>
    /// Reads the pixels of game textures, which are not CPU-readable.
    /// </summary>
    internal static class TexturePixels
    {
        /// <summary>
        /// Copies the texture through a render texture and returns its pixels, bottom row first.
        /// </summary>
        public static Color32[] Read(Texture tex)
        {
            int w = tex.width, h = tex.height;
            RenderTexture rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
            RenderTexture previous = RenderTexture.active;
            Texture2D copy = new Texture2D(w, h, TextureFormat.RGBA32, false);
            try
            {
                Graphics.Blit(tex, rt);
                RenderTexture.active = rt;
                copy.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                copy.Apply();
                return copy.GetPixels32();
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
                Object.Destroy(copy);
            }
        }
    }
}

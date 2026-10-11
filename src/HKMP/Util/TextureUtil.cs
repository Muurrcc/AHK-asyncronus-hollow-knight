using System;
using UnityEngine;

namespace Hkmp.Util;

/// <summary>
/// Texture helpers that stay compilable against both the old (Unity 2020) and the Unity 6 build of the game.
/// </summary>
internal static class TextureUtil {
    /// <summary>
    /// Cached delegate to <c>ImageConversion.LoadImage(Texture2D, byte[], bool)</c>.
    /// Unity 6 adds <c>ReadOnlySpan&lt;byte&gt;</c> overloads, and calling <c>LoadImage</c> directly makes the
    /// compiler require <c>System.ReadOnlySpan`1</c>, which the net472 reference mscorlib does not have (CS0518).
    /// Binding the byte[] overload through reflection avoids overload resolution at compile time.
    /// </summary>
    private static readonly Func<Texture2D, byte[], bool, bool> LoadImageFunc =
        (Func<Texture2D, byte[], bool, bool>) Delegate.CreateDelegate(
            typeof(Func<Texture2D, byte[], bool, bool>),
            typeof(ImageConversion).GetMethod(
                "LoadImage",
                new[] { typeof(Texture2D), typeof(byte[]), typeof(bool) }
            )!
        );

    /// <summary>
    /// Load PNG/JPG bytes into the given texture.
    /// </summary>
    /// <param name="texture">The texture to load into.</param>
    /// <param name="data">The image bytes.</param>
    /// <param name="markNonReadable">Whether to mark the texture as non-readable after loading.</param>
    /// <returns>True if the image was loaded successfully.</returns>
    public static bool LoadImage(Texture2D texture, byte[] data, bool markNonReadable) {
        return LoadImageFunc(texture, data, markNonReadable);
    }
}

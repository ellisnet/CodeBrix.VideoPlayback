using System;
using System.Collections.Generic;
using CodeBrix.VideoPlayback.Decoding;
using CodeBrix.VideoPlayback.Frames;
using SkiaSharp;

namespace CodeBrix.VideoPlayback.Skia.Internal;

/// <summary>
/// Draws one decoded frame onto a surface with the colour shader: three planes in, colour-converted and
/// effect-graded pixels out, in a single pass.
/// </summary>
/// <remarks>
/// <para>
/// It works with or without a graphics context. WITH one, the planes are uploaded as single-channel textures
/// and the shader runs on the device - the shipping arrangement. WITHOUT one, the planes stay host images and
/// the shader runs on Skia's raster backend, which is slower than the core's vector converter and is
/// therefore never a render path - but it IS the same shader, the same uniforms and the same bindings, which
/// makes it exactly what a test needs on a machine with no display.
/// </para>
/// <para>The compiled shaders are cached, so a steady stream of frames compiles nothing.</para>
/// </remarks>
internal sealed class YuvSurfaceRenderer : IDisposable
{
    private static readonly SKSamplingOptions LumaSampling =
        new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None);

    private static readonly SKSamplingOptions SmoothSampling =
        new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);

    private readonly List<SKImage> images = new List<SKImage>(6);

    private SKRuntimeEffect plainEffect;
    private SKRuntimeEffect lookupEffect;

    /// <summary>Draws a frame onto a surface.</summary>
    /// <param name="frame">The frame to draw.</param>
    /// <param name="surface">The surface to draw onto, which must be the frame's coded size.</param>
    /// <param name="graphicsContext">
    /// The context whose textures the planes should be uploaded to, or null to run on the raster backend
    /// with host images.
    /// </param>
    /// <param name="lookupAtlas">The composed effect atlas, or null when there is no effect chain.</param>
    /// <param name="lookupSize">The number of nodes a side of the atlas, ignored when there is no atlas.</param>
    /// <exception cref="ArgumentNullException"><paramref name="frame" /> or <paramref name="surface" /> is null.</exception>
    /// <exception cref="CodeBrix.VideoPlayback.VideoPlaybackException">
    /// A shader would not compile, a plane would not upload, or the backend refused the shader.
    /// </exception>
    internal void Render(
        VideoFrame frame,
        SKSurface surface,
        GRContext graphicsContext,
        SKImage lookupAtlas,
        int lookupSize)
    {
        if (frame == null) throw new ArgumentNullException(nameof(frame));
        if (surface == null) throw new ArgumentNullException(nameof(surface));

        images.Clear();

        try
        {
            bool monochrome = frame.Layout == VideoPixelLayout.Gray || frame.U.IsEmpty || frame.V.IsEmpty;
            VideoColorInfo resolved = frame.Color.Resolve(frame.Height);
            YuvShaderUniforms numbers =
                YuvShaderUniforms.Create(resolved, frame.BitDepth, frame.Layout, monochrome);

            SKImage luma = PreparePlane(frame.Y, "luma", graphicsContext);
            SKImage blueChroma = monochrome ? luma : PreparePlane(frame.U, "first chroma", graphicsContext);
            SKImage redChroma = monochrome ? luma : PreparePlane(frame.V, "second chroma", graphicsContext);

            bool useLookup = lookupAtlas != null;
            SKRuntimeEffect effect = useLookup ? EnsureLookupEffect() : EnsurePlainEffect();

            using SKShader lumaShader =
                luma.ToRawShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, LumaSampling);
            using SKShader blueShader =
                blueChroma.ToRawShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, SmoothSampling);
            using SKShader redShader =
                redChroma.ToRawShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, SmoothSampling);
            using SKShader lookupShader = useLookup
                ? lookupAtlas.ToRawShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, SmoothSampling)
                : null;

            using SKRuntimeEffectUniforms uniforms = new SKRuntimeEffectUniforms(effect);
            uniforms.Add("chromaShift", new[] { numbers.ChromaShiftX, numbers.ChromaShiftY });
            uniforms.Add("chromaCosited", new[] { numbers.ChromaCositedX, numbers.ChromaCositedY });
            uniforms.Add("planeMaximum", numbers.PlaneMaximum);
            uniforms.Add("sampleOffsets", new[] { numbers.LumaOffset, numbers.ChromaOffset, numbers.ChromaOffset });
            uniforms.Add("redRow", numbers.RedRow);
            uniforms.Add("greenRow", numbers.GreenRow);
            uniforms.Add("blueRow", numbers.BlueRow);
            if (useLookup) uniforms.Add("lookupSize", (float)lookupSize);

            using SKRuntimeEffectChildren children = new SKRuntimeEffectChildren(effect);
            children.Add(YuvShaderSource.LumaChild, new SKRuntimeEffectChild(lumaShader));
            children.Add(YuvShaderSource.ChromaBlueChild, new SKRuntimeEffectChild(blueShader));
            children.Add(YuvShaderSource.ChromaRedChild, new SKRuntimeEffectChild(redShader));
            if (useLookup) children.Add(YuvShaderSource.LookupChild, new SKRuntimeEffectChild(lookupShader));

            using SKShader shader = effect.ToShader(uniforms, children);
            if (shader == null)
            {
                throw new VideoPlaybackException(
                    "SkiaSharp would not build the colour shader from its uniforms and planes. The graphics "
                    + "backend in use may not support runtime effects; set RenderPath to Cpu to use the "
                    + "processor path instead.");
            }

            using SKPaint paint = new SKPaint
            {
                Shader = shader,
                IsAntialias = false,
                BlendMode = SKBlendMode.Src,
            };

            surface.Canvas.DrawRect(SKRect.Create(0f, 0f, frame.Width, frame.Height), paint);
            surface.Flush();

            if (graphicsContext != null)
            {
                graphicsContext.Flush();
                graphicsContext.Submit(false);
            }
        }
        finally
        {
            for (int i = 0; i < images.Count; i++) images[i].Dispose();
            images.Clear();
        }
    }

    /// <summary>Releases the compiled shaders.</summary>
    public void Dispose()
    {
        plainEffect?.Dispose();
        plainEffect = null;
        lookupEffect?.Dispose();
        lookupEffect = null;
    }

    private SKImage PreparePlane(in VideoFramePlane plane, string which, GRContext graphicsContext)
    {
        SKColorType type = plane.BytesPerSample >= 2 ? SKColorType.R16Unorm : SKColorType.R8Unorm;
        SKImageInfo info = new SKImageInfo(plane.Width, plane.Height, type, SKAlphaType.Opaque);

        SKImage host;
        using (SKPixmap pixmap = new SKPixmap(info, plane.Data, plane.Stride))
        {
            // FromPixels BORROWS the memory rather than copying it: the samples the decoder wrote go to the
            // driver exactly where they already are, which is the whole point of the pinned pool.
            host = SKImage.FromPixels(pixmap);
        }

        if (host == null)
        {
            throw new VideoPlaybackException(
                $"SkiaSharp would not wrap this frame's {which} plane ({plane.Width}x{plane.Height}, "
                + $"{plane.BytesPerSample * 8}-bit samples, stride {plane.Stride}) as an image, so it cannot "
                + "be drawn. Set RenderPath to Cpu to use the processor path instead.");
        }

        images.Add(host);
        if (graphicsContext == null) return host;

        SKImage texture = host.ToTextureImage(graphicsContext);
        if (texture == null)
        {
            throw new VideoPlaybackException(
                $"The graphics context would not accept this frame's {which} plane as a "
                + $"{(plane.BytesPerSample >= 2 ? "16" : "8")}-bit single-channel texture "
                + $"({plane.Width}x{plane.Height}). The backend may not support that texture format; set "
                + "RenderPath to Cpu to use the processor path instead.");
        }

        images.Add(texture);
        return texture;
    }

    private SKRuntimeEffect EnsurePlainEffect() => plainEffect ??= Compile(false);

    private SKRuntimeEffect EnsureLookupEffect() => lookupEffect ??= Compile(true);

    private static SKRuntimeEffect Compile(bool withLookupTable)
    {
        SKRuntimeEffect effect = SKRuntimeEffect.CreateShader(
            YuvShaderSource.Build(withLookupTable),
            out string errors);

        if (effect == null)
        {
            throw new VideoPlaybackException(
                "SkiaSharp would not compile the colour shader"
                + (withLookupTable ? " with its lookup-table stage" : string.Empty)
                + $": {errors}. Set RenderPath to Cpu to use the processor path instead.");
        }

        return effect;
    }
}

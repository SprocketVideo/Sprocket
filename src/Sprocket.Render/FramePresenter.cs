using SkiaSharp;

namespace Sprocket.Render;

/// <summary>
/// Draws one decoded RGBA8888 video frame onto an <see cref="SKCanvas"/>, scaled to fit (letterboxed,
/// aspect preserved) into a target rectangle. This is the slice's preview presentation step
/// (PLAN.md step 4, ARCHITECTURE.md §10): the pixels stay in their native buffer and are wrapped by
/// <see cref="SKImage.FromPixels(SKImageInfo, nint, int)"/> — no managed copy (§1) — and the draw uploads
/// them to the GPU when the canvas is GPU-backed (the Avalonia shared <c>GRContext</c>).
/// </summary>
/// <remarks>
/// The caller is responsible for the frame buffer's lifetime: the native pixels must remain valid for the
/// duration of <see cref="Present"/> (the playback engine holds the frame under a lock while drawing).
/// When multiple tracks/effects arrive (PLAN steps 7/14) this is replaced by routing the resolved
/// <c>VideoFramePlan</c> through an <c>IVideoCompositor&lt;SKImage&gt;</c>; for one opaque video layer a
/// direct fit-draw is equivalent and keeps the hot loop allocation-clean.
/// </remarks>
public static class FramePresenter
{
    /// <summary>
    /// Clears <paramref name="bounds"/> to <paramref name="background"/> and draws the
    /// <paramref name="width"/>×<paramref name="height"/> RGBA8888 frame at <paramref name="pixels"/>
    /// (stride <paramref name="rowBytes"/>) centred and scaled to fit, preserving aspect ratio.
    /// Does nothing if the bounds or frame are degenerate.
    /// </summary>
    public static void Present(
        SKCanvas canvas,
        SKRect bounds,
        nint pixels,
        int rowBytes,
        int width,
        int height,
        SKColor background)
    {
        ArgumentNullException.ThrowIfNull(canvas);

        canvas.Clear(background);

        if (pixels == 0 || width <= 0 || height <= 0 || bounds.Width <= 0 || bounds.Height <= 0)
            return;

        SKRect dest = ComputeFitRect(bounds, width, height);

        // Video frames are opaque RGBA8888 (swscale output, ARCHITECTURE.md §11). FromPixels references
        // the native buffer without copying; DrawImage uploads to the GPU on a GPU-backed canvas.
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using SKImage image = SKImage.FromPixels(info, pixels, rowBytes);
        canvas.DrawImage(image, dest, new SKSamplingOptions(SKFilterMode.Linear));
    }

    /// <summary>
    /// Computes the largest rectangle of aspect ratio <paramref name="width"/>:<paramref name="height"/>
    /// that fits inside <paramref name="bounds"/>, centred (the "fit"/letterbox rectangle). Pure — exposed
    /// so the scaling math is unit-testable without a canvas.
    /// </summary>
    public static SKRect ComputeFitRect(SKRect bounds, int width, int height)
    {
        if (width <= 0 || height <= 0 || bounds.Width <= 0 || bounds.Height <= 0)
            return SKRect.Empty;

        float scale = Math.Min(bounds.Width / width, bounds.Height / height);
        float w = width * scale;
        float h = height * scale;
        float x = bounds.Left + (bounds.Width - w) / 2f;
        float y = bounds.Top + (bounds.Height - h) / 2f;
        return SKRect.Create(x, y, w, h);
    }
}

namespace Sprocket.Core.Model;

/// <summary>
/// The one-tap Toy Cassette Camera looks (plan/features/toy-cassette-camera.md, phases 5–6): each pairs Posterize Time
/// at the camera's ~15 fps, an Echo highlight trail and the Toy Cassette Camera stage's preset of the same name on the
/// picture with the Cassette preset of the same name on the linked sound. The names are generic (the B&amp;W film-preset
/// trademark rule); the brand appears only in the descriptions. Applied through <see cref="PresetStackApplication.Build"/>.
/// </summary>
/// <remarks>
/// The camera's sensor lag — bright moving things leaving comet-like trails — is a true <em>temporal</em> smear here
/// (phase 6): Echo with the Maximum operator over the last few 15 fps steps, keyed to highlights only, decaying fast.
/// It sits directly on the source (first shader stage after Posterize Time), so its echoes cost one texture read each
/// and the toycam stage then pixelates the trails with the picture. The stage's own spatial smear is switched off
/// (Smear Length 0) so the two don't double up; it stays available for anyone using the stage without Echo.
/// </remarks>
public static class ToyCassetteCameraStacks
{
    /// <summary>The Effects-browser group every stack here is listed under.</summary>
    public const string Group = "Toy Cassette Camera";

    /// <summary>The camera's picture rate — the frame rate Posterize Time is set to.</summary>
    public const double FrameRate = 15.0;

    /// <summary>The spacing of the highlight-trail echoes: one camera frame (Echo Time = −1 / <see cref="FrameRate"/>).</summary>
    public const double EchoTime = -1.0 / FrameRate;

    /// <summary>Only pixels brighter than this leave a trail (Echo's Highlight Key).</summary>
    public const double HighlightKey = 0.8;

    /// <summary>A camera in good order on fresh tape.</summary>
    public static PresetStack Clean { get; } = Create(ToyCamPresets.Clean.Name, CassettePresets.Clean.Name, echoes: 3, decay: 0.5,
        "Inspired by the Fisher-Price PXL 2000: a well-kept camera on fresh tape — blocky black-and-white pixels at 15 fps, the black border, narrow mono sound with a faint hiss.");

    /// <summary>A tape played to death.</summary>
    public static PresetStack WornTape { get; } = Create(ToyCamPresets.WornTape.Name, CassettePresets.WornTape.Name, echoes: 3, decay: 0.5,
        "Inspired by the Fisher-Price PXL 2000: a cassette replayed too often — noise lines and dropouts at 15 fps, dull hissy sound with a seasick wobble.");

    /// <summary>A dim room or dusk — the sensor lags most, so lights trail longest.</summary>
    public static PresetStack LowLight { get; } = Create(ToyCamPresets.LowLight.Name, CassettePresets.LowLight.Name, echoes: 4, decay: 0.6,
        "Inspired by the Fisher-Price PXL 2000: shooting in a dim room — crushed shadows, shimmer and trailing lights at 15 fps, with the automatic gain pumping up the hiss.");

    /// <summary>All stacks, in browser order.</summary>
    public static IReadOnlyList<PresetStack> All { get; } = [Clean, WornTape, LowLight];

    private static PresetStack Create(string videoPreset, string audioPreset, int echoes, double decay, string description) => new(
        videoPreset,
        Group,
        description,
        [
            // Posterize Time first: it retimes the source (a time modifier, filtered out of the shader chain), so it
            // reads naturally at the head of the stack, after any input color transform.
            new PresetStackEntry(EffectTypeIds.PosterizeTime, null,
                new Dictionary<string, double> { [EffectParamNames.PosterizeFrameRate] = FrameRate },
                PresetStackPlacement.Front),
            // Echo next (front entries keep their order), so it is the first real shader stage — directly on the source,
            // where re-applying the chain below it to each echo costs nothing. Maximum + a highlight key = bright-only
            // trails that never darken; one echo per camera frame, fading fast.
            new PresetStackEntry(EffectTypeIds.Echo, null,
                new Dictionary<string, double>
                {
                    [EffectParamNames.EchoTime] = EchoTime,
                    [EffectParamNames.EchoCount] = echoes,
                    [EffectParamNames.StartingIntensity] = 1.0,
                    [EffectParamNames.Decay] = decay,
                    [EffectParamNames.EchoOperator] = EchoOperators.Maximum,
                    [EffectParamNames.HighlightKey] = HighlightKey,
                },
                PresetStackPlacement.Front),
            // The stage's spatial smear is the stand-in for Echo's true temporal one — off here so they don't double up.
            new PresetStackEntry(EffectTypeIds.ToyCam, videoPreset,
                new Dictionary<string, double> { [EffectParamNames.SmearLength] = 0.0 }),
        ],
        [
            new PresetStackEntry(EffectTypeIds.AudioCassette, audioPreset),
        ]);
}

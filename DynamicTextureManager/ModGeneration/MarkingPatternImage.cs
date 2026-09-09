namespace DynamicTextureManager.ModGeneration;

/// <summary>
/// A custom markings image resolved by the compositor: intensity = luminance × alpha, 0..1.
/// The stamp (source file write time) keys the bake cache, so edits on disk regenerate.
/// </summary>
public sealed record MarkingPatternImage(long Stamp, float[] Intensity, int Width, int Height);

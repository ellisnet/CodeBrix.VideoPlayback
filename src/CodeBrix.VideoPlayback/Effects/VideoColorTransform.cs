namespace CodeBrix.VideoPlayback.Effects;

/// <summary>
/// A colour change expressed as a function of one colour - the general form an effect is reduced to when the
/// chain is composed.
/// </summary>
/// <param name="red">The red component on the way in, and the transformed red on the way out. 0 to 1.</param>
/// <param name="green">The green component on the way in, and the transformed green on the way out. 0 to 1.</param>
/// <param name="blue">The blue component on the way in, and the transformed blue on the way out. 0 to 1.</param>
/// <remarks>
/// It is called once per node of the composition grid - a few tens of thousands of times when the chain
/// changes, and never per pixel - so it may be as expensive as it likes.
/// </remarks>
public delegate void VideoColorTransform(ref float red, ref float green, ref float blue);

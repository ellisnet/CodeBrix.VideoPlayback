namespace CodeBrix.VideoPlayback.Authoring.Internal;

/// <summary>
/// The band of bit rates libvorbis will open at - the lowest AND the highest - for each sample rate and
/// channel count, MEASURED, not assumed.
/// </summary>
/// <remarks>
/// <para>
/// libvorbis's rate management is driven by a table of setup templates chosen by sample rate and channel
/// count, and each template declares the band of bit rates it can honour. Ask for a bit rate outside that
/// band and the encoder refuses at SETUP - <c>encoder setup failed</c>, before a single frame is read - so a
/// request that names one is worth refusing here, where the message can say what to do about it, rather than
/// part-way through an encode in FFmpeg's words.
/// </para>
/// <para>
/// EVERY NUMBER BELOW WAS MEASURED. On 2026-09-01, against the FFmpeg 7.1.5 build on the machine that wrote
/// this file, every bit rate this library can name (6 to 512 kbit/s) was offered to libvorbis at every
/// sample rate and channel count in the table, and the accepted band was recorded. Every band came out
/// CONTIGUOUS: every value from the floor to the ceiling opened and every value outside it failed. Three of
/// the results are worth stating out loud because the guesses in circulation are wrong: 48 kHz STEREO opens
/// from 45 kbit/s, not the 64 or 96 that this repository's own notes used to claim; SIX channels at 44.1 and
/// 48 kHz open LOWER than five, because libvorbis has a coupled 5.1 setup that the odd channel counts do not
/// get; and below 16 kHz the band has a CEILING low enough for this library's own maximum of 512 kbit/s to
/// be refused outright. A formula would have got all three wrong, so there is no formula here - only the
/// table.
/// </para>
/// <para>
/// ABOVE 48 kHz THE BIT-RATE MODE BARELY OPENS AT ALL, and that too is measured rather than inferred. At
/// 64 kHz only the coupled six-channel setup opens, from 84 kbit/s up; at 88.2, 96, 176.4 and 192 kHz NOTHING
/// opens, at any channel count, at any bit rate in the range - 4,056 attempts per rate, all refused. Those
/// combinations are recorded here as a band of ZERO, which
/// <see cref="TryGetBand" /> reports as "measured, and nothing opens", so the caller can refuse the request
/// up front and point at the quality path, which does open at every rate and channel count tried.
/// </para>
/// <para>
/// THE TABLE IS DELIBERATELY NOT INTERPOLATED. A sample rate that was not measured has no band here and is
/// not checked at all: the floors and ceilings do not move smoothly with the rate, so a value guessed
/// between two rows could refuse a request libvorbis would have accepted, which is the one mistake this
/// check must never make. The rates in the table are the rates real audio is recorded at, and 48 kHz - this
/// library's default - is one of them.
/// </para>
/// <para>
/// NONE OF THIS TOUCHES THE QUALITY PATH. <c>-q:a</c> has neither a floor nor a ceiling and opened
/// everywhere, at every rate and channel count in the sweep, which is what makes "set VorbisQuality instead"
/// correct advice in every message these numbers produce. Opus is held to none of it.
/// </para>
/// </remarks>
internal static class VorbisBitrateBands
{
    // One row per measured sample rate: the rate, then the FLOOR and the CEILING in kilobits per second for
    // 1 to 8 channels, in that order - seventeen numbers a row. A floor of 0 means "measured, and libvorbis
    // accepted no bit rate at all for that combination"; its ceiling is 0 too.
    private static readonly int[][] Bands =
    {
        //          ch1        ch2        ch3        ch4         ch5         ch6         ch7         ch8
        new[] { 8000, 8, 42, 12, 84, 24, 126, 32, 168, 40, 210, 48, 252, 56, 294, 64, 336 },
        new[] { 11025, 12, 50, 16, 100, 36, 150, 48, 200, 60, 250, 72, 300, 84, 350, 96, 400 },
        new[] { 12000, 12, 50, 16, 100, 36, 150, 48, 200, 60, 250, 72, 300, 84, 350, 96, 400 },
        new[] { 16000, 16, 100, 24, 200, 48, 300, 64, 400, 80, 500, 96, 512, 112, 512, 128, 512 },
        new[] { 22050, 16, 90, 30, 180, 48, 270, 64, 360, 80, 450, 96, 512, 112, 512, 128, 512 },
        new[] { 24000, 16, 90, 30, 180, 48, 270, 64, 360, 80, 450, 96, 512, 112, 512, 128, 512 },
        new[] { 32000, 30, 190, 36, 380, 90, 512, 120, 512, 150, 512, 180, 512, 210, 512, 240, 512 },
        new[] { 44100, 32, 240, 45, 500, 96, 512, 128, 512, 160, 512, 84, 512, 224, 512, 256, 512 },
        new[] { 48000, 32, 240, 45, 500, 96, 512, 128, 512, 160, 512, 84, 512, 224, 512, 256, 512 },

        // Above 48 kHz. The only band that opens anywhere up here is 64 kHz in six channels.
        new[] { 64000, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 84, 512, 0, 0, 0, 0 },
        new[] { 88200, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
        new[] { 96000, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
        new[] { 176400, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
        new[] { 192000, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
    };

    /// <summary>
    /// Finds the band of bit rates libvorbis accepts for one sample rate and channel count.
    /// </summary>
    /// <param name="sampleRateHz">The sample rate the encoder will be asked for.</param>
    /// <param name="channels">The channel count the encoder will be asked for.</param>
    /// <param name="floorKilobitsPerSecond">
    /// The lowest bit rate that opens, or 0 when the combination was measured and NOTHING opens.
    /// </param>
    /// <param name="ceilingKilobitsPerSecond">
    /// The highest bit rate that opens, or 0 when the combination was measured and nothing opens.
    /// </param>
    /// <returns>
    /// True when this combination was MEASURED - which is not the same as "something opens": a true return
    /// with a floor of 0 means libvorbis's bit-rate mode does not open there at all. False means the
    /// combination was never measured and nothing should be refused on its account.
    /// </returns>
    internal static bool TryGetBand(
        int sampleRateHz,
        int channels,
        out int floorKilobitsPerSecond,
        out int ceilingKilobitsPerSecond)
    {
        floorKilobitsPerSecond = 0;
        ceilingKilobitsPerSecond = 0;

        if (channels < 1 || channels > 8) return false;

        foreach (int[] row in Bands)
        {
            if (row[0] != sampleRateHz) continue;

            floorKilobitsPerSecond = row[(channels * 2) - 1];
            ceilingKilobitsPerSecond = row[channels * 2];
            return true;
        }

        return false;
    }
}

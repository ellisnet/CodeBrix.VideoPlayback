namespace CodeBrix.VideoPlayback.Containers.Ebml;

/// <summary>
/// The element identifiers RFC 8794 defines for every EBML document, whatever it carries.
/// </summary>
/// <remarks>
/// These are the generic ones. The identifiers that mean something only inside a Matroska document live in
/// <see cref="CodeBrix.VideoPlayback.Containers.Matroska.MatroskaIds" />.
/// </remarks>
public static class EbmlIds
{
    /// <summary>The EBML header that begins every document. The first four bytes of the file.</summary>
    public const uint EbmlHeader = 0x1A45DFA3;

    /// <summary>The version of EBML the writer used.</summary>
    public const uint EbmlVersion = 0x4286;

    /// <summary>The minimum EBML version a reader needs.</summary>
    public const uint EbmlReadVersion = 0x42F7;

    /// <summary>The longest element identifier the document uses, in bytes.</summary>
    public const uint EbmlMaxIdLength = 0x42F2;

    /// <summary>The longest element size field the document uses, in bytes.</summary>
    public const uint EbmlMaxSizeLength = 0x42F3;

    /// <summary>What kind of document this is - "matroska" or "webm" for the files this library reads.</summary>
    public const uint DocType = 0x4282;

    /// <summary>The version of the document type the writer used.</summary>
    public const uint DocTypeVersion = 0x4287;

    /// <summary>The minimum document-type version a reader needs.</summary>
    public const uint DocTypeReadVersion = 0x4285;

    /// <summary>Padding. Its payload means nothing and is always skipped.</summary>
    public const uint Void = 0xEC;

    /// <summary>
    /// A checksum over the rest of its parent's payload. By rule it is the parent's FIRST child.
    /// </summary>
    public const uint Crc32 = 0xBF;
}

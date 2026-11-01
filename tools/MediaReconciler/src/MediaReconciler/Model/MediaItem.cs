namespace MediaReconciler.Model;

public enum MediaKind
{
    Photo,
    Video,
}

/// <summary>
/// One photo or video as seen in one source (an iCloud mirror folder, a Dropbox account,
/// a Google Takeout export, an iPhone...). Items from different sources that are the same
/// picture get grouped together by the <see cref="Matching.Matcher"/>.
/// </summary>
public sealed record MediaItem
{
    /// <summary>Index into the configured source list.</summary>
    public required int SourceIndex { get; init; }

    /// <summary>Where the item lives inside its source: a path, a Dropbox path, "zip!entry"...</summary>
    public required string Location { get; init; }

    public required string FileName { get; init; }

    public required MediaKind Kind { get; init; }

    /// <summary>Size in bytes, or -1 when the source can't tell us.</summary>
    public long Size { get; init; } = -1;

    /// <summary>
    /// Dropbox-style content hash (hex). Dropbox reports this for every file, and we compute
    /// the same algorithm locally, so one hash compares across every source.
    /// </summary>
    public string? ContentHash { get; init; }

    /// <summary>Capture time as a UTC instant, when known.</summary>
    public DateTime? TakenUtc { get; init; }

    /// <summary>True when <see cref="TakenUtc"/> came from a file timestamp, not from photo metadata.</summary>
    public bool UtcIsFileTime { get; init; }

    /// <summary>Capture time as camera wall-clock time (no time zone), when known.</summary>
    public DateTime? TakenLocal { get; init; }

    /// <summary>A path on this PC you can open the file from, when there is one.</summary>
    public string? LocalPath { get; init; }

    public string Stem => MediaNames.NormalizeStem(FileName);

    public string Extension => MediaNames.NormalizeExtension(FileName);

    /// <summary>
    /// False when the name was generated from the date (Dropbox camera uploads, Android),
    /// so it tells us nothing about which camera file it originally was.
    /// </summary>
    public bool HasOriginalName => !MediaNames.IsDateDerivedName(FileName);
}

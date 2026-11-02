namespace Bandichan.Core;

public sealed record CodecPreference(string VideoCodec, string AudioCodec, string Container)
{
    public static CodecPreference Default { get; } = new("libx264", "aac", "mp4");
}

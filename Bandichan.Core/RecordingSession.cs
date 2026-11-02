namespace Bandichan.Core;

public sealed record RecordingSession(string OutputPath, CodecPreference Codec, bool AudioEnabled);

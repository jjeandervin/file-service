namespace FileService.Api.Application;

// Leading-byte signatures for normalized (lowercase) content types. Types without a known signature always pass.
public static class ContentSignatures
{
    public const int PrefixLength = 16;

    public static bool IsKnown(string contentType) =>
        contentType is "image/jpeg" or "image/png" or "image/webp";

    public static bool Matches(string contentType, ReadOnlySpan<byte> prefix) => contentType switch
    {
        "image/jpeg" => prefix.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]),
        "image/png" => prefix.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]),
        "image/webp" => prefix.Length >= 12 && prefix.StartsWith("RIFF"u8) && prefix[8..12].SequenceEqual("WEBP"u8),
        _ => true
    };
}

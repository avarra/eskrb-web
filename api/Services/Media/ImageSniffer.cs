namespace Eskrb.Api.Services.Media;

/// <summary>Identifies an image by its first bytes. The client-sent Content-Type is never trusted.</summary>
public static class ImageSniffer
{
    public static (string ContentType, string Extension)? Detect(ReadOnlySpan<byte> h)
    {
        if (h.Length >= 3 && h[0] == 0xFF && h[1] == 0xD8 && h[2] == 0xFF) return ("image/jpeg", ".jpg");
        if (h.Length >= 8 && h[0] == 0x89 && h[1] == 0x50 && h[2] == 0x4E && h[3] == 0x47
            && h[4] == 0x0D && h[5] == 0x0A && h[6] == 0x1A && h[7] == 0x0A) return ("image/png", ".png");
        if (h.Length >= 12 && h[..4].SequenceEqual("RIFF"u8) && h[8..12].SequenceEqual("WEBP"u8)) return ("image/webp", ".webp");
        if (h.Length >= 12 && h[4..8].SequenceEqual("ftyp"u8))
        {
            var brand = System.Text.Encoding.ASCII.GetString(h[8..12]);
            if (brand is "heic" or "heix" or "hevc" or "hevx" or "mif1" or "msf1" or "heim" or "heis")
                return ("image/heic", ".heic");
            if (brand is "avif" or "avis") return ("image/avif", ".avif");
        }
        return null;
    }
}

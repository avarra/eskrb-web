using Eskrb.Api.Rules;
using Eskrb.Api.Services.Media;

namespace Eskrb.Api.Tests;

public class QuotesTests
{
    [Fact] public void Uses_the_quote_when_it_occurs_in_the_text() => Assert.Equal("boli me meča", Quotes.Resolve("Že dva dni boli me meča.", "boli me meča"));
    [Fact] public void Falls_back_to_the_whole_text_when_the_quote_is_not_in_it() => Assert.Equal("Boli me noga.", Quotes.Resolve("Boli me noga.", "invented"));
    [Fact] public void Falls_back_when_the_quote_is_empty() => Assert.Equal("Boli me noga.", Quotes.Resolve("Boli me noga.", "  "));
    [Fact] public void Falls_back_when_the_quote_is_null() => Assert.Equal("Boli me noga.", Quotes.Resolve("Boli me noga.", null));
}

public class ImageSnifferTests
{
    [Fact] public void Jpeg() => Assert.Equal(("image/jpeg", ".jpg"), ImageSniffer.Detect([0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0]));
    [Fact] public void Png() => Assert.Equal(("image/png", ".png"), ImageSniffer.Detect([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]));
    [Fact] public void Webp() => Assert.Equal(("image/webp", ".webp"), ImageSniffer.Detect("RIFF\0\0\0\0WEBPVP8 "u8));
    [Fact] public void Heic() => Assert.Equal(("image/heic", ".heic"), ImageSniffer.Detect("\0\0\0\u0018ftypheic\0\0\0\0"u8));
    [Fact] public void Avif() => Assert.Equal(("image/avif", ".avif"), ImageSniffer.Detect("\0\0\0\u0018ftypavif\0\0\0\0"u8));
    [Fact] public void Html_is_not_an_image() => Assert.Null(ImageSniffer.Detect("<html></html>"u8));
    [Fact] public void Wav_is_not_an_image() => Assert.Null(ImageSniffer.Detect("RIFF\0\0\0\0WAVEfmt "u8));
    [Fact] public void Empty_is_not_an_image() => Assert.Null(ImageSniffer.Detect([]));
}

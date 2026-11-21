namespace Eskrb.Api.Services.Media;

/// <summary>Where photo bytes live. Callers only ever see opaque keys, never paths, so a bucket can replace the disk.</summary>
public interface IMediaStore
{
    Task<string> SaveAsync(Stream content, string extension, CancellationToken ct);
    Stream Open(string key);
}

public class DiskMediaStore : IMediaStore
{
    private static readonly System.Text.RegularExpressions.Regex ValidKey = new(@"^[0-9a-f]{32}\.[a-z0-9]{2,5}$");
    private readonly string _root;

    public DiskMediaStore(IConfiguration config)
    {
        _root = Path.GetFullPath(config["Media:Path"] ?? "data/media");
        Directory.CreateDirectory(_root);
    }

    public async Task<string> SaveAsync(Stream content, string extension, CancellationToken ct)
    {
        var key = Guid.NewGuid().ToString("N") + extension;
        await using var file = File.Create(Path.Combine(_root, key));
        await content.CopyToAsync(file, ct);
        return key;
    }

    public Stream Open(string key)
    {
        if (!ValidKey.IsMatch(key)) throw new FileNotFoundException();   // blocks path traversal
        return File.OpenRead(Path.Combine(_root, key));
    }
}

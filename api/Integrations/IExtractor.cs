using System.Text.Json;
using Eskrb.Api.Domain;

namespace Eskrb.Api.Integrations;

/// <summary>A value found in free text for a protocol question, plus the exact words it came from.</summary>
public record ExtractedValue(JsonElement Value, string Quote);

/// <summary>
/// Turns the patient's confirmed free text into values for protocol question ids. It only FILLS FIELDS;
/// flags are decided by RulesEngine, never by this. Output is validated by the caller.
/// </summary>
public interface IExtractor
{
    Task<Dictionary<string, ExtractedValue>> ExtractAsync(Protocol protocol, string text, CancellationToken ct);
}

/// <summary>Placeholder until an LLM provider is chosen: extracts nothing, so text logs raise no flags.</summary>
public class NullExtractor : IExtractor
{
    public Task<Dictionary<string, ExtractedValue>> ExtractAsync(Protocol protocol, string text, CancellationToken ct) =>
        Task.FromResult(new Dictionary<string, ExtractedValue>());
}

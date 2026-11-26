using System.Text.Json.Serialization;
using Newtonsoft.Json;

namespace RepairPlanner.Models;

/// <summary>
/// One ordered, actionable step of a repair plan.
/// </summary>
public sealed class RepairTask
{
    [JsonPropertyName("sequence")]
    [JsonProperty("sequence")]
    public int Sequence { get; set; }

    [JsonPropertyName("title")]
    [JsonProperty("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    [JsonProperty("description")]
    public string Description { get; set; } = string.Empty;

    // Integer minutes (e.g. 90), never a string like "90 minutes"
    [JsonPropertyName("estimatedDurationMinutes")]
    [JsonProperty("estimatedDurationMinutes")]
    public int EstimatedDurationMinutes { get; set; }

    [JsonPropertyName("requiredSkills")]
    [JsonProperty("requiredSkills")]
    public List<string> RequiredSkills { get; set; } = new();

    [JsonPropertyName("safetyNotes")]
    [JsonProperty("safetyNotes")]
    public string? SafetyNotes { get; set; }
}

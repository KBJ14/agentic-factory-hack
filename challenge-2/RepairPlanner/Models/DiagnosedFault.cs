using System.Text.Json.Serialization;
using Newtonsoft.Json;

namespace RepairPlanner.Models;

/// <summary>
/// Input from the Fault Diagnosis Agent (Challenge 1).
/// Shape: { MachineId, FaultType, RootCause, Severity, DetectedAt, Metadata }.
/// </summary>
public sealed class DiagnosedFault
{
    [JsonPropertyName("machineId")]
    [JsonProperty("machineId")]
    public string MachineId { get; set; } = string.Empty;

    [JsonPropertyName("faultType")]
    [JsonProperty("faultType")]
    public string FaultType { get; set; } = string.Empty;

    [JsonPropertyName("rootCause")]
    [JsonProperty("rootCause")]
    public string RootCause { get; set; } = string.Empty;

    // "Low" | "Medium" | "High" | "Critical" | "Unknown"
    [JsonPropertyName("severity")]
    [JsonProperty("severity")]
    public string Severity { get; set; } = "Unknown";

    [JsonPropertyName("detectedAt")]
    [JsonProperty("detectedAt")]
    public DateTimeOffset DetectedAt { get; set; } = DateTimeOffset.UtcNow;

    // Free-form supporting details (observed values, thresholds, KB references, ...)
    [JsonPropertyName("metadata")]
    [JsonProperty("metadata")]
    public Dictionary<string, object?> Metadata { get; set; } = new();
}

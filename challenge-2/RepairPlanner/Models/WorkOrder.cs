using System.Text.Json.Serialization;
using Newtonsoft.Json;

namespace RepairPlanner.Models;

/// <summary>
/// Work order produced by the Repair Planner Agent and saved to the
/// Cosmos DB "WorkOrders" container (partition key: /status).
/// </summary>
public sealed class WorkOrder
{
    [JsonPropertyName("id")]
    [JsonProperty("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("workOrderNumber")]
    [JsonProperty("workOrderNumber")]
    public string WorkOrderNumber { get; set; } = string.Empty;

    [JsonPropertyName("machineId")]
    [JsonProperty("machineId")]
    public string MachineId { get; set; } = string.Empty;

    [JsonPropertyName("faultType")]
    [JsonProperty("faultType")]
    public string? FaultType { get; set; }

    [JsonPropertyName("title")]
    [JsonProperty("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    [JsonProperty("description")]
    public string Description { get; set; } = string.Empty;

    // "corrective" | "preventive" | "emergency"
    [JsonPropertyName("type")]
    [JsonProperty("type")]
    public string? Type { get; set; }

    // "critical" | "high" | "medium" | "low"
    [JsonPropertyName("priority")]
    [JsonProperty("priority")]
    public string? Priority { get; set; }

    // Partition key in Cosmos DB, so it must always have a value (e.g. "new")
    [JsonPropertyName("status")]
    [JsonProperty("status")]
    public string? Status { get; set; }

    // Technician id (e.g. "tech-001") or null if nobody is available
    [JsonPropertyName("assignedTo")]
    [JsonProperty("assignedTo")]
    public string? AssignedTo { get; set; }

    [JsonPropertyName("createdDate")]
    [JsonProperty("createdDate")]
    public DateTimeOffset CreatedDate { get; set; } = DateTimeOffset.UtcNow;

    // Integer minutes
    [JsonPropertyName("estimatedDuration")]
    [JsonProperty("estimatedDuration")]
    public int EstimatedDuration { get; set; }

    [JsonPropertyName("partsUsed")]
    [JsonProperty("partsUsed")]
    public List<WorkOrderPartUsage> PartsUsed { get; set; } = new();

    [JsonPropertyName("tasks")]
    [JsonProperty("tasks")]
    public List<RepairTask> Tasks { get; set; } = new();

    [JsonPropertyName("notes")]
    [JsonProperty("notes")]
    public string? Notes { get; set; }
}

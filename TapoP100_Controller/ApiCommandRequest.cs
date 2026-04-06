using System.Text.Json.Serialization;

namespace TapoP100_Controller;

internal sealed record ApiCommandRequest(
    [property: JsonPropertyName("tapo_ip")] string TapoIp,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("pass")] string Pass);

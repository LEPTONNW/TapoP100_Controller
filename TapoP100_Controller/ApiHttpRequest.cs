namespace TapoP100_Controller;

internal sealed record ApiHttpRequest(string Method, string Path, ApiCommandRequest? Payload);

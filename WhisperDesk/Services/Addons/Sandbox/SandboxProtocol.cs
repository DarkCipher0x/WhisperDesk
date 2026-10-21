using System.Text.Json;
using System.Text.Json.Nodes;

namespace WhisperDesk.Services.Addons.Sandbox;

/// <summary>
/// The pipe protocol between WhisperDesk and a sandboxed add-on: one JSON object per line.
/// <code>
/// app → add-on:  init {dll, data, folder, version, options, caps} · start · stop · options {options}
///                line|started|stopped|saved|edited {e}
/// add-on → app:  hello {id, name, description, author, options[]} · log {msg} · fatal {msg}
/// </code>
/// The app only sends events for capabilities the user granted; the add-on can only answer with logs.
/// </summary>
public static class SandboxProtocol
{
    public const string HostArgument = "--addon-host";
    public const int MaxLineChars = 4 << 20;   // a saved transcript can be large; anything bigger is refused
    public const int MaxLogChars = 2000;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Message(string type, object? payload = null)
    {
        var node = payload == null ? new JsonObject() : JsonSerializer.SerializeToNode(payload, Json)!.AsObject();
        node["t"] = type;
        return node.ToJsonString(Json);
    }

    public sealed record Init(string Dll, string Data, string? Folder, string Version, Dictionary<string, string> Options, List<string> Caps, string? Sha256 = null);
    public sealed record OptionInfo(string Key, string Label, string DefaultValue, string Help, bool Secret);
    public sealed record Hello(string Id, string Name, string Description, string Author, List<OptionInfo> Options);
    public sealed record Text(string Msg);
    public sealed record Options(Dictionary<string, string> Values);
}

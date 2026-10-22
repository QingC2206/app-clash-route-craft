using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ClashRouteCraft.Core;

[JsonSerializable(typeof(JsonNode))]
[JsonSerializable(typeof(JsonObject))]
[JsonSerializable(typeof(JsonArray))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(double))]
internal partial class JsonContext : JsonSerializerContext;

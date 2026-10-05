using System.Text.Json.Serialization;
using MusicLoom.Core.State;

namespace MusicLoom.Core.Configuration;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(MusicLoomSettings))]
[JsonSerializable(typeof(StateDocument))]
public sealed partial class MusicLoomJsonContext : JsonSerializerContext;

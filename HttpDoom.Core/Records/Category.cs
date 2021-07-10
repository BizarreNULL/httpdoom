using Newtonsoft.Json;

namespace HttpDoom.Core.Records
{
    public record CategoryValue
    {
        [JsonProperty("name")] public string Name { get; init; }
    }
}
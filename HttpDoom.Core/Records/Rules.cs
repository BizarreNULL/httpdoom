using System.Collections.Generic;

using Newtonsoft.Json;

namespace HttpDoom.Core.Records
{
    public record Rules
    {
        [JsonProperty("categories")] public Dictionary<string, CategoryValue> Categories { get; init; }
        [JsonProperty("technologies")] public Dictionary<string, Technology> Technologies { get; init; }
    }
}
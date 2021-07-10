using System.Collections.Generic;
using HttpDoom.Core.Converters;
using Newtonsoft.Json;

namespace HttpDoom.Core.Records
{
    public record Technology
    {
        [JsonProperty("cats")] public List<int> Categories { get; init; } = new();
        [JsonProperty("description")] public string Description { get; init; }

        [JsonProperty("html"), JsonConverter(typeof(SingleOrArrayConverter<string>))]
        public List<string> HtmlExpressions { get; init; } = new();
        
        [JsonProperty("implies"), JsonConverter(typeof(SingleOrArrayConverter<string>))]
        public List<string> Implies { get; init; } = new();
        
        [JsonProperty("website")]
        public string VendorWebsite { get; init; }
        
        [JsonProperty("oss")]
        public bool IsOpenSource { get; init; }
        
        [JsonProperty("headers")]
        public Dictionary<string, string> HeaderExpressions { get; init; } = new();
        
        [JsonProperty("cookies")]
        public Dictionary<string, string> CookieExpressions { get; init; } = new();
        
        [JsonProperty("js")]
        public Dictionary<string, string> JavaScriptExpressions { get; init; } = new();
    }
}
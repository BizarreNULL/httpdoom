using System.Collections.Generic;

namespace HttpDoom.Core.Records
{
    public record DetectedTechnology
    {
        public string Vendor { get; set; }
        public bool IsOpenSource { get; set; }
        public string Website { get; set; }
        public string Description { get; set; }
        public List<string> Categories { get; set; }
        public List<string> Implies { get; set; }
    }
}
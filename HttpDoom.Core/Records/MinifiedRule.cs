using System.Collections.Generic;

namespace HttpDoom.Core.Records
{
    public record MinifiedRule
    {
        public string Vendor { get; init; }
        public string VendorWebsite { get; init; }
        public string Description { get; init; }
        public bool IsOpenSource { get; init; }
        public List<MinifiedCategory> Categories { get; init; } = new();
        public List<string> Implies { get; init; } = new();
        public List<string> ContentExpressions { get; init; } = new();
        public List<string> SessionExpressions { get; init; } = new();
        public List<string> HeaderExpressions { get; init; } = new();
    }
}
using System;
using System.Net;
using System.Net.Http.Headers;
using System.Collections.Generic;

namespace HttpDoom.Core.Records
{
    public record Response
    {
        public HttpResponseHeaders ResponseHeaders { get; init; }
        public HttpRequestHeaders RequestHeaders { get; init; }
        public HttpStatusCode StatusCode { get; init; }
        public List<Cookie> Cookies { get; init; }
        public Uri RedirectUri { get; init; }
        public Uri OriginUri { get; init; }
        public bool IsSuccessStatusCode { get; init; }
        public string[] Addresses { get; init; }
        public string Content { get; init; }
        public string ContentSha256Sum { get; init; }
        public string ScreenshotPath { get; set; }
    }
}
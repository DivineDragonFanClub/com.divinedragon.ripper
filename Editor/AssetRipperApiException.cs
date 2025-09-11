using System;
using System.Net;

namespace DivineDragon
{
    /// <summary>
    /// Exception class for calls related to AssetRipper
    /// </summary>
    public class AssetRipperApiException : Exception
    {
        /// <summary>
        /// Gets or sets the error code (HTTP status code)
        /// </summary>
        /// <value>The error code (HTTP status code).</value>
        public HttpStatusCode ErrorCode { get; set; }

        public AssetRipperApiException() { }

        public AssetRipperApiException(HttpStatusCode errorCode, string message) : base(message)
        {
            ErrorCode = errorCode;
        }
    }
}
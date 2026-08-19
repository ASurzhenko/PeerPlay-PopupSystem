using UnityEngine;

namespace PeerPlay.Popups.Sourcing
{
    /// <summary>
    /// Which bound fired, not merely that something did. Two safeguards that resolve to the same visible
    /// outcome make an acceptance test pass whichever one ran, so the reason is part of the result.
    /// </summary>
    public enum HttpFailure : byte
    {
        None = 0,

        /// <summary>No answer at all — DNS, connection, an aborted transfer.</summary>
        Transport = 1,

        /// <summary>The per-request budget ran out. Retryable: it is a transport-class failure.</summary>
        Timeout = 2,

        /// <summary>The whole-operation budget ran out, retries included. Terminal by construction.</summary>
        Deadline = 3,

        /// <summary>A publishing mistake. Never retried: three attempts only delay the log that says so.</summary>
        Http4xx = 4,

        Http5xx = 5
    }

    public readonly struct HttpResult
    {
        public readonly bool Ok;
        public readonly long StatusCode;

        /// <summary>Null for <see cref="IHttpClient.GetTextureAsync"/>.</summary>
        public readonly byte[] Data;

        /// <summary>Null for <see cref="IHttpClient.GetAsync"/>.</summary>
        public readonly Texture2D Texture;

        public readonly HttpFailure Failure;
        public readonly string Error;

        private HttpResult(bool ok, long statusCode, byte[] data, Texture2D texture, HttpFailure failure, string error)
        {
            Ok = ok;
            StatusCode = statusCode;
            Data = data;
            Texture = texture;
            Failure = failure;
            Error = error;
        }

        public static HttpResult Success(long statusCode, byte[] data)
        {
            return new HttpResult(true, statusCode, data, null, HttpFailure.None, null);
        }

        public static HttpResult Success(long statusCode, Texture2D texture)
        {
            return new HttpResult(true, statusCode, null, texture, HttpFailure.None, null);
        }

        public static HttpResult Fail(HttpFailure failure, string error, long statusCode = 0)
        {
            return new HttpResult(false, statusCode, null, null, failure, error);
        }
    }
}

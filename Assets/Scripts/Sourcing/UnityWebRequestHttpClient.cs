using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace PeerPlay.Popups.Sourcing
{
    /// <summary>
    /// The real transport. Every request is disposed here, and every non-2xx answer becomes a named
    /// failure rather than an exception — a 404 is a publishing mistake, not a crash.
    /// </summary>
    public sealed class UnityWebRequestHttpClient : IHttpClient
    {
        public async UniTask<HttpResult> GetAsync(string url, int timeoutSeconds, CancellationToken ct)
        {
            using (UnityWebRequest request = UnityWebRequest.Get(url))
            {
                request.timeout = timeoutSeconds;

                HttpResult failure = await SendAsync(request, url, ct);
                return failure.Ok ? HttpResult.Success(request.responseCode, request.downloadHandler.data) : failure;
            }
        }

        public async UniTask<HttpResult> GetTextureAsync(string url, int timeoutSeconds, CancellationToken ct)
        {
            using (UnityWebRequest request = UnityWebRequestTexture.GetTexture(url, true))
            {
                request.timeout = timeoutSeconds;

                HttpResult failure = await SendAsync(request, url, ct);
                if (!failure.Ok)
                {
                    return failure;
                }

                Texture2D texture = DownloadHandlerTexture.GetContent(request);
                return texture == null
                    ? HttpResult.Fail(HttpFailure.Transport, "the response decoded to no texture", request.responseCode)
                    : HttpResult.Success(request.responseCode, texture);
            }
        }

        /// <summary>Ok with no payload means "carry on and read the handler"; anything else is the answer.</summary>
        private static async UniTask<HttpResult> SendAsync(UnityWebRequest request, string url, CancellationToken ct)
        {
            try
            {
                await request.SendWebRequest().ToUniTask(cancellationToken: ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (UnityWebRequestException e)
            {
                return Classify(e.ResponseCode, e.Error, url);
            }

            if (request.result != UnityWebRequest.Result.Success)
            {
                return Classify(request.responseCode, request.error, url);
            }

            return HttpResult.Success(request.responseCode, Array.Empty<byte>());
        }

        private static HttpResult Classify(long responseCode, string error, string url)
        {
            if (responseCode >= 400 && responseCode < 500)
            {
                return HttpResult.Fail(HttpFailure.Http4xx, $"{responseCode} {error}", responseCode);
            }

            if (responseCode >= 500)
            {
                return HttpResult.Fail(HttpFailure.Http5xx, $"{responseCode} {error}", responseCode);
            }

            // UnityWebRequest reports its own timeout as a transport error with this exact text, which is
            // the only way to tell it apart from a connection failure.
            if (!string.IsNullOrEmpty(error) && error.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return HttpResult.Fail(HttpFailure.Timeout, "http-timeout", responseCode);
            }

            Debug.LogWarning($"{nameof(UnityWebRequestHttpClient)}.{nameof(Classify)} transport failure " +
                             $"({error}) — {url}");
            return HttpResult.Fail(HttpFailure.Transport, error, responseCode);
        }
    }
}

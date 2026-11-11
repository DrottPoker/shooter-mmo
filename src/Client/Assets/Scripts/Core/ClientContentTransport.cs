using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine.Networking;

namespace Mmorpg.Client.Core
{
    // Fetches the bytes of a URL for the content resolver. A request ends at its time limit,
    // and a body above the byte limit fails without being read to its end.
    public interface IClientContentTransport
    {
        IEnumerator Get(
            string url,
            int timeoutSeconds,
            long maximumBytes,
            Action<ClientContentResponse> onCompleted);
    }

    public sealed class ClientContentResponse
    {
        private ClientContentResponse(byte[] body, string error)
        {
            Body = body;
            Error = error ?? string.Empty;
        }

        public bool Succeeded
        {
            get { return Body != null; }
        }

        public byte[] Body { get; }

        // An English diagnostic for the log.
        public string Error { get; }

        public static ClientContentResponse Success(byte[] body)
        {
            return new ClientContentResponse(body ?? throw new ArgumentNullException(nameof(body)), string.Empty);
        }

        public static ClientContentResponse Failure(string error)
        {
            return new ClientContentResponse(null, string.IsNullOrWhiteSpace(error) ? "The request failed." : error);
        }
    }

    // The transport over UnityWebRequest. The owner disposes it when it is destroyed, so a
    // request that a stopped coroutine left in flight releases its native resources.
    public sealed class UnityWebContentTransport : IClientContentTransport, IDisposable
    {
        private readonly HashSet<UnityWebRequest> activeRequests = new HashSet<UnityWebRequest>();
        private bool disposed;

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            foreach (var request in activeRequests)
            {
                request.Abort();
                request.Dispose();
            }

            activeRequests.Clear();
        }

        public IEnumerator Get(
            string url,
            int timeoutSeconds,
            long maximumBytes,
            Action<ClientContentResponse> onCompleted)
        {
            if (disposed)
            {
                onCompleted(ClientContentResponse.Failure("The content transport is disposed."));
                yield break;
            }

            var download = new CappedDownloadHandler(maximumBytes);
            var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbGET)
            {
                downloadHandler = download,
                timeout = Math.Max(1, timeoutSeconds)
            };
            request.SetRequestHeader("Accept", "application/json");
            activeRequests.Add(request);
            try
            {
                yield return request.SendWebRequest();

                if (download.ExceededLimit)
                {
                    onCompleted(ClientContentResponse.Failure(
                        "The response of " + url + " is larger than " + maximumBytes + " bytes."));
                }
                else if (request.result != UnityWebRequest.Result.Success
                    || request.responseCode < 200
                    || request.responseCode >= 300)
                {
                    onCompleted(ClientContentResponse.Failure(
                        "GET " + url + " failed: " + request.result + " " + request.responseCode + " " + request.error));
                }
                else
                {
                    onCompleted(ClientContentResponse.Success(download.Body));
                }
            }
            finally
            {
                if (activeRequests.Remove(request))
                {
                    request.Dispose();
                }
            }
        }

        // Keeps the body in memory and aborts the request at the first byte above the limit.
        private sealed class CappedDownloadHandler : DownloadHandlerScript
        {
            private readonly long maximumBytes;
            private readonly MemoryStream body = new MemoryStream();

            public CappedDownloadHandler(long maximumBytes)
                : base(new byte[64 * 1024])
            {
                this.maximumBytes = maximumBytes;
            }

            public bool ExceededLimit { get; private set; }

            public byte[] Body
            {
                get { return body.ToArray(); }
            }

            protected override bool ReceiveData(byte[] data, int dataLength)
            {
                if (data == null || dataLength <= 0)
                {
                    return true;
                }

                if (body.Length + dataLength > maximumBytes)
                {
                    ExceededLimit = true;
                    return false;
                }

                body.Write(data, 0, dataLength);
                return true;
            }
        }
    }
}

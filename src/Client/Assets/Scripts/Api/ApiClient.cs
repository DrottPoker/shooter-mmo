using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace Mmorpg.Client.Api
{
    // Owners dispose the client when they are destroyed. Unity stops a coroutine without
    // disposing its iterator, so a request still in flight would otherwise never release
    // its native resources.
    public sealed class ApiClient : IDisposable
    {
        private readonly int timeoutSeconds;
        private readonly HashSet<UnityWebRequest> activeRequests = new HashSet<UnityWebRequest>();
        private bool disposed;

        public ApiClient(int timeoutSeconds)
        {
            this.timeoutSeconds = Mathf.Clamp(timeoutSeconds, 1, 120);
        }

        public int ActiveRequestCount
        {
            get { return activeRequests.Count; }
        }

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

        public IEnumerator Register(
            string gameServiceBaseUrl,
            RegisterAccountRequest request,
            Action<AuthResponse> onSuccess,
            Action<ApiError> onError)
        {
            return SendJson(
                "POST",
                CombineUrl(gameServiceBaseUrl, "/api/accounts/register"),
                JsonUtility.ToJson(request),
                null,
                onSuccess,
                onError);
        }

        public IEnumerator Login(
            string gameServiceBaseUrl,
            LoginAccountRequest request,
            Action<AuthResponse> onSuccess,
            Action<ApiError> onError)
        {
            return SendJson(
                "POST",
                CombineUrl(gameServiceBaseUrl, "/api/accounts/login"),
                JsonUtility.ToJson(request),
                null,
                onSuccess,
                onError);
        }

        public IEnumerator Logout(
            string gameServiceBaseUrl,
            string sessionToken,
            Action onSuccess,
            Action<ApiError> onError)
        {
            return SendEmpty(
                "POST",
                CombineUrl(gameServiceBaseUrl, "/api/accounts/logout"),
                null,
                sessionToken,
                onSuccess,
                onError);
        }

        public IEnumerator RevokeSession(
            string gameServiceBaseUrl,
            string sessionToken,
            string sessionId,
            Action onSuccess,
            Action<ApiError> onError)
        {
            return SendEmpty(
                "DELETE",
                CombineUrl(gameServiceBaseUrl, "/api/accounts/sessions/" + Uri.EscapeDataString(sessionId)),
                null,
                sessionToken,
                onSuccess,
                onError);
        }

        public IEnumerator ValidateSession(
            string gameServiceBaseUrl,
            string sessionToken,
            Action onSuccess,
            Action<ApiError> onError)
        {
            return SendEmpty(
                "GET",
                CombineUrl(gameServiceBaseUrl, "/api/accounts/session"),
                null,
                sessionToken,
                onSuccess,
                onError);
        }

        public IEnumerator GetCharacters(
            string gameServiceBaseUrl,
            string sessionToken,
            Action<CharacterResponse[]> onSuccess,
            Action<ApiError> onError)
        {
            return SendArray(
                "GET",
                CombineUrl(gameServiceBaseUrl, "/api/characters"),
                null,
                sessionToken,
                onSuccess,
                onError);
        }

        public IEnumerator CreateCharacter(
            string gameServiceBaseUrl,
            string sessionToken,
            CreateCharacterRequest request,
            Action<CharacterResponse> onSuccess,
            Action<ApiError> onError)
        {
            return SendJson(
                "POST",
                CombineUrl(gameServiceBaseUrl, "/api/characters"),
                JsonUtility.ToJson(request),
                sessionToken,
                onSuccess,
                onError);
        }

        public IEnumerator GetCharacterAppearanceCatalog(
            string gameServiceBaseUrl,
            string sessionToken,
            Action<CharacterAppearanceCatalogResponse> onSuccess,
            Action<ApiError> onError)
        {
            return SendJson(
                "GET",
                CombineUrl(gameServiceBaseUrl, "/api/character-appearance"),
                null,
                sessionToken,
                onSuccess,
                onError);
        }

        public IEnumerator GetShards(
            string gameServiceBaseUrl,
            Action<ShardResponse[]> onSuccess,
            Action<ApiError> onError)
        {
            return SendArray(
                "GET",
                CombineUrl(gameServiceBaseUrl, "/api/shards"),
                null,
                null,
                onSuccess,
                onError);
        }

        public IEnumerator CreateJoinTicket(
            string gameServiceBaseUrl,
            string sessionToken,
            string shardId,
            JoinShardRequest request,
            Action<JoinShardResponse> onSuccess,
            Action<ApiError> onError)
        {
            return SendJson(
                "POST",
                CombineUrl(gameServiceBaseUrl, "/api/shards/" + Uri.EscapeDataString(shardId) + "/join"),
                JsonUtility.ToJson(request),
                sessionToken,
                onSuccess,
                onError);
        }

        public IEnumerator GetCharacterInventory(
            string gameServiceBaseUrl,
            string sessionToken,
            string characterId,
            Action<CharacterInventorySnapshotResponse> onSuccess,
            Action<ApiError> onError)
        {
            return SendJson(
                "GET",
                CombineUrl(
                    gameServiceBaseUrl,
                    "/api/characters/" + Uri.EscapeDataString(characterId) + "/item-state"),
                null,
                sessionToken,
                onSuccess,
                onError);
        }

        public IEnumerator GetCharacterBank(
            string gameServiceBaseUrl,
            string sessionToken,
            string characterId,
            Action<CharacterBankSnapshotResponse> onSuccess,
            Action<ApiError> onError)
        {
            return SendJson(
                "GET",
                CombineUrl(
                    gameServiceBaseUrl,
                    "/api/characters/" + Uri.EscapeDataString(characterId) + "/bank"),
                null,
                sessionToken,
                onSuccess,
                onError);
        }

        public IEnumerator GetCharacterRecovery(
            string gameServiceBaseUrl,
            string sessionToken,
            string characterId,
            Action<CharacterRecoverySnapshotResponse> onSuccess,
            Action<ApiError> onError)
        {
            return SendJson(
                "GET",
                CombineUrl(
                    gameServiceBaseUrl,
                    "/api/characters/" + Uri.EscapeDataString(characterId) + "/recovery"),
                null,
                sessionToken,
                onSuccess,
                onError);
        }

        private IEnumerator SendJson<T>(
            string method,
            string url,
            string json,
            string sessionToken,
            Action<T> onSuccess,
            Action<ApiError> onError)
        {
            var request = CreateRequest(method, url, json, sessionToken);
            try
            {
                yield return request.SendWebRequest();

                if (!IsSuccessful(request))
                {
                    onError(CreateError(request));
                    yield break;
                }

                var body = request.downloadHandler != null ? request.downloadHandler.text : string.Empty;
                if (string.IsNullOrWhiteSpace(body))
                {
                    onError(InvalidResponse("The server returned an empty response."));
                    yield break;
                }

                T response;
                try
                {
                    response = JsonUtility.FromJson<T>(body);
                }
                catch (Exception exception)
                {
                    onError(InvalidResponse("The server returned invalid JSON: " + exception.Message));
                    yield break;
                }

                if (response == null)
                {
                    onError(InvalidResponse("The server returned an invalid response."));
                    yield break;
                }

                onSuccess(response);
            }
            finally
            {
                Release(request);
            }
        }

        private IEnumerator SendArray<T>(
            string method,
            string url,
            string json,
            string sessionToken,
            Action<T[]> onSuccess,
            Action<ApiError> onError)
        {
            var request = CreateRequest(method, url, json, sessionToken);
            try
            {
                yield return request.SendWebRequest();

                if (!IsSuccessful(request))
                {
                    onError(CreateError(request));
                    yield break;
                }

                T[] result;
                if (!JsonArrayUtility.TryFromJson(request.downloadHandler.text, out result))
                {
                    onError(InvalidResponse("The server returned an invalid JSON array."));
                    yield break;
                }

                onSuccess(result);
            }
            finally
            {
                Release(request);
            }
        }

        private IEnumerator SendEmpty(
            string method,
            string url,
            string json,
            string sessionToken,
            Action onSuccess,
            Action<ApiError> onError)
        {
            var request = CreateRequest(method, url, json, sessionToken);
            try
            {
                yield return request.SendWebRequest();

                if (!IsSuccessful(request))
                {
                    onError(CreateError(request));
                    yield break;
                }

                onSuccess();
            }
            finally
            {
                Release(request);
            }
        }

        private void Release(UnityWebRequest request)
        {
            if (activeRequests.Remove(request))
            {
                request.Dispose();
            }
        }

        private UnityWebRequest CreateRequest(
            string method,
            string url,
            string json,
            string sessionToken)
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(ApiClient));
            }

            var request = new UnityWebRequest(url, method)
            {
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = timeoutSeconds
            };

            if (!string.IsNullOrEmpty(json))
            {
                var bytes = Encoding.UTF8.GetBytes(json);
                request.uploadHandler = new UploadHandlerRaw(bytes);
                request.SetRequestHeader("Content-Type", "application/json");
            }

            request.SetRequestHeader("Accept", "application/json");

            if (!string.IsNullOrWhiteSpace(sessionToken))
            {
                request.SetRequestHeader("Authorization", "Bearer " + sessionToken);
            }

            activeRequests.Add(request);
            return request;
        }

        private static bool IsSuccessful(UnityWebRequest request)
        {
            return request.result == UnityWebRequest.Result.Success
                   && request.responseCode >= 200
                   && request.responseCode < 300;
        }

        internal static ApiError ParseHttpError(long statusCode, string body, string fallbackMessage)
        {
            ProblemDetailsResponse problem = null;
            if (!string.IsNullOrWhiteSpace(body))
            {
                try
                {
                    problem = JsonUtility.FromJson<ProblemDetailsResponse>(body);
                }
                catch (ArgumentException)
                {
                    problem = null;
                }
            }

            var code = problem != null && !string.IsNullOrWhiteSpace(problem.code)
                ? problem.code
                : "http_error";
            var message = problem != null && !string.IsNullOrWhiteSpace(problem.detail)
                ? problem.detail
                : fallbackMessage;
            var correlationId = problem != null ? problem.correlationId : string.Empty;

            return new ApiError(
                ApiErrorKind.Http,
                statusCode,
                code,
                message,
                correlationId);
        }

        private static ApiError CreateError(UnityWebRequest request)
        {
            var body = request.downloadHandler != null ? request.downloadHandler.text : string.Empty;
            if (request.result == UnityWebRequest.Result.ConnectionError)
            {
                var timedOut = !string.IsNullOrWhiteSpace(request.error)
                    && (request.error.IndexOf("timed out", StringComparison.OrdinalIgnoreCase) >= 0
                        || request.error.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0);
                return new ApiError(
                    timedOut ? ApiErrorKind.Timeout : ApiErrorKind.Network,
                    request.responseCode,
                    timedOut ? "request_timeout" : "network_error",
                    timedOut ? "The request timed out." : request.error,
                    string.Empty);
            }

            if (request.responseCode > 0)
            {
                return ParseHttpError(request.responseCode, body, request.error);
            }

            return InvalidResponse(request.error);
        }

        private static ApiError InvalidResponse(string message)
        {
            return new ApiError(
                ApiErrorKind.InvalidResponse,
                0,
                "invalid_response",
                message,
                string.Empty);
        }

        private static string CombineUrl(string baseUrl, string path)
        {
            return baseUrl.TrimEnd('/') + path;
        }
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace Mmorpg.Client.Core
{
    // Resolves the content set once at client start: for each file, the bundled copy when its
    // revision is the one GameService serves, else the cached copy of that revision, else a
    // download. A cached or downloaded file is used only when its content hashes to the revision
    // the manifest names. It is all or nothing: when anything fails, the client runs with its
    // complete bundled set. The manifest comes from the configured GameService like every API
    // answer; the catalogs feed prediction and UI only, and the servers stay the authority.
    public sealed class ClientContentResolver
    {
        // Set to 1 in the Editor, no bundled file counts as current, so every file comes from
        // the cache or a download. The bundled set stays the fallback.
        public const string IgnoreBundledContentPreference = "Mmorpg.Debug.IgnoreBundledContent";

        public const string ManifestPath = "/api/content";
        public const int ManifestTimeoutSeconds = 3;
        public const int FileTimeoutSeconds = 10;

        // The whole check, manifest and downloads, ends within this time.
        public const int TotalTimeoutSeconds = 20;

        public const long MaximumManifestBytes = 1024L * 1024L;
        public const long MaximumFileBytes = 16L * 1024L * 1024L;

        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        private readonly string gameServiceBaseUrl;
        private readonly IClientContentTransport transport;
        private readonly ClientContentCache cache;
        private readonly IReadOnlyList<ClientContentRequest> requests;
        private readonly bool ignoreBundledContent;
        private readonly List<string> warnings = new List<string>();
        private Stopwatch clock;

        public ClientContentResolver(
            string gameServiceBaseUrl,
            IClientContentTransport transport,
            ClientContentCache cache,
            IReadOnlyList<ClientContentRequest> requests,
            bool ignoreBundledContent)
        {
            this.gameServiceBaseUrl = (gameServiceBaseUrl ?? string.Empty).Trim().TrimEnd('/');
            this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
            this.cache = cache ?? throw new ArgumentNullException(nameof(cache));
            this.requests = requests ?? throw new ArgumentNullException(nameof(requests));
            this.ignoreBundledContent = ignoreBundledContent;
        }

        // Runs once; the callback always receives a complete set.
        public IEnumerator Resolve(Action<ClientContentReport> onResolved)
        {
            if (onResolved == null)
            {
                throw new ArgumentNullException(nameof(onResolved));
            }

            warnings.Clear();
            clock = Stopwatch.StartNew();
            var outcome = new Outcome();
            yield return ResolveServed(outcome);
            onResolved(outcome.Files != null
                ? new ClientContentReport(outcome.Files, string.Empty, string.Empty, warnings.ToArray())
                : ClientContentReport.Bundled(requests, outcome.FallbackCode, outcome.FallbackDiagnostic, warnings.ToArray()));
        }

        private IEnumerator ResolveServed(Outcome outcome)
        {
            if (!TryCreateHttpUrl(gameServiceBaseUrl + ManifestPath, out var manifestUrl))
            {
                outcome.Fail(ClientContentReport.UnavailableCode, "The GameService base URL '" + gameServiceBaseUrl + "' is not an http or https URL.");
                yield break;
            }

            ClientContentResponse manifestResponse = null;
            yield return transport.Get(
                manifestUrl,
                ManifestTimeoutSeconds,
                MaximumManifestBytes,
                response => manifestResponse = response);
            if (manifestResponse == null || !manifestResponse.Succeeded)
            {
                outcome.Fail(
                    ClientContentReport.UnavailableCode,
                    "The content manifest could not be fetched: "
                    + (manifestResponse != null ? manifestResponse.Error : "no answer"));
                yield break;
            }

            if (!TryDecode(manifestResponse.Body, out var manifestJson))
            {
                outcome.Fail(ClientContentReport.UnavailableCode, "The content manifest is not UTF-8.");
                yield break;
            }

            if (!ClientContentManifest.TryParse(manifestJson, out var manifest, out var manifestError))
            {
                outcome.Fail(ClientContentReport.UnavailableCode, manifestError);
                yield break;
            }

            if (!TryMatchManifest(manifest, outcome, out var entries))
            {
                yield break;
            }

            var files = new ClientContentFile[requests.Count];
            for (var index = 0; index < requests.Count; index++)
            {
                var request = requests[index];
                var entry = entries[index];
                if (!ignoreBundledContent
                    && ClientContentHeader.TryRead(request.BundledText, out var bundled)
                    && MatchesEntry(request, entry, bundled))
                {
                    files[index] = new ClientContentFile(request.Kind, request.Id, request.Locale, entry.Revision, ClientContentSource.Bundled, request.BundledText);
                    continue;
                }

                if (TryUseCached(request, entry, out var cachedText))
                {
                    files[index] = new ClientContentFile(request.Kind, request.Id, request.Locale, entry.Revision, ClientContentSource.Cache, cachedText);
                    continue;
                }

                string downloaded = null;
                yield return Download(request, entry, outcome, text => downloaded = text);
                if (downloaded == null)
                {
                    yield break;
                }

                files[index] = new ClientContentFile(request.Kind, request.Id, request.Locale, entry.Revision, ClientContentSource.Downloaded, downloaded);
            }

            for (var index = 0; index < requests.Count; index++)
            {
                if (!cache.TryDeleteOtherRevisions(requests[index], entries[index].Revision, out var error))
                {
                    warnings.Add(error);
                }
            }

            outcome.Files = files;
        }

        // Every file of the set must be listed in a format this build reads. A newer format
        // means this client is out of date.
        private bool TryMatchManifest(
            ClientContentManifest manifest,
            Outcome outcome,
            out ClientContentManifestEntry[] entries)
        {
            entries = null;
            if (manifest.FormatVersion > ClientContentManifest.SupportedFormatVersion)
            {
                outcome.Fail(
                    ClientContentReport.UpdateRequiredCode,
                    "The content manifest has format " + manifest.FormatVersion + ", and this client reads format "
                    + ClientContentManifest.SupportedFormatVersion + ".");
                return false;
            }

            if (manifest.FormatVersion != ClientContentManifest.SupportedFormatVersion)
            {
                outcome.Fail(ClientContentReport.UnavailableCode, "The content manifest has the unknown format " + manifest.FormatVersion + ".");
                return false;
            }

            var matched = new ClientContentManifestEntry[requests.Count];
            for (var index = 0; index < requests.Count; index++)
            {
                var request = requests[index];
                var entry = manifest.Find(request.Kind, request.Id, request.Locale);
                if (entry == null)
                {
                    outcome.Fail(ClientContentReport.UnavailableCode, "The content manifest does not list " + request.Name + ".");
                    return false;
                }

                if (entry.FormatVersion > request.FormatVersion)
                {
                    outcome.Fail(
                        ClientContentReport.UpdateRequiredCode,
                        "GameService serves " + request.Name + " in format " + entry.FormatVersion
                        + ", and this client reads format " + request.FormatVersion + ".");
                    return false;
                }

                if (entry.FormatVersion != request.FormatVersion)
                {
                    outcome.Fail(
                        ClientContentReport.UnavailableCode,
                        "GameService serves " + request.Name + " in the older format " + entry.FormatVersion
                        + ", and this client reads format " + request.FormatVersion + ".");
                    return false;
                }

                matched[index] = entry;
            }

            entries = matched;
            return true;
        }

        // A cached file that fails its check is deleted, and the file is downloaded once.
        private bool TryUseCached(ClientContentRequest request, ClientContentManifestEntry entry, out string text)
        {
            text = null;
            if (!cache.TryRead(request, entry.Revision, out var bytes, out var readError))
            {
                if (readError.Length > 0)
                {
                    warnings.Add(readError);
                }

                return false;
            }

            if (TryValidate(request, entry, bytes, out text, out var error))
            {
                return true;
            }

            warnings.Add("The cached " + request.Name + " is invalid and is downloaded again: " + error);
            if (!cache.TryDelete(request, entry.Revision, out var deleteError))
            {
                warnings.Add(deleteError);
            }

            text = null;
            return false;
        }

        private IEnumerator Download(
            ClientContentRequest request,
            ClientContentManifestEntry entry,
            Outcome outcome,
            Action<string> onDownloaded)
        {
            if (!TryResolveUrl(entry.Url, out var url))
            {
                outcome.Fail(ClientContentReport.UnavailableCode, "The URL '" + entry.Url + "' of " + request.Name + " is not an http or https URL.");
                yield break;
            }

            if (entry.SizeBytes > MaximumFileBytes)
            {
                outcome.Fail(ClientContentReport.UnavailableCode, request.Name + " is " + entry.SizeBytes + " bytes, above the limit of " + MaximumFileBytes + ".");
                yield break;
            }

            var remainingSeconds = TotalTimeoutSeconds - (int)clock.Elapsed.TotalSeconds;
            if (remainingSeconds < 1)
            {
                outcome.Fail(ClientContentReport.UnavailableCode, "The content check took longer than " + TotalTimeoutSeconds + " seconds.");
                yield break;
            }

            ClientContentResponse response = null;
            yield return transport.Get(
                url,
                Math.Min(FileTimeoutSeconds, remainingSeconds),
                MaximumFileBytes,
                value => response = value);
            if (response == null || !response.Succeeded)
            {
                outcome.Fail(
                    ClientContentReport.UnavailableCode,
                    request.Name + " could not be downloaded: " + (response != null ? response.Error : "no answer"));
                yield break;
            }

            if (!TryValidate(request, entry, response.Body, out var text, out var error))
            {
                outcome.Fail(ClientContentReport.UnavailableCode, "The downloaded " + request.Name + " is invalid: " + error);
                yield break;
            }

            if (!cache.TryWrite(request, entry.Revision, response.Body, out var writeError))
            {
                warnings.Add(writeError);
            }

            onDownloaded(text);
        }

        // The loader's own check, which hashes the content again, and then the identity: the
        // revision the manifest names, the expected id and locale, and this build's format.
        private static bool TryValidate(
            ClientContentRequest request,
            ClientContentManifestEntry entry,
            byte[] bytes,
            out string text,
            out string error)
        {
            text = null;
            if (bytes == null || bytes.LongLength > MaximumFileBytes)
            {
                error = "it is larger than " + MaximumFileBytes + " bytes.";
                return false;
            }

            if (bytes.LongLength != entry.SizeBytes)
            {
                error = "it is " + bytes.LongLength + " bytes, and the manifest lists " + entry.SizeBytes + ".";
                return false;
            }

            if (!TryDecode(bytes, out var decoded))
            {
                error = "it is not UTF-8.";
                return false;
            }

            ClientContentHeader header;
            try
            {
                if (!request.Validator(decoded, out header, out error))
                {
                    return false;
                }
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }

            if (header == null || !MatchesEntry(request, entry, header))
            {
                error = "it states " + Describe(header) + ", and the manifest lists " + entry.Name
                    + " format " + entry.FormatVersion + " revision " + entry.Revision + ".";
                return false;
            }

            text = decoded;
            error = string.Empty;
            return true;
        }

        private static bool MatchesEntry(ClientContentRequest request, ClientContentManifestEntry entry, ClientContentHeader header)
        {
            return string.Equals(header.Id, request.Id, StringComparison.Ordinal)
                && string.Equals(header.Locale, request.Locale, StringComparison.Ordinal)
                && header.FormatVersion == request.FormatVersion
                && string.Equals(header.Revision, entry.Revision, StringComparison.Ordinal);
        }

        private static string Describe(ClientContentHeader header)
        {
            return header == null
                ? "no header"
                : "id '" + header.Id + "' locale '" + header.Locale + "' format " + header.FormatVersion + " revision " + header.Revision;
        }

        // A URL that starts with one slash is relative to the GameService base URL, as the
        // manifest URL is; any other must be an absolute http or https URL.
        private bool TryResolveUrl(string value, out string url)
        {
            url = null;
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            if (value[0] == '/')
            {
                return value.Length > 1 && value[1] != '/' && TryCreateHttpUrl(gameServiceBaseUrl + value, out url);
            }

            return TryCreateHttpUrl(value, out url);
        }

        private static bool TryCreateHttpUrl(string value, out string url)
        {
            url = null;
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return false;
            }

            url = uri.AbsoluteUri;
            return true;
        }

        private static bool TryDecode(byte[] bytes, out string text)
        {
            try
            {
                text = bytes == null ? null : StrictUtf8.GetString(bytes);
                return text != null;
            }
            catch (ArgumentException)
            {
                text = null;
                return false;
            }
        }

        private sealed class Outcome
        {
            public ClientContentFile[] Files;
            public string FallbackCode = string.Empty;
            public string FallbackDiagnostic = string.Empty;

            public void Fail(string code, string diagnostic)
            {
                FallbackCode = code;
                FallbackDiagnostic = diagnostic;
            }
        }
    }
}

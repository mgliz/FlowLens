using FlowLens;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class GitHubUpdateTests
{
    public static void Run(Action<bool, string> check) => RunAsync(check).GetAwaiter().GetResult();

    private static async Task RunAsync(Action<bool, string> check)
    {
        var ordered = new[] { "1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta", "1.0.0-beta.2", "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0", "1.0.1", "1.1.0", "2.0.0" };
        for (var i = 1; i < ordered.Length; i++)
            check(Parse(ordered[i - 1]).CompareTo(Parse(ordered[i])) < 0, "Updates must compare semantic versions: " + ordered[i - 1] + " < " + ordered[i]);
        check(Parse("v1.0.5+build.10").CompareTo(Parse("1.0.5+build.20")) == 0, "Build metadata must not change semantic version precedence.");
        check(Parse("1.0.0-999999999999999999999999999").CompareTo(Parse("1.0.0-1000000000000000000000000000")) < 0,
            "Long numeric prerelease components must compare without integer overflow.");
        foreach (var invalid in new[] { "", "1.0", "1.0.5.0", "01.0.0", "1.0.0-01", "1.0.0-", "1.0.0+", "1.0.0-preview/1" })
            check(!SemanticVersion.TryParse(invalid, out _), "Malformed versions must not be accepted: " + invalid);

        var assemblyPath = typeof(GitHubUpdateService).Assembly.Location;
        var fileVersion = FileVersionInfo.GetVersionInfo(assemblyPath);
        var version = $"{fileVersion.FileMajorPart}.{fileVersion.FileMinorPart}.{fileVersion.FileBuildPart}";
        var binary = await File.ReadAllBytesAsync(assemblyPath);
        var package = Zip(("FlowLens.exe", binary), ("../escaped.txt", Encoding.UTF8.GetBytes("must not be extracted")),
            ("README.md", Encoding.UTF8.GetBytes("metadata")));
        var archiveName = $"FlowLens-{version}-win-x64.zip";
        var fixture = CreateFixture(version, package);
        var root = Path.Combine(Path.GetTempPath(), "FlowLens-updates-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using (var handler = new StubHandler(request => JsonResponse(fixture.Metadata)))
            using (var client = new HttpClient(handler))
            using (var service = new GitHubUpdateService(client))
            {
                var same = await service.CheckAsync(version, false);
                check(same is { Relation: GitHubVersionRelation.Same, CanInstall: false }, "The installed stable version must not offer itself as a newer update.");
                var test = await service.CheckAsync("99.0.0-preview.1", true);
                check(test is { Relation: GitHubVersionRelation.Older, CanInstall: true }, "A newer local test build must still be able to switch to the official stable release.");
                var newer = await service.CheckAsync("0.0.0", false);
                check(newer is { Relation: GitHubVersionRelation.Newer, CanInstall: true }, "A newer official version must be offered to stable installations.");
                var prerelease = await service.CheckAsync(version + "-preview.1", false);
                check(prerelease is { Relation: GitHubVersionRelation.Newer, CanInstall: true }, "A final release must sort after a prerelease with the same core version.");
                check(handler.Requests.All(request => request.Uri == GitHubUpdateService.LatestReleaseApiUrl && request.UserAgent.Contains("FlowLens") && request.ApiVersion == "2022-11-28"),
                    "Update checks must use the fixed official latest-release API, User-Agent, and API version.");
            }

            foreach (var metadata in new[]
                     {
                         CreateFixture(version, package, draft: true).Metadata,
                         CreateFixture(version, package, prerelease: true).Metadata,
                         CreateFixture(version + "-preview.1", package).Metadata
                     })
            {
                using var client = new HttpClient(new StubHandler(_ => JsonResponse(metadata)));
                using var service = new GitHubUpdateService(client);
                check(await service.CheckAsync(version, false) is null, "Draft and prerelease results must not become stable update offers.");
            }
            using (var client = new HttpClient(new StubHandler(_ => new(HttpStatusCode.NotFound))))
            using (var service = new GitHubUpdateService(client))
                check(await service.CheckAsync(version, false) is null, "A repository without a published stable release must return no update.");

            foreach (var status in new[] { HttpStatusCode.Forbidden, HttpStatusCode.TooManyRequests })
            {
                using var handler = WebsiteFallbackHandler(version, package, status);
                using var client = new HttpClient(handler);
                using var service = new GitHubUpdateService(client);
                var result = await service.CheckAsync("99.0.0-preview.1", true);
                check(result is { Relation: GitHubVersionRelation.Older, CanInstall: true } &&
                      result.Release.Asset.Size == package.LongLength &&
                      result.Release.Asset.Sha256 == Convert.ToHexString(SHA256.HashData(package)),
                    "API rate limits must fall back to the official stable tag, checksum asset, and package size.");
                check(handler.Requests.Select(item => (item.Method, item.Uri)).SequenceEqual(new[]
                {
                    ("GET", GitHubUpdateService.LatestReleaseApiUrl),
                    ("HEAD", GitHubUpdateService.RepositoryUrl + "/releases/latest"),
                    ("GET", GitHubUpdateService.RepositoryUrl + "/releases/download/v" + version + "/SHA256SUMS.txt"),
                    ("HEAD", GitHubUpdateService.RepositoryUrl + "/releases/download/v" + version + "/" + archiveName)
                }), "API fallback must use only known official endpoints and fetch no HTML or package body while checking.");
                var prepared = await service.DownloadAndPrepareAsync(result!.Release, Path.Combine(root, "rate-limit-" + (int)status));
                check(File.Exists(prepared.ExecutablePath), "A checksum-verified website fallback must support normal update preparation.");
            }
            foreach (var redirect in new[]
                     {
                         "https://example.com/mgliz/FlowLens/releases/tag/v" + version,
                         "https://github.com/other/FlowLens/releases/tag/v" + version,
                         GitHubUpdateService.RepositoryUrl + "/releases/tag/v" + version + "-preview.1",
                         GitHubUpdateService.RepositoryUrl + "/releases/tag/v" + version + "?mirror=other",
                         GitHubUpdateService.RepositoryUrl + "/releases/latest"
                     })
            {
                using var handler = WebsiteFallbackHandler(version, package, releaseRedirect: redirect);
                using var client = new HttpClient(handler);
                using var service = new GitHubUpdateService(client);
                await MustFail<InvalidDataException>(() => service.CheckAsync(version, true), check,
                    "The API fallback must reject redirects outside the exact official stable release path.");
                check(handler.Requests.Count == 2, "An invalid latest-release redirect must stop before requesting checksum or package assets.");
            }
            foreach (var invalidFallback in new[]
                     {
                         WebsiteFallbackHandler(version, package, checksumStatus: HttpStatusCode.NotFound),
                         WebsiteFallbackHandler(version, package, checksumText: "no valid checksum"),
                         WebsiteFallbackHandler(version, package, archiveRedirect: "https://example.com/package.zip"),
                         WebsiteFallbackHandler(version, package, archiveSize: 0),
                         WebsiteFallbackHandler(version, package, archiveSize: GitHubUpdateService.MaximumArchiveBytes + 1),
                         WebsiteFallbackHandler(version, package, archiveStatus: HttpStatusCode.NotFound)
                     })
            {
                using var handler = invalidFallback;
                using var client = new HttpClient(handler);
                using var service = new GitHubUpdateService(client);
                await MustFail<InvalidDataException>(() => service.CheckAsync(version, true), check,
                    "API fallback must block missing/invalid checksums, untrusted asset redirects, and absent or incorrectly sized packages.");
            }
            foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.InternalServerError })
            {
                using var handler = new StubHandler(_ => new(status));
                using var client = new HttpClient(handler);
                using var service = new GitHubUpdateService(client);
                await MustFail<HttpRequestException>(() => service.CheckAsync(version, true), check,
                    "Errors other than API 403/429 must retain their original HTTP failure.");
                check(handler.Requests.Count == 1, "The website fallback must run only for API 403/429.");
            }

            foreach (var invalidMetadata in new[]
                     {
                         fixture.Metadata.Replace("https://github.com/mgliz/FlowLens/releases/download/", "https://example.com/mgliz/FlowLens/releases/download/", StringComparison.Ordinal),
                         fixture.Metadata.Replace("https://github.com/mgliz/FlowLens/releases/tag/", "https://example.com/mgliz/FlowLens/releases/tag/", StringComparison.Ordinal),
                         CreateFixture(version, package, digest: null).Metadata,
                         CreateFixture(version, package, digest: "sha256:broken").Metadata,
                         fixture.Metadata.Replace(archiveName, "unrelated.zip", StringComparison.Ordinal)
                     })
            {
                using var client = new HttpClient(new StubHandler(_ => JsonResponse(invalidMetadata)));
                using var service = new GitHubUpdateService(client);
                await MustFail<InvalidDataException>(() => service.CheckAsync(version, false), check,
                    "Untrusted URLs, missing checksums, invalid checksums, and unrelated packages must be rejected.");
            }

            using (var client = new HttpClient(FixtureHandler(fixture)))
            using (var service = new GitHubUpdateService(client))
            {
                var release = (await service.CheckAsync(version, true))!.Release;
                var progress = new List<GitHubUpdateProgress>();
                var stage = Path.Combine(root, "valid");
                var prepared = await service.DownloadAndPrepareAsync(release, stage, new ImmediateProgress(progress.Add));
                check(File.ReadAllBytes(prepared.ExecutablePath).SequenceEqual(binary), "A validated update must extract the exact executable bytes.");
                check(prepared.ExecutableSha256 == Convert.ToHexString(SHA256.HashData(binary)) && prepared.ArchiveSha256 == Convert.ToHexString(SHA256.HashData(package)),
                    "Update preparation must return independent verified executable and archive hashes.");
                check(Directory.GetFiles(stage).Select(Path.GetFileName).SequenceEqual(new[] { "FlowLens.exe" }) && !File.Exists(Path.Combine(root, "escaped.txt")),
                    "Preparation must extract only the root executable; other and traversal entries must remain unextracted.");
                check(progress.Count > 0 && progress[^1].DownloadedBytes == package.Length && progress.All(item => item.TotalBytes == package.Length),
                    "Download progress must be based on verified asset byte counts.");

                await MustFail<IOException>(() => service.DownloadAndPrepareAsync(release, stage), check,
                    "Preparation must never overwrite an existing executable.");
                check(File.ReadAllBytes(prepared.ExecutablePath).SequenceEqual(binary), "A failed preparation must preserve an existing staging executable.");
            }

            var checksumText = Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant() + "  " + archiveName + "\r\n";
            var fallback = CreateFixture(version, package, digest: null, checksumText: checksumText);
            using (var client = new HttpClient(FixtureHandler(fallback)))
            using (var service = new GitHubUpdateService(client))
            {
                var release = (await service.CheckAsync(version, true))!.Release;
                var prepared = await service.DownloadAndPrepareAsync(release, Path.Combine(root, "fallback"));
                check(File.Exists(prepared.ExecutablePath), "Official SHA256SUMS.txt must support assets whose API digest is unavailable.");
            }

            foreach (var badChecksums in new[] { checksumText + checksumText, checksumText.Replace(archiveName, "other.zip", StringComparison.Ordinal), new string('0', 64) + " *" + archiveName })
            {
                var badFixture = CreateFixture(version, package, digest: null, checksumText: badChecksums);
                using var client = new HttpClient(FixtureHandler(badFixture));
                using var service = new GitHubUpdateService(client);
                var release = (await service.CheckAsync(version, true))!.Release;
                await MustFail<InvalidDataException>(() => service.DownloadAndPrepareAsync(release, Path.Combine(root, Guid.NewGuid().ToString("N"))), check,
                    "Duplicate, absent, and mismatched fallback checksums must block update preparation.");
            }

            var brokenPackages = new[]
            {
                Zip(("folder/FlowLens.exe", binary)),
                Zip(("FlowLens.exe", binary), ("flowlens.exe", binary)),
                Zip(("FlowLens.exe", new byte[4096])),
                Zip(("FlowLens.exe", new byte[1]))
            };
            foreach (var brokenPackage in brokenPackages)
            {
                var badFixture = CreateFixture(version, brokenPackage);
                using var client = new HttpClient(FixtureHandler(badFixture));
                using var service = new GitHubUpdateService(client);
                var release = (await service.CheckAsync(version, true))!.Release;
                var stage = Path.Combine(root, Guid.NewGuid().ToString("N"));
                await MustFail<InvalidDataException>(() => service.DownloadAndPrepareAsync(release, stage), check,
                    "Missing, duplicate, invalid, and undersized executables must be rejected.");
                check(!File.Exists(Path.Combine(stage, "FlowLens.exe")) && !File.Exists(Path.Combine(stage, "update.zip")),
                    "Rejected downloads must not leave an executable or archive eligible for installation.");
            }

            var wrongVersionFixture = CreateFixture($"{fileVersion.FileMajorPart}.{fileVersion.FileMinorPart}.{fileVersion.FileBuildPart + 1}", package);
            using (var client = new HttpClient(FixtureHandler(wrongVersionFixture)))
            using (var service = new GitHubUpdateService(client))
            {
                var release = (await service.CheckAsync(version, true))!.Release;
                await MustFail<InvalidDataException>(() => service.DownloadAndPrepareAsync(release, Path.Combine(root, "wrong-version")), check,
                    "The extracted executable must match the release's version resource.");
            }
            using (var client = new HttpClient(FixtureHandler(fixture)))
            using (var service = new GitHubUpdateService(client))
            {
                var release = (await service.CheckAsync(version, true))!.Release;
                var altered = release with { Asset = release.Asset with { Sha256 = new string('0', 64) } };
                await MustFail<InvalidDataException>(() => service.DownloadAndPrepareAsync(altered, Path.Combine(root, "hash-mismatch")), check,
                    "A package with mismatching SHA-256 must never be extracted.");
                altered = release with { Asset = release.Asset with { Size = package.Length + 1 } };
                await MustFail<InvalidDataException>(() => service.DownloadAndPrepareAsync(altered, Path.Combine(root, "size-mismatch")), check,
                    "A response with a size different from the release metadata must be rejected.");
                altered = release with { Asset = release.Asset with { Size = GitHubUpdateService.MaximumArchiveBytes + 1 } };
                await MustFail<InvalidDataException>(() => service.DownloadAndPrepareAsync(altered, Path.Combine(root, "too-large")), check,
                    "Oversized release assets must be rejected before transfer.");
                altered = release with { Asset = release.Asset with { DownloadUrl = new Uri("https://example.com/FlowLens.exe") } };
                await MustFail<InvalidDataException>(() => service.DownloadAndPrepareAsync(altered, Path.Combine(root, "wrong-host")), check,
                    "Preparation must independently reject untrusted URLs even if its release record was changed by the caller.");
                using var cancellation = new CancellationTokenSource();
                cancellation.Cancel();
                await MustFail<OperationCanceledException>(() => service.DownloadAndPrepareAsync(release, Path.Combine(root, "cancelled"), token: cancellation.Token), check,
                    "Cancellation must stop update preparation.");
                using var transferCancellation = new CancellationTokenSource();
                var cancelledStage = Path.Combine(root, "cancelled-transfer");
                await MustFail<OperationCanceledException>(() => service.DownloadAndPrepareAsync(release, cancelledStage,
                    new ImmediateProgress(_ => transferCancellation.Cancel()), transferCancellation.Token), check,
                    "Cancelling an active transfer must stop further download and extraction.");
                check(!File.Exists(Path.Combine(cancelledStage, "update.zip")) && !File.Exists(Path.Combine(cancelledStage, "FlowLens.exe")),
                    "A cancelled transfer must remove its incomplete files.");
            }

            using (var client = new HttpClient(new StubHandler(request => request.RequestUri!.Host == "api.github.com"
                ? JsonResponse(fixture.Metadata)
                : new(HttpStatusCode.OK) { Content = new ByteArrayContent(package), RequestMessage = new(HttpMethod.Get, "https://example.com/mirror.zip") })))
            using (var service = new GitHubUpdateService(client))
            {
                var release = (await service.CheckAsync(version, true))!.Release;
                await MustFail<InvalidDataException>(() => service.DownloadAndPrepareAsync(release, Path.Combine(root, "bad-redirect")), check,
                    "An asset response redirected outside GitHub must not be accepted.");
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static SemanticVersion Parse(string text)
    {
        if (!SemanticVersion.TryParse(text, out var version)) throw new InvalidOperationException("Invalid test version.");
        return version!;
    }

    private static async Task MustFail<T>(Func<Task> action, Action<bool, string> check, string message) where T : Exception
    {
        try { await action(); check(false, message); }
        catch (T) { check(true, message); }
    }

    private static byte[] Zip(params (string Name, byte[] Data)[] files)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, data) in files)
            {
                using var target = zip.CreateEntry(name).Open();
                target.Write(data);
            }
        }
        return stream.ToArray();
    }

    private static Fixture CreateFixture(string version, byte[] archive, string? digest = "auto", string? checksumText = null,
        bool draft = false, bool prerelease = false)
    {
        var tag = "v" + version;
        var name = "FlowLens-" + Parse(version).Core + "-win-x64.zip";
        var url = GitHubUpdateService.RepositoryUrl + "/releases/download/" + tag + "/";
        var assets = new List<object>
        {
            new { name, browser_download_url = url + name, size = archive.Length, digest = digest == "auto" ? "sha256:" + Convert.ToHexString(SHA256.HashData(archive)) : digest }
        };
        if (checksumText is not null)
            assets.Add(new { name = "SHA256SUMS.txt", browser_download_url = url + "SHA256SUMS.txt", size = Encoding.UTF8.GetByteCount(checksumText), digest = (string?)null });
        return new(JsonSerializer.Serialize(new { tag_name = tag, html_url = GitHubUpdateService.RepositoryUrl + "/releases/tag/" + tag, draft, prerelease, assets }), archive, checksumText);
    }

    private static StubHandler FixtureHandler(Fixture fixture) => new(request =>
    {
        if (request.RequestUri!.Host == "api.github.com") return JsonResponse(fixture.Metadata);
        if (request.RequestUri.AbsolutePath.EndsWith("/SHA256SUMS.txt", StringComparison.Ordinal))
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(fixture.Checksums!)) };
        return new(HttpStatusCode.OK) { Content = new ByteArrayContent(fixture.Archive) };
    });

    private static StubHandler WebsiteFallbackHandler(string version, byte[] archive, HttpStatusCode apiStatus = HttpStatusCode.Forbidden,
        string? releaseRedirect = null, HttpStatusCode checksumStatus = HttpStatusCode.OK, string? checksumText = null,
        string? archiveRedirect = null, long? archiveSize = null, HttpStatusCode archiveStatus = HttpStatusCode.OK) => new(request =>
    {
        if (request.RequestUri!.Host == "api.github.com") return new(apiStatus);
        if (request.RequestUri.AbsolutePath.EndsWith("/releases/latest", StringComparison.Ordinal))
            return new(HttpStatusCode.OK) { RequestMessage = new(HttpMethod.Head, releaseRedirect ?? GitHubUpdateService.RepositoryUrl + "/releases/tag/v" + version) };
        if (request.RequestUri.AbsolutePath.EndsWith("/SHA256SUMS.txt", StringComparison.Ordinal))
            return new(checksumStatus) { Content = new StringContent(checksumText ?? Convert.ToHexString(SHA256.HashData(archive)) + "  FlowLens-" + version + "-win-x64.zip\n") };
        var response = new HttpResponseMessage(archiveStatus)
        {
            Content = new ByteArrayContent(request.Method == HttpMethod.Head ? [] : archive),
            RequestMessage = new(request.Method, archiveRedirect ?? "https://release-assets.githubusercontent.com/github-production-release-asset/fixture")
        };
        if (request.Method == HttpMethod.Head) response.Content.Headers.ContentLength = archiveSize ?? archive.LongLength;
        return response;
    });

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private sealed record Fixture(string Metadata, byte[] Archive, string? Checksums);
    private sealed record RequestRecord(string Uri, string UserAgent, string? ApiVersion, string Method);

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<RequestRecord> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(new(request.RequestUri!.AbsoluteUri, request.Headers.UserAgent.ToString(),
                request.Headers.TryGetValues("X-GitHub-Api-Version", out var values) ? values.Single() : null, request.Method.Method));
            return Task.FromResult(respond(request));
        }
    }

    private sealed class ImmediateProgress(Action<GitHubUpdateProgress> report) : IProgress<GitHubUpdateProgress>
    {
        public void Report(GitHubUpdateProgress value) => report(value);
    }
}

using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FlowLens;

public sealed class SemanticVersion : IComparable<SemanticVersion>
{
    private static readonly Regex Pattern = new(
        @"^[vV]?(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$",
        RegexOptions.CultureInvariant);

    private SemanticVersion(int major, int minor, int patch, string prerelease, string metadata)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        Prerelease = prerelease;
        Metadata = metadata;
    }

    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public string Prerelease { get; }
    public string Metadata { get; }
    public bool IsPrerelease => Prerelease.Length != 0;
    public string Core => $"{Major}.{Minor}.{Patch}";

    public static bool TryParse(string? text, out SemanticVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 256)
            return false;
        var match = Pattern.Match(text.Trim());
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var major) ||
            !int.TryParse(match.Groups[2].Value, out var minor) || !int.TryParse(match.Groups[3].Value, out var patch))
            return false;
        var prerelease = match.Groups[4].Value;
        if (prerelease.Split('.').Any(part => IsNumeric(part) && part.Length > 1 && part[0] == '0'))
            return false;
        version = new(major, minor, patch, prerelease, match.Groups[5].Value);
        return true;
    }

    public int CompareTo(SemanticVersion? other)
    {
        if (other is null) return 1;
        var comparison = Major.CompareTo(other.Major);
        if (comparison != 0) return comparison;
        comparison = Minor.CompareTo(other.Minor);
        if (comparison != 0) return comparison;
        comparison = Patch.CompareTo(other.Patch);
        if (comparison != 0) return comparison;
        if (!IsPrerelease || !other.IsPrerelease)
            return IsPrerelease == other.IsPrerelease ? 0 : IsPrerelease ? -1 : 1;
        var left = Prerelease.Split('.');
        var right = other.Prerelease.Split('.');
        for (var i = 0; i < Math.Min(left.Length, right.Length); i++)
        {
            var leftNumeric = IsNumeric(left[i]);
            var rightNumeric = IsNumeric(right[i]);
            if (leftNumeric != rightNumeric) return leftNumeric ? -1 : 1;
            if (leftNumeric)
            {
                comparison = left[i].Length.CompareTo(right[i].Length);
                if (comparison != 0) return comparison;
            }
            comparison = string.CompareOrdinal(left[i], right[i]);
            if (comparison != 0) return comparison;
        }
        return left.Length.CompareTo(right.Length);
    }

    public override string ToString() => Core + (IsPrerelease ? "-" + Prerelease : "") +
        (Metadata.Length == 0 ? "" : "+" + Metadata);

    private static bool IsNumeric(string part) => part.Length != 0 && part.All(c => c is >= '0' and <= '9');
}

public enum GitHubVersionRelation { Older, Same, Newer }

public sealed record GitHubReleaseAsset(string Name, Uri DownloadUrl, long Size, string? Sha256);
public sealed record GitHubReleaseInfo(SemanticVersion Version, string Tag, Uri ReleaseUrl,
    GitHubReleaseAsset Asset, GitHubReleaseAsset? ChecksumAsset);
public sealed record GitHubUpdateResult(GitHubReleaseInfo Release, GitHubVersionRelation Relation, bool CanInstall);
public sealed record GitHubUpdateProgress(long DownloadedBytes, long TotalBytes);
public sealed record PreparedGitHubUpdate(string ExecutablePath, string ExecutableSha256, string ArchiveSha256);

/// <summary>Checks the official stable release and prepares a verified executable; never installs or launches it.</summary>
public sealed class GitHubUpdateService : IDisposable
{
    public const string RepositoryUrl = "https://github.com/mgliz/FlowLens";
    public const string LatestReleaseApiUrl = "https://api.github.com/repos/mgliz/FlowLens/releases/latest";
    public const long MaximumArchiveBytes = 512L * 1024 * 1024;
    public const long MaximumExecutableBytes = 1024L * 1024 * 1024;
    private const int MaximumMetadataBytes = 2 * 1024 * 1024;
    private const int MaximumChecksumsBytes = 64 * 1024;
    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    public GitHubUpdateService(HttpClient? client = null)
    {
        _ownsClient = client is null;
        _client = client ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<GitHubUpdateResult?> CheckAsync(string localVersion, bool isTestBuild,
        CancellationToken token = default)
    {
        if (!SemanticVersion.TryParse(localVersion, out var current))
            throw new ArgumentException("The current application version is invalid.", nameof(localVersion));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        using var request = CreateRequest(new Uri(LatestReleaseApiUrl), isApi: true);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            var fallback = await ReadOfficialReleaseWithoutApiAsync(timeout.Token).ConfigureAwait(false);
            return fallback is null ? null : CompareRelease(fallback, current!, isTestBuild);
        }
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await ReadBoundedAsync(response.Content, MaximumMetadataBytes, timeout.Token).ConfigureAwait(false));
        var root = json.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) return null;
        var tag = RequiredString(root, "tag_name");
        if (!SemanticVersion.TryParse(tag, out var version) || version!.IsPrerelease)
            return null;
        var releaseUrl = new Uri(RequiredString(root, "html_url"), UriKind.Absolute);
        if (releaseUrl.AbsoluteUri != RepositoryUrl + "/releases/tag/" + Uri.EscapeDataString(tag))
            throw new InvalidDataException("The release page does not belong to the official repository.");
        var expectedName = $"FlowLens-{version.Core}-win-x64.zip";
        var assets = root.GetProperty("assets").EnumerateArray().ToArray();
        var archive = FindAsset(assets, expectedName, tag, MaximumArchiveBytes)
            ?? throw new InvalidDataException("The release has no Windows x64 update package.");
        var checksums = FindAsset(assets, "SHA256SUMS.txt", tag, MaximumChecksumsBytes);
        if (archive.Sha256 is null && checksums is null)
            throw new InvalidDataException("The release has no SHA-256 checksum for its update package.");
        return CompareRelease(new(version, tag, releaseUrl, archive, checksums), current!, isTestBuild);
    }

    private static GitHubUpdateResult CompareRelease(GitHubReleaseInfo release, SemanticVersion current, bool isTestBuild)
    {
        var comparison = release.Version.CompareTo(current);
        var relation = comparison > 0 ? GitHubVersionRelation.Newer :
            comparison < 0 ? GitHubVersionRelation.Older : GitHubVersionRelation.Same;
        return new(release, relation, isTestBuild || comparison > 0);
    }

    private async Task<GitHubReleaseInfo?> ReadOfficialReleaseWithoutApiAsync(CancellationToken token)
    {
        using var latestRequest = CreateRequest(new Uri(RepositoryUrl + "/releases/latest"), method: HttpMethod.Head);
        using var latestResponse = await _client.SendAsync(latestRequest, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (latestResponse.StatusCode == HttpStatusCode.NotFound) return null;
        latestResponse.EnsureSuccessStatusCode();
        var releaseUrl = latestResponse.RequestMessage?.RequestUri;
        var tagPrefix = RepositoryUrl + "/releases/tag/";
        if (releaseUrl is null || !releaseUrl.AbsoluteUri.StartsWith(tagPrefix, StringComparison.Ordinal))
            throw new InvalidDataException("GitHub did not resolve an official stable release page.");
        var tag = Uri.UnescapeDataString(releaseUrl.AbsoluteUri[tagPrefix.Length..]);
        if (releaseUrl.AbsoluteUri != tagPrefix + Uri.EscapeDataString(tag) ||
            !SemanticVersion.TryParse(tag, out var version) || version!.IsPrerelease)
            throw new InvalidDataException("The latest GitHub release does not have a compatible stable version tag.");

        var name = $"FlowLens-{version.Core}-win-x64.zip";
        var downloadPrefix = RepositoryUrl + "/releases/download/" + Uri.EscapeDataString(tag) + "/";
        var checksumUrl = new Uri(downloadPrefix + "SHA256SUMS.txt");
        using var checksumRequest = CreateRequest(checksumUrl);
        using var checksumResponse = await _client.SendAsync(checksumRequest, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (checksumResponse.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidDataException("The official release has no SHA256SUMS.txt; a verified update cannot be prepared while the GitHub API is rate limited.");
        checksumResponse.EnsureSuccessStatusCode();
        ValidateAssetResponse(checksumResponse, checksumUrl);
        var checksumBytes = await ReadBoundedAsync(checksumResponse.Content, MaximumChecksumsBytes, token).ConfigureAwait(false);
        var digest = ReadChecksum(System.Text.Encoding.UTF8.GetString(checksumBytes), name);

        var archiveUrl = new Uri(downloadPrefix + Uri.EscapeDataString(name));
        using var archiveRequest = CreateRequest(archiveUrl, method: HttpMethod.Head);
        using var archiveResponse = await _client.SendAsync(archiveRequest, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (archiveResponse.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidDataException("The official release has no compatible Windows x64 update package.");
        archiveResponse.EnsureSuccessStatusCode();
        ValidateAssetResponse(archiveResponse, archiveUrl);
        if (archiveResponse.Content.Headers.ContentLength is not { } size || size <= 0 || size > MaximumArchiveBytes)
            throw new InvalidDataException("GitHub did not provide a valid size for the official update package.");
        var archive = new GitHubReleaseAsset(name, archiveUrl, size, digest);
        var checksums = new GitHubReleaseAsset("SHA256SUMS.txt", checksumUrl, checksumBytes.LongLength, null);
        ValidateAsset(archive, tag, MaximumArchiveBytes);
        ValidateAsset(checksums, tag, MaximumChecksumsBytes);
        return new(version, tag, releaseUrl, archive, checksums);
    }

    public async Task<PreparedGitHubUpdate> DownloadAndPrepareAsync(GitHubReleaseInfo release,
        string stagingDirectory, IProgress<GitHubUpdateProgress>? progress = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        ValidateAsset(release.Asset, release.Tag, MaximumArchiveBytes);
        if (release.Asset.Name != $"FlowLens-{release.Version.Core}-win-x64.zip" || release.Version.IsPrerelease ||
            !SemanticVersion.TryParse(release.Tag, out var tagVersion) || tagVersion!.CompareTo(release.Version) != 0)
            throw new InvalidDataException("The update package does not match its stable release.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        token = timeout.Token;
        var expectedHash = NormalizeSha256(release.Asset.Sha256);
        if (expectedHash is null)
        {
            if (release.ChecksumAsset is not { } checksumAsset || checksumAsset.Name != "SHA256SUMS.txt")
                throw new InvalidDataException("The update has no verified SHA-256 checksum.");
            ValidateAsset(checksumAsset, release.Tag, MaximumChecksumsBytes);
            using var checksumRequest = CreateRequest(checksumAsset.DownloadUrl);
            using var checksumResponse = await _client.SendAsync(checksumRequest, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            checksumResponse.EnsureSuccessStatusCode();
            ValidateAssetResponse(checksumResponse, checksumAsset.DownloadUrl);
            var checksumBytes = await ReadBoundedAsync(checksumResponse.Content, MaximumChecksumsBytes, token).ConfigureAwait(false);
            if (checksumBytes.LongLength != checksumAsset.Size)
                throw new InvalidDataException("The checksum file size does not match the release metadata.");
            if (checksumAsset.Sha256 is not null && !HashMatches(checksumBytes, checksumAsset.Sha256))
                throw new InvalidDataException("The checksum file does not match its SHA-256 digest.");
            expectedHash = ReadChecksum(System.Text.Encoding.UTF8.GetString(checksumBytes), release.Asset.Name);
        }

        var directory = Path.GetFullPath(stagingDirectory);
        Directory.CreateDirectory(directory);
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The update staging directory must not be a link.");
        var archivePath = Path.Combine(directory, "update.zip");
        var executablePath = Path.Combine(directory, "FlowLens.exe");
        var createdArchive = false;
        var createdExecutable = false;
        var succeeded = false;
        try
        {
            string archiveHash;
            using (var request = CreateRequest(release.Asset.DownloadUrl))
            using (var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                ValidateAssetResponse(response, release.Asset.DownloadUrl);
                if (response.Content.Headers.ContentLength is { } length && length != release.Asset.Size)
                    throw new InvalidDataException("The package size does not match the release metadata.");
                await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                await using var target = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                createdArchive = true;
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];
                long total = 0;
                int count;
                while ((count = await source.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
                {
                    total += count;
                    if (total > release.Asset.Size || total > MaximumArchiveBytes)
                        throw new InvalidDataException("The update package exceeds its declared size.");
                    hash.AppendData(buffer, 0, count);
                    await target.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                    progress?.Report(new(total, release.Asset.Size));
                }
                if (total != release.Asset.Size)
                    throw new InvalidDataException("The update package download was incomplete.");
                archiveHash = Convert.ToHexString(hash.GetHashAndReset());
                if (!string.Equals(archiveHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The downloaded package failed SHA-256 verification.");
            }

            using (var zip = ZipFile.OpenRead(archivePath))
            {
                var candidates = zip.Entries.Where(entry => string.Equals(entry.FullName, "FlowLens.exe", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (candidates.Length != 1)
                    throw new InvalidDataException("The package must contain exactly one FlowLens.exe at its root.");
                var executable = candidates[0];
                if (executable.Length < 1024 || executable.Length > MaximumExecutableBytes ||
                    ((executable.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                    throw new InvalidDataException("The package executable has an invalid size or file type.");
                await using var source = executable.Open();
                await using var target = new FileStream(executablePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                createdExecutable = true;
                var buffer = new byte[81920];
                long total = 0;
                int count;
                while ((count = await source.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
                {
                    total += count;
                    if (total > executable.Length || total > MaximumExecutableBytes)
                        throw new InvalidDataException("The extracted executable exceeds its declared size.");
                    await target.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                }
                if (total != executable.Length)
                    throw new InvalidDataException("The package executable is incomplete.");
            }
            token.ThrowIfCancellationRequested();
            var fileVersion = FileVersionInfo.GetVersionInfo(executablePath);
            if (string.IsNullOrEmpty(fileVersion.FileVersion) || fileVersion.FileMajorPart != release.Version.Major || fileVersion.FileMinorPart != release.Version.Minor ||
                fileVersion.FileBuildPart != release.Version.Patch || fileVersion.FilePrivatePart != 0)
                throw new InvalidDataException("The executable version does not match the selected release.");
            await using var verifiedFile = new FileStream(executablePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            var executableHash = Convert.ToHexString(await SHA256.HashDataAsync(verifiedFile, token).ConfigureAwait(false));
            succeeded = true;
            return new(executablePath, executableHash, archiveHash);
        }
        finally
        {
            if (createdArchive) TryDelete(archivePath);
            if (createdExecutable && !succeeded) TryDelete(executablePath);
        }
    }

    public void Dispose()
    {
        if (_ownsClient) _client.Dispose();
    }

    private static HttpRequestMessage CreateRequest(Uri uri, bool isApi = false, HttpMethod? method = null)
    {
        var request = new HttpRequestMessage(method ?? HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("FlowLens-Updater/1.0");
        if (isApi)
        {
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        }
        return request;
    }

    private static GitHubReleaseAsset? FindAsset(JsonElement[] assets, string name, string tag, long maximum)
    {
        var matches = assets.Where(asset => RequiredString(asset, "name") == name).ToArray();
        if (matches.Length == 0) return null;
        if (matches.Length != 1) throw new InvalidDataException("The release has duplicate update assets.");
        var item = matches[0];
        string? digest = null;
        if (item.TryGetProperty("digest", out var value) && value.ValueKind != JsonValueKind.Null)
        {
            var raw = value.GetString();
            if (!string.IsNullOrEmpty(raw))
            {
                if (!raw.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The release uses an unsupported digest format.");
                digest = NormalizeSha256(raw[7..]);
            }
        }
        var result = new GitHubReleaseAsset(name, new Uri(RequiredString(item, "browser_download_url"), UriKind.Absolute), item.GetProperty("size").GetInt64(), digest);
        ValidateAsset(result, tag, maximum);
        return result;
    }

    private static void ValidateAsset(GitHubReleaseAsset asset, string tag, long maximum)
    {
        var expected = RepositoryUrl + "/releases/download/" + Uri.EscapeDataString(tag) + "/" + Uri.EscapeDataString(asset.Name);
        if (!asset.DownloadUrl.IsAbsoluteUri || asset.DownloadUrl.AbsoluteUri != expected || asset.Size <= 0 || asset.Size > maximum)
            throw new InvalidDataException("The update asset URL or size is invalid.");
        _ = NormalizeSha256(asset.Sha256);
    }

    private static void ValidateAssetResponse(HttpResponseMessage response, Uri originalUrl)
    {
        var uri = response.RequestMessage?.RequestUri ?? originalUrl;
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
            (uri != originalUrl && uri.Host != "release-assets.githubusercontent.com" && uri.Host != "objects.githubusercontent.com"))
            throw new InvalidDataException("The update download was redirected outside GitHub's asset service.");
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maximum, CancellationToken token)
    {
        if (content.Headers.ContentLength is { } length && length > maximum)
            throw new InvalidDataException("The update response is too large.");
        await using var source = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var target = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await source.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            if (target.Length + count > maximum) throw new InvalidDataException("The update response is too large.");
            target.Write(buffer, 0, count);
        }
        return target.ToArray();
    }

    private static string RequiredString(JsonElement item, string property) => item.GetProperty(property).GetString()
        ?? throw new InvalidDataException("A required release field is missing.");

    private static string? NormalizeSha256(string? value)
    {
        if (value is null) return null;
        if (value.Length != 64 || value.Any(c => !char.IsAsciiHexDigit(c)))
            throw new InvalidDataException("The release SHA-256 checksum is invalid.");
        return value.ToUpperInvariant();
    }

    private static bool HashMatches(byte[] bytes, string expected) =>
        string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), NormalizeSha256(expected), StringComparison.Ordinal);

    private static string ReadChecksum(string text, string name)
    {
        string? result = null;
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim().TrimStart('\uFEFF');
            var match = Regex.Match(trimmed, @"^([0-9a-fA-F]{64})\s+\*?(.+)$", RegexOptions.CultureInvariant);
            if (!match.Success || match.Groups[2].Value != name) continue;
            if (result is not null) throw new InvalidDataException("The checksum file contains duplicate package entries.");
            result = NormalizeSha256(match.Groups[1].Value);
        }
        return result ?? throw new InvalidDataException("The checksum file does not contain the update package.");
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

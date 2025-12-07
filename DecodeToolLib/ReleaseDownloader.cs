using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;

namespace DecodeToolLib
{
    /// <summary>
    /// Downloads the latest release asset matching a keyword from GitHub and extracts it to a local folder.
    /// </summary>
    public static class ReleaseDownloader
    {
        private const string Owner = "oyvindln";
        private const string Repo = "vhs-decode";
        private const string DefaultKeyword = "decode_suite_full";
        private const string LatestReleaseApi = "https://api.github.com/repos/{0}/{1}/releases/latest";

        /// <summary>
        /// Download the latest release asset that contains <paramref name="assetKeyword"/> in its name,
        /// create a folder named after the release version under the <paramref name="targetFolderName"/> folder
        /// in the current working directory, and extract the asset there if it is a ZIP.
        /// Returns the release version string (e.g. tag_name).
        /// </summary>
        /// <param name="assetKeyword">Case-insensitive substring to match asset name. Default "decode_suite_full".</param>
        /// <param name="targetFolderName">Parent folder name under current working directory. Default "binary".</param>
        /// <param name="progress">Optional progress reporter for download percentage (0.0 - 100.0).</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The release version string (from release "tag_name" or fallback value).</returns>
        public static async Task<string> DownloadAndExtractLatestAsync(
            string assetKeyword = DefaultKeyword,
            string targetFolderName = "binary",
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default)
        {
            // Prepare HTTP client with required headers for GitHub API
            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("DecodeToolLib/1.0 (+https://github.com)");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            var apiUrl = string.Format(LatestReleaseApi, Owner, Repo);

            // Fetch latest release metadata
            using var relResp = await http.GetAsync(apiUrl, cancellationToken).ConfigureAwait(false);
            relResp.EnsureSuccessStatusCode();

            await using var relStream = await relResp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(relStream, cancellationToken: cancellationToken).ConfigureAwait(false);

            // Determine release version (prefer tag_name)
            string version;
            if (doc.RootElement.TryGetProperty("tag_name", out var tagElem) && !string.IsNullOrEmpty(tagElem.GetString()))
            {
                version = tagElem.GetString()!;
            }
            else if (doc.RootElement.TryGetProperty("name", out var nameElem) && !string.IsNullOrEmpty(nameElem.GetString()))
            {
                version = nameElem.GetString()!;
            }
            else
            {
                version = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            }

            if (!doc.RootElement.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException("Latest release contains no assets.");
            }

            JsonElement? chosen = null;
            foreach (var a in assets.EnumerateArray())
            {
                if (a.TryGetProperty("name", out var nameElem))
                {
                    var name = nameElem.GetString() ?? string.Empty;
                    if (name.Contains(assetKeyword, StringComparison.OrdinalIgnoreCase))
                    {
                        chosen = a;
                        break;
                    }
                }
            }

            if (chosen == null)
            {
                throw new InvalidOperationException($"No asset found containing keyword '{assetKeyword}' in its name.");
            }

            if (!chosen.Value.TryGetProperty("browser_download_url", out var urlElem))
            {
                throw new InvalidOperationException("Chosen asset does not contain a download URL.");
            }

            var downloadUrl = urlElem.GetString() ?? throw new InvalidOperationException("Invalid download URL.");
            var assetName = chosen.Value.GetProperty("name").GetString() ?? "asset.bin";

            // Prepare target directory: ./{targetFolderName}/{sanitizedVersion}/
            var cwd = Directory.GetCurrentDirectory();

            // sanitize version for filesystem
            var invalids = Path.GetInvalidFileNameChars();
            var sanitizedVersion = string.Join("_", version.Split(invalids, StringSplitOptions.RemoveEmptyEntries));
            if (string.IsNullOrWhiteSpace(sanitizedVersion))
            {
                sanitizedVersion = "unknown_version";
            }

            var versionedTargetDir = Path.Combine(cwd, targetFolderName, sanitizedVersion);
            Directory.CreateDirectory(versionedTargetDir);

            // Create temp file with same extension as asset to preserve type
            var ext = Path.GetExtension(assetName) ?? string.Empty;
            var tempFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ext);

            // Download asset with streaming and optional progress reporting
            using (var response = await http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();

                var contentLength = response.Content.Headers.ContentLength;
                await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var fs = File.Create(tempFile);

                var buffer = new byte[81920];
                long totalRead = 0;
                int read;
                while ((read = await contentStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await fs.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    totalRead += read;

                    if (contentLength.HasValue && progress != null)
                    {
                        double percent = Math.Round((double)totalRead / contentLength.Value * 100.0, 2);
                        progress.Report(percent);
                    }
                }

                progress?.Report(100.0);
            }

            try
            {
                // If zip, extract into versioned folder. Otherwise copy the downloaded file into that folder.
                if (ext.Equals(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    ZipFile.ExtractToDirectory(tempFile, versionedTargetDir, overwriteFiles: true);
                }
                else
                {
                    var dest = Path.Combine(versionedTargetDir, assetName);
                    File.Copy(tempFile, dest, overwrite: true);
                    throw new NotSupportedException($"Downloaded asset saved to '{dest}', but automatic extraction is only implemented for .zip files. To support other formats, extend this class (e.g. use SharpCompress or 7z).");
                }
            }
            finally
            {
                // Cleanup temporary file (best-effort)
                try
                {
                    if (File.Exists(tempFile))
                    {
                        File.Delete(tempFile);
                    }
                }
                catch
                {
                    // ignore cleanup errors
                }
            }

            // Return the original release version string (tag_name or fallback)
            return version;
        }
    }
}

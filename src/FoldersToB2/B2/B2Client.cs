using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Serilog;

namespace FoldersToB2.B2;

public class B2Client : IDisposable
{
    private readonly HttpClient _http = new();
    private readonly string _keyId;
    private readonly string _key;
    private readonly string _bucketId;
    private B2AuthResponse? _auth;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public long RecommendedPartSize => _auth?.RecommendedPartSize ?? 100_000_000;

    public B2Client(string keyId, string key, string bucketId)
    {
        _keyId = keyId;
        _key = key;
        _bucketId = bucketId;
    }

    public async Task AuthorizeAsync()
    {
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_keyId}:{_key}"));
        var request = new HttpRequestMessage(HttpMethod.Get, "https://api.backblazeb2.com/b2api/v2/b2_authorize_account");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);

        var response = await _http.SendAsync(request);
        await EnsureSuccessAsync(response, "b2_authorize_account");

        var json = await response.Content.ReadAsStringAsync();
        _auth = JsonSerializer.Deserialize<B2AuthResponse>(json, JsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize auth response");

        Log.Information("Authorized with B2 API (recommended part size: {PartSize} bytes)", _auth.RecommendedPartSize);
    }

    private void EnsureAuthorized()
    {
        if (_auth is null)
            throw new InvalidOperationException("Not authorized. Call AuthorizeAsync first.");
    }

    public async Task<B2FileResponse> UploadFileAsync(string b2FileName, string localFilePath, CancellationToken ct = default)
    {
        EnsureAuthorized();

        var fileInfo = new FileInfo(localFilePath);
        if (fileInfo.Length >= RecommendedPartSize)
            return await UploadLargeFileAsync(b2FileName, localFilePath, ct);

        // Get upload URL
        var uploadUrl = await GetUploadUrlAsync(ct);

        // Read file and compute SHA1
        var fileBytes = await File.ReadAllBytesAsync(localFilePath, ct);
        var sha1 = ComputeSha1Hex(fileBytes);

        var encodedFileName = EncodeB2FileName(b2FileName);

        var request = new HttpRequestMessage(HttpMethod.Post, uploadUrl.UploadUrl);
        request.Headers.TryAddWithoutValidation("Authorization", uploadUrl.AuthorizationToken);
        request.Headers.TryAddWithoutValidation("X-Bz-File-Name", encodedFileName);
        request.Headers.TryAddWithoutValidation("X-Bz-Content-Sha1", sha1);
        request.Headers.TryAddWithoutValidation("X-Bz-Info-src_last_modified_millis",
            new DateTimeOffset(fileInfo.LastWriteTimeUtc).ToUnixTimeMilliseconds().ToString());

        request.Content = new ByteArrayContent(fileBytes);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("b2/x-auto");
        request.Content.Headers.ContentLength = fileBytes.Length;

        var response = await _http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, "b2_upload_file");

        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<B2FileResponse>(json, JsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize upload response");
    }

    private async Task<B2FileResponse> UploadLargeFileAsync(string b2FileName, string localFilePath, CancellationToken ct)
    {
        EnsureAuthorized();

        var encodedFileName = EncodeB2FileName(b2FileName);

        // Start large file
        var startBody = new { bucketId = _bucketId, fileName = encodedFileName, contentType = "b2/x-auto" };
        var startResponse = await PostJsonAsync<B2StartLargeFileResponse>(
            $"{_auth!.ApiUrl}/b2api/v2/b2_start_large_file", startBody, ct);

        var fileId = startResponse.FileId;
        var partSize = _auth.RecommendedPartSize;
        var fileLength = new FileInfo(localFilePath).Length;
        var partCount = (int)Math.Ceiling((double)fileLength / partSize);
        var partSha1List = new List<string>();

        Log.Information("Starting large file upload: {FileName} ({Parts} parts, {Size} bytes)",
            b2FileName, partCount, fileLength);

        try
        {
            await using var fileStream = File.OpenRead(localFilePath);
            for (int partNumber = 1; partNumber <= partCount; partNumber++)
            {
                ct.ThrowIfCancellationRequested();

                // Get a fresh upload part URL for each part
                var partUrl = await PostJsonAsync<B2UploadPartUrlResponse>(
                    $"{_auth.ApiUrl}/b2api/v2/b2_get_upload_part_url",
                    new { fileId }, ct);

                var remaining = fileLength - fileStream.Position;
                var currentPartSize = (int)Math.Min(partSize, remaining);
                var buffer = new byte[currentPartSize];
                await fileStream.ReadExactlyAsync(buffer, 0, currentPartSize, ct);

                var sha1 = ComputeSha1Hex(buffer);
                partSha1List.Add(sha1);

                var request = new HttpRequestMessage(HttpMethod.Post, partUrl.UploadUrl);
                request.Headers.TryAddWithoutValidation("Authorization", partUrl.AuthorizationToken);
                request.Headers.TryAddWithoutValidation("X-Bz-Part-Number", partNumber.ToString());
                request.Headers.TryAddWithoutValidation("X-Bz-Content-Sha1", sha1);

                request.Content = new ByteArrayContent(buffer);
                request.Content.Headers.ContentLength = currentPartSize;

                var response = await _http.SendAsync(request, ct);
                await EnsureSuccessAsync(response, $"b2_upload_part ({partNumber}/{partCount})");

                Log.Debug("Uploaded part {Part}/{Total} of {FileName}", partNumber, partCount, b2FileName);
            }
        }
        catch
        {
            // Cancel the large file on failure so it doesn't linger
            try
            {
                await PostJsonAsync<object>(
                    $"{_auth.ApiUrl}/b2api/v2/b2_cancel_large_file",
                    new { fileId }, CancellationToken.None);
                Log.Information("Cancelled incomplete large file: {FileId}", fileId);
            }
            catch (Exception cancelEx)
            {
                Log.Warning("Failed to cancel large file {FileId}: {Error}", fileId, cancelEx.Message);
            }
            throw;
        }

        // Finish large file
        var finishResponse = await PostJsonAsync<B2FileResponse>(
            $"{_auth.ApiUrl}/b2api/v2/b2_finish_large_file",
            new { fileId, partSha1Array = partSha1List }, ct);

        Log.Information("Completed large file upload: {FileName}", b2FileName);
        return finishResponse;
    }

    public async Task<B2ListFileVersionsResponse> ListFileVersionsAsync(
        string? startFileName = null, string? startFileId = null,
        int maxFileCount = 1000, CancellationToken ct = default)
    {
        EnsureAuthorized();

        var body = new Dictionary<string, object>
        {
            ["bucketId"] = _bucketId,
            ["maxFileCount"] = maxFileCount
        };
        if (startFileName is not null) body["startFileName"] = startFileName;
        if (startFileId is not null) body["startFileId"] = startFileId;

        return await PostJsonAsync<B2ListFileVersionsResponse>(
            $"{_auth!.ApiUrl}/b2api/v2/b2_list_file_versions", body, ct);
    }

    public async Task DeleteFileVersionAsync(string fileName, string fileId, CancellationToken ct = default)
    {
        EnsureAuthorized();

        await PostJsonAsync<object>(
            $"{_auth!.ApiUrl}/b2api/v2/b2_delete_file_version",
            new { fileName, fileId }, ct);
    }

    private async Task<B2UploadUrlResponse> GetUploadUrlAsync(CancellationToken ct = default)
    {
        return await PostJsonAsync<B2UploadUrlResponse>(
            $"{_auth!.ApiUrl}/b2api/v2/b2_get_upload_url",
            new { bucketId = _bucketId }, ct);
    }

    private async Task<T> PostJsonAsync<T>(string url, object body, CancellationToken ct = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.TryAddWithoutValidation("Authorization", _auth!.AuthorizationToken);

        var jsonBody = JsonSerializer.Serialize(body, JsonOptions);
        request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

        var response = await _http.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            Log.Error("B2 API error at {Url}: {StatusCode} {Body}", url, response.StatusCode, json);
            response.EnsureSuccessStatusCode();
        }

        return JsonSerializer.Deserialize<T>(json, JsonOptions)
            ?? throw new InvalidOperationException($"Failed to deserialize response from {url}");
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string operation)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            Log.Error("B2 {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, body);
            throw new HttpRequestException(
                $"B2 {operation} failed with {response.StatusCode}: {body}",
                null, response.StatusCode);
        }
    }

    /// <summary>
    /// Encodes a B2 file name per B2 rules: percent-encode UTF-8 chars, allow / unencoded.
    /// </summary>
    private static string EncodeB2FileName(string fileName)
    {
        return Uri.EscapeDataString(fileName).Replace("%2F", "/");
    }

    private static string ComputeSha1Hex(byte[] data)
    {
        var hash = SHA1.HashData(data);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public void Dispose() => _http.Dispose();
}

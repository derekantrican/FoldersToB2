namespace FoldersToB2.B2;

public class B2AuthResponse
{
    public string AuthorizationToken { get; set; } = "";
    public string ApiUrl { get; set; } = "";
    public string DownloadUrl { get; set; } = "";
    public long RecommendedPartSize { get; set; }
    public long AbsoluteMinimumPartSize { get; set; }
}

public class B2UploadUrlResponse
{
    public string BucketId { get; set; } = "";
    public string UploadUrl { get; set; } = "";
    public string AuthorizationToken { get; set; } = "";
}

public class B2FileResponse
{
    public string FileId { get; set; } = "";
    public string FileName { get; set; } = "";
    public long ContentLength { get; set; }
    public string ContentSha1 { get; set; } = "";
    public string Action { get; set; } = "";
    public long UploadTimestamp { get; set; }
}

public class B2StartLargeFileResponse
{
    public string FileId { get; set; } = "";
}

public class B2UploadPartUrlResponse
{
    public string FileId { get; set; } = "";
    public string UploadUrl { get; set; } = "";
    public string AuthorizationToken { get; set; } = "";
}

public class B2ListFileVersionsResponse
{
    public List<B2FileResponse> Files { get; set; } = new();
    public string? NextFileName { get; set; }
    public string? NextFileId { get; set; }
}

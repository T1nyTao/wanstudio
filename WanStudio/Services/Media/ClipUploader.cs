using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WanStudio.Services.Media;

/// <summary>
/// Uploads a local clip to DashScope's temporary storage and returns an
/// <c>oss://</c> URL that the model API can consume.
///
/// Official flow (阿里云百炼「上传本地文件获取临时URL」):
///   1. GET  {base}/api/v1/uploads?action=getPolicy&amp;model={model}
///   2. POST {upload_host}  multipart/form-data
///   3. When calling the model with an oss:// URL, the request MUST carry
///      <c>X-DashScope-OssResourceResolve: enable</c>.
///
/// Constraints that matter here (from the official doc):
///   - the file is bound to the model given at policy time; the later model call
///     must use that same model, otherwise the URL cannot be resolved;
///   - the temporary URL lives 48h;
///   - the upload and the model call must belong to the same Alibaba Cloud account.
///
/// This exists because chained continuation needs the tail of an already
/// generated clip as <c>first_clip</c>, and a locally cut clip has no public URL.
/// </summary>
public interface IClipUploader
{
    /// <summary>Uploads <paramref name="localPath"/> and returns an oss:// URL.</summary>
    Task<string> UploadAsync(string localPath, string model, CancellationToken ct = default);
}

public sealed class DashScopeClipUploader : IClipUploader, IDisposable
{
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _baseUrl;

    public DashScopeClipUploader(string apiKey, string? baseUrl = null)
    {
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        _baseUrl = (baseUrl ?? AppSettings.DefaultEndpoint).TrimEnd('/');
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    }

    /// <summary>Last oss:// URL produced, for evidence/logging (no secret inside).</summary>
    public string? LastOssUrl { get; private set; }

    public async Task<string> UploadAsync(string localPath, string model, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(localPath))
            throw new ArgumentException("上传需要本地文件路径。", nameof(localPath));
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("上传必须指定模型名（文件与模型绑定）。", nameof(model));

        var full = Path.GetFullPath(localPath);
        if (!File.Exists(full))
            throw new FileNotFoundException("待上传的片段不存在。", full);

        var policy = await GetPolicyAsync(model, ct).ConfigureAwait(false);
        var ossUrl = await PostToOssAsync(policy, full, ct).ConfigureAwait(false);
        LastOssUrl = ossUrl;
        return ossUrl;
    }

    private async Task<UploadPolicy> GetPolicyAsync(string model, CancellationToken ct)
    {
        var url = $"{_baseUrl}/api/v1/uploads?action=getPolicy&model={Uri.EscapeDataString(model)}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_apiKey}");
        req.Headers.TryAddWithoutValidation("Content-Type", "application/json");

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var head = json.Length > 400 ? json[..400] : json;
            throw new InvalidOperationException($"获取上传凭证失败 {(int)resp.StatusCode}：{head}");
        }

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data))
            throw new InvalidOperationException($"上传凭证响应缺少 data 字段：{json}");

        string Need(string name) =>
            data.TryGetProperty(name, out var v) && v.GetString() is { Length: > 0 } s
                ? s
                : throw new InvalidOperationException($"上传凭证缺少字段 {name}：{json}");

        return new UploadPolicy(
            UploadDir: Need("upload_dir"),
            OssAccessKeyId: Need("oss_access_key_id"),
            Signature: Need("signature"),
            Policy: Need("policy"),
            ObjectAcl: data.TryGetProperty("x_oss_object_acl", out var acl) ? acl.GetString() ?? "" : "",
            ForbidOverwrite: data.TryGetProperty("x_oss_forbid_overwrite", out var fo) ? fo.GetString() ?? "" : "",
            UploadHost: Need("upload_host"));
    }

    private async Task<string> PostToOssAsync(UploadPolicy policy, string localPath, CancellationToken ct)
    {
        // OSS 的 object key 不要带中文/空格：用固定的 ASCII 文件名，
        // 原名只留在本地，避免服务端对 key 做编码后取不到对象。
        var ext = Path.GetExtension(localPath);
        if (string.IsNullOrEmpty(ext)) ext = ".bin";
        var fileName = $"clip-{Guid.NewGuid():N}{ext}";
        var key = $"{policy.UploadDir.TrimEnd('/')}/{fileName}";

        // 手工拼 multipart body，不用 MultipartFormDataContent。
        //
        // 为什么：MultipartFormDataContent 包 StreamContent 时，HttpClient 无法预知
        // 总长度就会退化成 Transfer-Encoding: chunked。OSS 的 PostObject 不接受
        // chunked 上传，直接回 400 MalformedPOSTRequest（"not well-formed
        // multipart/form-data"）——实测就是这条。
        // 手工拼可以写出确定的 Content-Length，并保证边界符与 Content-Type 完全一致。
        var boundary = "----WanStudioBoundary" + Guid.NewGuid().ToString("N");

        using var body = new MemoryStream();
        void WriteText(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            body.Write(bytes, 0, bytes.Length);
        }

        void WriteField(string name, string value)
        {
            WriteText($"--{boundary}\r\n");
            WriteText($"Content-Disposition: form-data; name=\"{name}\"\r\n\r\n");
            WriteText(value);
            WriteText("\r\n");
        }

        // 顺序按 OSS PostObject 要求：普通字段在前，file 必须最后。
        WriteField("OSSAccessKeyId", policy.OssAccessKeyId);
        WriteField("Signature", policy.Signature);
        WriteField("policy", policy.Policy);
        if (!string.IsNullOrEmpty(policy.ObjectAcl)) WriteField("x-oss-object-acl", policy.ObjectAcl);
        if (!string.IsNullOrEmpty(policy.ForbidOverwrite)) WriteField("x-oss-forbid-overwrite", policy.ForbidOverwrite);
        WriteField("key", key);
        WriteField("success_action_status", "200");

        // 文件部分
        WriteText($"--{boundary}\r\n");
        WriteText($"Content-Disposition: form-data; name=\"file\"; filename=\"{fileName}\"\r\n");
        WriteText("Content-Type: application/octet-stream\r\n\r\n");

        await using (var stream = File.OpenRead(localPath))
        {
            await stream.CopyToAsync(body, ct).ConfigureAwait(false);
        }
        WriteText("\r\n");
        WriteText($"--{boundary}--\r\n");

        var payload = body.ToArray();

        using var content = new ByteArrayContent(payload);
        // 边界必须与 Content-Type 里声明的完全一致（不要加引号）。
        content.Headers.TryAddWithoutValidation("Content-Type", $"multipart/form-data; boundary={boundary}");

        // 必须显式关掉 Expect: 100-continue。
        // HttpClient 在 body 较大时会自动加这个头去问服务端「可以发了吗」；
        // OSS 的 PostObject 不做这个握手，客户端会卡在等 100 或直接读到被断开的连接
        // （表现为 "The response ended prematurely"）。
        using var req = new HttpRequestMessage(HttpMethod.Post, policy.UploadHost) { Content = content };
        req.Headers.ExpectContinue = false;

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var respBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var head = respBody.Length > 400 ? respBody[..400] : respBody;
            throw new InvalidOperationException($"上传到临时存储失败 {(int)resp.StatusCode}：{head}");
        }

        return $"oss://{key}";
    }

    private sealed record UploadPolicy(
        string UploadDir,
        string OssAccessKeyId,
        string Signature,
        string Policy,
        string ObjectAcl,
        string ForbidOverwrite,
        string UploadHost);

    public void Dispose() => _http.Dispose();
}

/// <summary>
/// Local tail extraction used to build the <c>first_clip</c> anchor.
/// The API accepts 2..10s for first_clip, so a longer segment must be trimmed
/// down to its own tail before upload.
/// </summary>
public static class ClipAnchor
{
    /// <summary>first_clip must be at least this long.</summary>
    public const double MinAnchorSeconds = 2.0;

    /// <summary>first_clip must not exceed this length.</summary>
    public const double MaxAnchorSeconds = 10.0;

    /// <summary>Default tail length used when the source is long enough.</summary>
    public const double DefaultAnchorSeconds = 6.0;

    /// <summary>
    /// How many seconds of tail to take from a clip of <paramref name="durationSeconds"/>
    /// so the result satisfies the first_clip window.
    /// </summary>
    public static double TailSecondsFor(double durationSeconds, double wanted = DefaultAnchorSeconds)
    {
        if (durationSeconds <= 0) return 0;
        var take = Math.Clamp(wanted, MinAnchorSeconds, MaxAnchorSeconds);
        return Math.Min(take, durationSeconds);
    }

    /// <summary>True when the clip can serve as a first_clip without any trimming.</summary>
    public static bool CanBeUsedWhole(double durationSeconds)
        => durationSeconds >= MinAnchorSeconds && durationSeconds <= MaxAnchorSeconds;
}

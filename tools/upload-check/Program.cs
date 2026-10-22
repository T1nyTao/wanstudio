using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

// Protocol probe for the "upload a local file, get a temporary oss:// URL" flow
// that chain continuation (first_clip) needs.
//
//   getPolicy -> multipart POST to upload_host -> oss://<upload_dir>/<filename>
//             -> submit video-synthesis with X-DashScope-OssResourceResolve: enable
//             -> poll to SUCCEEDED
//
// It then exercises the negative path (oss:// without the resolve header, expect 400).
//
// Deliberately standalone (bare HttpClient, no WanStudio sources): the point is to
// prove the wire protocol against the mock, and WanStudio's own
// DashScopeClient.SubmitAsync adds the resolve header automatically whenever the
// payload contains "oss://", so the app itself can never reach the 400 branch.
// The upload uses StreamContent exactly like WanStudio's DashScopeClipUploader does,
// which makes the request body length unknown and forces chunked transfer encoding --
// a path a ByteArrayContent probe would silently skip.

const string Model = "wan2.7-i2v";
const string Prompt = "延续上一段的主体与镜头运动，动作自然衔接。";

var baseUrl = (args.Length > 0 ? args[0] : "http://127.0.0.1:8788").TrimEnd('/');
var apiKey = args.Length > 1 ? args[1] : "demo-key";
var localFile = args.Length > 2 ? args[2] : "fixtures/long-20s.mp4";

Console.WriteLine($"baseUrl={baseUrl} key={(apiKey.Length > 8 ? apiKey[..4] + "****" : "****")} file={localFile}");

if (!File.Exists(localFile))
{
    Console.WriteLine($"ERROR local file not found: {Path.GetFullPath(localFile)}");
    return 2;
}

var fileName = Path.GetFileName(localFile);
var fileBytes = new FileInfo(localFile).Length;
Console.WriteLine($"local file={Path.GetFullPath(localFile)} bytes={fileBytes}");

using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

var submitUrl = $"{baseUrl}/api/v1/services/aigc/video-generation/video-synthesis";

// ---------- step 1: getPolicy ----------
Console.WriteLine();
Console.WriteLine("=== step 1: getPolicy ===");
var policyUrl = $"{baseUrl}/api/v1/uploads?action=getPolicy&model={Model}";
using var policyResp = await http.GetAsync(policyUrl);
var policyJson = await policyResp.Content.ReadAsStringAsync();
Console.WriteLine($"GET {policyUrl} -> {(int)policyResp.StatusCode}");
Console.WriteLine(policyJson);
if (!policyResp.IsSuccessStatusCode)
{
    Console.WriteLine("ERROR getPolicy failed; stopping.");
    return 3;
}

using var policyDoc = JsonDocument.Parse(policyJson);
var data = policyDoc.RootElement.GetProperty("data");
string PolicyField(string name) => data.TryGetProperty(name, out var v) ? (v.GetString() ?? "") : "";

var uploadDir = PolicyField("upload_dir");
var uploadHost = PolicyField("upload_host");
var ossKey = $"{uploadDir}/{fileName}";
var ossUrl = $"oss://{ossKey}";

Console.WriteLine($"upload_dir  = {uploadDir}");
Console.WriteLine($"upload_host = {uploadHost}");
Console.WriteLine($"oss key     = {ossKey}");
if (uploadDir.Length == 0 || uploadHost.Length == 0)
{
    Console.WriteLine("ERROR policy is missing upload_dir/upload_host; stopping.");
    return 3;
}

// ---------- step 2: multipart upload (streamed, like the app) ----------
Console.WriteLine();
Console.WriteLine("=== step 2: multipart upload to upload_host ===");
await using (var stream = File.OpenRead(localFile))
{
    using var form = new MultipartFormDataContent();

    void Field(string name, string value) => form.Add(new StringContent(value, Encoding.UTF8), name);

    Field("OSSAccessKeyId", PolicyField("oss_access_key_id"));
    Field("Signature", PolicyField("signature"));
    Field("policy", PolicyField("policy"));
    Field("x-oss-object-acl", PolicyField("x_oss_object_acl"));
    Field("x-oss-forbid-overwrite", PolicyField("x_oss_forbid_overwrite"));
    Field("key", ossKey);
    Field("success_action_status", "200");

    var filePart = new StreamContent(stream);
    filePart.Headers.TryAddWithoutValidation("Content-Type", "application/octet-stream");
    form.Add(filePart, "file", fileName);

    Console.WriteLine("multipart fields: " + string.Join(", ",
        form.Select(p => p.Headers.ContentDisposition?.Name?.Trim('"') ?? "?")));
    Console.WriteLine($"file part: name=file filename={fileName} bytes={fileBytes} (streamed -> chunked transfer)");

    using var uploadResp = await http.PostAsync(uploadHost, form);
    var uploadBody = await uploadResp.Content.ReadAsStringAsync();
    Console.WriteLine($"POST {uploadHost} -> {(int)uploadResp.StatusCode} {uploadResp.StatusCode} body={(uploadBody.Length == 0 ? "<empty>" : uploadBody)}");
    if (!uploadResp.IsSuccessStatusCode)
    {
        Console.WriteLine("ERROR upload failed; stopping.");
        return 4;
    }
}

Console.WriteLine($"temporary url = {ossUrl}");

// ---------- step 3: submit with the resolve header ----------
Console.WriteLine();
Console.WriteLine("=== step 3: submit video-synthesis with oss:// (X-DashScope-OssResourceResolve: enable) ===");
var submitBody = BuildSubmitBody(ossUrl);
Console.WriteLine("request body: " + submitBody);

using var submitReq = new HttpRequestMessage(HttpMethod.Post, submitUrl);
submitReq.Headers.TryAddWithoutValidation("X-DashScope-Async", "enable");
submitReq.Headers.TryAddWithoutValidation("X-DashScope-OssResourceResolve", "enable");
submitReq.Content = new StringContent(submitBody, Encoding.UTF8, "application/json");
using var submitResp = await http.SendAsync(submitReq);
var submitJson = await submitResp.Content.ReadAsStringAsync();
Console.WriteLine($"-> {(int)submitResp.StatusCode} {submitResp.StatusCode}");
Console.WriteLine(submitJson);
if (!submitResp.IsSuccessStatusCode)
{
    Console.WriteLine("ERROR submit failed; stopping.");
    return 5;
}

var taskId = ExtractTaskId(submitJson);
if (taskId is null)
{
    Console.WriteLine("ERROR no task_id in submit response; stopping.");
    return 5;
}

// ---------- step 4: poll to terminal ----------
Console.WriteLine();
Console.WriteLine("=== step 4: poll until terminal ===");
Console.WriteLine($"task_id={taskId}");
for (var i = 1; i <= 8; i++)
{
    using var pollResp = await http.GetAsync($"{baseUrl}/api/v1/tasks/{taskId}");
    var pollJson = await pollResp.Content.ReadAsStringAsync();
    using var pollDoc = JsonDocument.Parse(pollJson);
    var output = pollDoc.RootElement.GetProperty("output");
    var status = output.GetProperty("task_status").GetString() ?? "UNKNOWN";
    string? videoUrl = output.TryGetProperty("video_url", out var vu) ? vu.GetString() : null;
    Console.WriteLine($"poll[{i}] status={status} url={videoUrl}");
    if (status is "SUCCEEDED" or "FAILED" or "CANCELED" or "CANCELLED")
    {
        break;
    }

    await Task.Delay(200);
}

// ---------- step 5: negative path, no resolve header ----------
Console.WriteLine();
Console.WriteLine("=== negative path: oss:// WITHOUT X-DashScope-OssResourceResolve (expect 400) ===");
using var negReq = new HttpRequestMessage(HttpMethod.Post, submitUrl);
negReq.Headers.TryAddWithoutValidation("X-DashScope-Async", "enable");
// X-DashScope-OssResourceResolve deliberately omitted.
negReq.Content = new StringContent(BuildSubmitBody(ossUrl), Encoding.UTF8, "application/json");
using var negResp = await http.SendAsync(negReq);
var negJson = await negResp.Content.ReadAsStringAsync();
Console.WriteLine($"-> {(int)negResp.StatusCode} {negResp.StatusCode}");
Console.WriteLine(negJson);
Console.WriteLine(negResp.StatusCode == HttpStatusCode.BadRequest
    ? "negative path OK: 400 as expected"
    : $"UNEXPECTED: expected 400, got {(int)negResp.StatusCode}");

return 0;

string BuildSubmitBody(string url) => JsonSerializer.Serialize(new Dictionary<string, object?>
{
    ["model"] = Model,
    ["input"] = new Dictionary<string, object?>
    {
        ["prompt"] = Prompt,
        ["media"] = new List<object>
        {
            new Dictionary<string, object?> { ["type"] = "first_clip", ["url"] = url }
        }
    },
    ["parameters"] = new Dictionary<string, object?>
    {
        ["resolution"] = "480P",
        ["ratio"] = "adaptive",
        ["duration"] = 5
    }
});

static string? ExtractTaskId(string json)
{
    using var doc = JsonDocument.Parse(json);
    var root = doc.RootElement;
    if (root.TryGetProperty("output", out var output) &&
        output.TryGetProperty("task_id", out var taskId))
    {
        return taskId.GetString();
    }

    return root.TryGetProperty("task_id", out var top) ? top.GetString() : null;
}

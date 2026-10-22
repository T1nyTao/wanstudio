using WanStudio.Services;

var endpoint = args.Length > 0 ? args[0] : "http://127.0.0.1:8787";
var key = args.Length > 1 ? args[1] : "demo-key";

Console.WriteLine($"endpoint={endpoint} key={(key.Length > 8 ? key[..4] + "****" : "****")}");

try
{
    using var client = new DashScopeClient(key, endpoint);
    var taskId = await client.SubmitContinueAsync(
        "http://127.0.0.1:8787/files/sample.mp4",
        "延续上一段的主体与镜头运动，动作自然衔接。",
        "wan2.7-i2v",
        lastFrameUrl: null,
        duration: 5);
    Console.WriteLine($"task_id={taskId}");

    for (var i = 0; i < 5; i++)
    {
        var st = await client.GetTaskAsync(taskId);
        Console.WriteLine($"poll[{i}] status={st.Status} url={st.VideoUrl}");
        if (st.IsTerminal) break;
        await Task.Delay(300);
    }
}
catch (Exception ex)
{
    Console.WriteLine($"ERROR {ex.GetType().Name}: {ex.Message}");
}

Console.WriteLine("--- local path guard ---");
try
{
    using var c2 = new DashScopeClient(key, endpoint);
    await c2.SubmitContinueAsync(@"C:\tmp\a.mp4", "x", "wan2.7-i2v");
    Console.WriteLine("UNEXPECTED: local path accepted");
}
catch (Exception ex)
{
    Console.WriteLine("guard OK: " + ex.Message[..Math.Min(120, ex.Message.Length)]);
}

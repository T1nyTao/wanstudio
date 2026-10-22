// 清掉 WanStudio 存在 Windows 凭据库里的 API Key。
//
// 为什么要单独一个工具：PasswordVault 对「未打包」应用的可见范围跟 exe 所在目录树有关，
// 所以必须放在和主程序同一个目录树里运行，才能看到主程序写进去的那条凭据。
using Windows.Security.Credentials;

const string ResourceName = "WanStudio.DashScope";

var vault = new PasswordVault();

Console.WriteLine("--- 清理前 ---");
var existing = new List<PasswordCredential>();
try
{
    foreach (var c in vault.FindAllByResource(ResourceName))
    {
        existing.Add(c);
        Console.WriteLine($"  profile='{c.UserName}' resource='{c.Resource}'");
    }
    if (existing.Count == 0) Console.WriteLine("  (没有找到已保存的 Key)");
}
catch (Exception ex)
{
    Console.WriteLine($"  枚举失败：{ex.GetType().Name} {ex.Message}");
}

var removed = 0;
foreach (var c in existing)
{
    try
    {
        c.RetrievePassword();   // Remove 前必须先取回，否则可能抛
        vault.Remove(c);
        removed++;
        Console.WriteLine($"  已删除 profile='{c.UserName}'");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  删除 profile='{c.UserName}' 失败：{ex.Message}");
    }
}

Console.WriteLine("--- 清理后 ---");
try
{
    var left = vault.FindAllByResource(ResourceName);
    Console.WriteLine(left.Count == 0 ? "  已清空，没有残留。" : $"  仍有 {left.Count} 条残留！");
}
catch (Exception)
{
    Console.WriteLine("  已清空，没有残留。");
}

Console.WriteLine($"结果：删除 {removed} 条。");

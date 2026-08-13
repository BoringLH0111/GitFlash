using System.Diagnostics;
using System.Text;

namespace GitFlash;

/// <summary>
/// Git 命令封装：程序所有与 git 的交互都通过这里。
/// 原理很简单：在指定目录下执行 git 命令，然后读取输出。
/// </summary>
public static class GitHelper
{
    /// <summary>同步执行 git 命令，返回标准输出（已去除尾部空白）。失败时抛出异常。</summary>
    public static string Run(string workingDirectory, params string[] args)
    {
        using var p = Start(workingDirectory, args);
        string stdout = p.StandardOutput.ReadToEnd();
        string stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new Exception(stderr.Trim());
        // 注意只能用 TrimEnd：git status --porcelain 等输出开头的空格是状态标记的一部分
        return stdout.TrimEnd();
    }

    /// <summary>异步执行 git 命令（用于克隆等耗时操作），避免卡住界面。</summary>
    public static async Task<string> RunAsync(string workingDirectory, params string[] args)
    {
        using var p = Start(workingDirectory, args);
        string stdout = await p.StandardOutput.ReadToEndAsync();
        string stderr = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        if (p.ExitCode != 0)
            throw new Exception(stderr.Trim());
        // 与 Run 一样只用 TrimEnd，保留前导空格（porcelain 状态标记）
        return stdout.TrimEnd();
    }

    private static Process Start(string workingDirectory, string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // 用 ArgumentList 逐个传参，自动处理空格和引号，无需手动转义
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        return Process.Start(psi)!;
    }
}

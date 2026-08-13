namespace GitFlash;

/// <summary>
/// 一条提交记录，来自 git log 的解析结果。
/// </summary>
public class CommitInfo
{
    /// <summary>短哈希（7 位，界面显示用）</summary>
    public string ShortHash { get; set; } = "";

    /// <summary>完整哈希（可用于 git show 等命令）</summary>
    public string FullHash { get; set; } = "";

    /// <summary>作者</summary>
    public string Author { get; set; } = "";

    /// <summary>提交时间（yyyy-MM-dd HH:mm）</summary>
    public string Time { get; set; } = "";

    /// <summary>提交说明</summary>
    public string Message { get; set; } = "";

    /// <summary>
    /// 解析 git log 的输出。字段之间用 NUL（%x00）分隔，
    /// 这样即使提交说明里含有 | 等字符也不会解析错。
    /// </summary>
    public static List<CommitInfo> Parse(string log)
    {
        var list = new List<CommitInfo>();
        string[] parts = log.Split('\0');
        // 每 5 个字段为一组：短哈希、作者、时间、说明、完整哈希
        for (int i = 0; i + 4 < parts.Length; i += 5)
        {
            list.Add(new CommitInfo
            {
                ShortHash = parts[i],
                Author = parts[i + 1],
                Time = parts[i + 2],
                Message = parts[i + 3].Trim(),
                // 每条记录以换行结尾，完整哈希字段末尾可能带 \n
                FullHash = parts[i + 4].TrimEnd('\r', '\n'),
            });
        }
        return list;
    }
}

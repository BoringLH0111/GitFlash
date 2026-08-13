namespace GitFlash;

/// <summary>
/// 一个文件变更，来自 git status --porcelain 的解析结果。
/// </summary>
public class ChangeFile
{
    /// <summary>文件路径（相对仓库根目录）</summary>
    public string Path { get; set; } = "";

    /// <summary>暂存区状态字符（X），空格表示不在暂存区</summary>
    public char StagedStatus { get; set; } = ' ';

    /// <summary>工作区状态字符（Y），空格表示工作区无改动</summary>
    public char WorktreeStatus { get; set; } = ' ';

    /// <summary>是否已暂存</summary>
    public bool IsStaged => StagedStatus != ' ' && StagedStatus != '?';

    /// <summary>是否为未跟踪的新文件</summary>
    public bool IsUntracked => StagedStatus == '?' && WorktreeStatus == '?';

    /// <summary>
    /// 解析 git status --porcelain 的输出为文件变更列表。
    /// 每个文件的状态形如 "XY 路径"，X 表示暂存区，Y 表示工作区。
    /// </summary>
    public static List<ChangeFile> Parse(string porcelain)
    {
        var list = new List<ChangeFile>();
        foreach (var raw in porcelain.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length < 3)
                continue;

            char x = line[0];          // 暂存区状态
            char y = line[1];          // 工作区状态
            string path = line[3..];

            // 重命名/复制格式：old -> new，我们只关心 new
            int arrow = path.IndexOf(" -> ", StringComparison.Ordinal);
            if (arrow >= 0)
                path = path[(arrow + 4)..];

            // 同一文件可能同时出现在暂存区和工作区（如 MM），需要合并
            var exist = list.FirstOrDefault(f => f.Path == path);
            if (exist != null)
            {
                if (x != ' ' && x != '?') exist.StagedStatus = x;
                if (y != ' ' && y != '?') exist.WorktreeStatus = y;
            }
            else
            {
                list.Add(new ChangeFile { Path = path, StagedStatus = x, WorktreeStatus = y });
            }
        }
        return list;
    }
}

using PhiEdit.Core.Skins;

namespace PhiEdit.App.Services;

/// <summary>
/// 内置资源包的装载。资源包随程序一起分发，位于
/// <c>Assets/NoteSkins</c> 目录下的 ZIP 文件。
/// </summary>
public static class BuiltinSkins
{
    public const string BuiltinDirectory = "builtin-gaofan";

    /// <summary>装载全部内置资源包，返回载入失败的提示。</summary>
    public static IReadOnlyList<string> Load(PhiEdit.Core.Editing.EditorSession session)
    {
        var failures = new List<string>();
        var directory = Path.Combine(AppContext.BaseDirectory, "Assets", "NoteSkins");
        if (!Directory.Exists(directory))
        {
            return failures;
        }

        foreach (var file in Directory.GetFiles(directory, "*.zip", SearchOption.TopDirectoryOnly))
        {
            var id = Path.GetFileNameWithoutExtension(file);
            try
            {
                var skin = NoteSkin.FromZip(File.ReadAllBytes(file), $"builtin-{id}", builtin: true);
                session.Skins.Add(skin);
            }
            catch (Exception exception)
            {
                failures.Add($"内置资源包 {Path.GetFileName(file)} 载入失败：{exception.Message}");
            }
        }

        return failures;
    }
}

using Colibri.Core.Media;
namespace Colibri.Platform.Media;
internal static class MediaPathSafety
{
    public static void RequireNoLinks(string path)
    {
        string? current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current)) {
            try {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new MediaHelperException("Media output and staging paths must not contain symbolic links or reparse points.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            var parent = Path.GetDirectoryName(current);
            if (parent == current) break;
            current = parent;
        }
    }
}

using HiveDB.Storage;

namespace HiveDB.Tree;

internal sealed class TreeNavigator
{
    private readonly PageManager _pages;

    public TreeNavigator(PageManager pages)
    {
        _pages = pages;
    }

    /// <summary>
    /// Resolves a path like @"Software\MyApp\Settings" to a KeyPage.
    /// If <paramref name="createMissing"/> is true, non-existent intermediate keys are created.
    /// Returns null if any segment is missing and createMissing is false.
    /// </summary>
    public KeyPage? ResolveKey(string path, bool createMissing)
    {
        var segments = NormalizePath(path);
        var header = _pages.ReadHeader();
        int currentPage = header.RootKeyPage;

        foreach (var segment in segments)
        {
            var currentKey = KeyPage.Read(_pages.ReadPage(currentPage), currentPage);

            int childPage = currentKey.FirstChildPage;
            int? foundPage = null;

            while (childPage != 0)
            {
                var childKey = KeyPage.Read(_pages.ReadPage(childPage), childPage);
                if (!childKey.IsDeleted && childKey.KeyName == segment)
                {
                    foundPage = childPage;
                    break;
                }
                childPage = childKey.NextSiblingPage;
            }

            if (foundPage.HasValue)
            {
                currentPage = foundPage.Value;
                continue;
            }

            if (!createMissing)
                return null;

            // Create the missing key
            int newPage = _pages.AllocatePage(PageType.Key);
            var newKey = new KeyPage
            {
                PageNumber = newPage,
                KeyName = segment,
                ParentPage = currentPage,
                FirstChildPage = 0,
                NextSiblingPage = 0,
            };

            // Link into sibling chain
            if (currentKey.FirstChildPage == 0)
            {
                currentKey.FirstChildPage = newPage;
            }
            else
            {
                // Append to end of sibling list
                int sibling = currentKey.FirstChildPage;
                while (true)
                {
                    var siblingKey = KeyPage.Read(_pages.ReadPage(sibling), sibling);
                    if (siblingKey.NextSiblingPage == 0)
                    {
                        siblingKey.NextSiblingPage = newPage;
                        _pages.WritePage(sibling, SerializeKeyPage(siblingKey));
                        break;
                    }
                    sibling = siblingKey.NextSiblingPage;
                }
            }

            var buffer = SerializeKeyPage(currentKey);
            _pages.WritePage(currentPage, buffer);

            buffer = SerializeKeyPage(newKey);
            _pages.WritePage(newPage, buffer);

            currentPage = newPage;
        }

        return KeyPage.Read(_pages.ReadPage(currentPage), currentPage);
    }

    /// <summary>
    /// Enumerates all direct child keys of the given parent page.
    /// </summary>
    public IEnumerable<KeyPage> GetChildren(int parentPageNumber)
    {
        var parent = KeyPage.Read(_pages.ReadPage(parentPageNumber), parentPageNumber);
        int childPage = parent.FirstChildPage;

        while (childPage != 0)
        {
            var child = KeyPage.Read(_pages.ReadPage(childPage), childPage);
            if (!child.IsDeleted)
                yield return child;
            childPage = child.NextSiblingPage;
        }
    }

    /// <summary>
    /// Walks the full subtree depth-first, calling the action for each key page.
    /// </summary>
    public void WalkSubtree(int pageNumber, Action<KeyPage> action)
    {
        var key = KeyPage.Read(_pages.ReadPage(pageNumber), pageNumber);
        action(key);
        int childPage = key.FirstChildPage;
        while (childPage != 0)
        {
            WalkSubtree(childPage, action);
            var child = KeyPage.Read(_pages.ReadPage(childPage), childPage);
            childPage = child.NextSiblingPage;
        }
    }

    /// <summary>
    /// Given a parent key and a child name, finds the previous sibling (or returns null if it's the first child).
    /// Returns (previousSibling, target). If previousSibling is null, target is the first child.
    /// </summary>
    public (int? previousSibling, int target) FindChildWithPrev(KeyPage parent, string name)
    {
        int child = parent.FirstChildPage;
        int? prev = null;

        while (child != 0)
        {
            var childKey = KeyPage.Read(_pages.ReadPage(child), child);
            if (!childKey.IsDeleted && childKey.KeyName == name)
                return (prev, child);

            prev = child;
            child = childKey.NextSiblingPage;
        }

        throw new KeyNotFoundException($"Child key '{name}' not found.");
    }

    public void UnlinkChild(KeyPage parent, string name)
    {
        var (prev, target) = FindChildWithPrev(parent, name);
        var targetKey = KeyPage.Read(_pages.ReadPage(target), target);

        if (prev.HasValue)
        {
            var prevKey = KeyPage.Read(_pages.ReadPage(prev.Value), prev.Value);
            prevKey.NextSiblingPage = targetKey.NextSiblingPage;
            _pages.WritePage(prev.Value, SerializeKeyPage(prevKey));
        }
        else
        {
            parent.FirstChildPage = targetKey.NextSiblingPage;
            _pages.WritePage(parent.PageNumber, SerializeKeyPage(parent));
        }
    }

    private static string[] NormalizePath(string path)
    {
        return path
            .Replace('/', '\\')
            .Split('\\', StringSplitOptions.RemoveEmptyEntries);
    }

    private static byte[] SerializeKeyPage(KeyPage page)
    {
        var buffer = new byte[FileHeader.PageSize];
        page.Write(buffer);
        return buffer;
    }
}

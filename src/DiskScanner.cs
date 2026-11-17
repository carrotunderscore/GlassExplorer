using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Enumeration;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;

namespace GlassExplorer
{
    /// One folder in a disk scan. Only folders are stored (files are summed per folder) to keep memory low.
    public sealed class SizeNode
    {
        public string Name;
        public SizeNode Parent;
        public List<SizeNode> Children;
        public long Size;        // total, including subfolders
        public long FilesSize;   // files directly in this folder
        public int FileCount;
        public long FileCountTotal;
        public long Unknown;     // root only: used space we couldn't read
        public bool IsFiles, IsUnknown;
        SizeNode filesNode, unknownNode;

        public string FullPath => IsFiles || IsUnknown ? Parent.FullPath : Parent == null ? Name : Path.Combine(Parent.FullPath, Name);
        public bool IsPseudo => IsFiles || IsUnknown;
        public string DisplayName => IsFiles ? $"Files in this folder ({FileCount:N0})" : IsUnknown ? "System / unreadable" : Name;

        /// Children to draw, biggest first: subfolders + a "files here" block + (root) unknown block.
        public List<SizeNode> Display()
        {
            var l = new List<SizeNode>();
            if (Children != null) foreach (var c in Children) if (c.Size > 0) l.Add(c);
            if (FilesSize > 0) l.Add(filesNode ??= new SizeNode { Name = "Files", IsFiles = true, Size = FilesSize, FileCount = FileCount, Parent = this });
            if (Unknown > 0) l.Add(unknownNode ??= new SizeNode { Name = "Unknown", IsUnknown = true, Size = Unknown, Parent = this });
            l.Sort((a, b) => b.Size.CompareTo(a.Size));
            return l;
        }

        public bool HasChildren => Children != null && Children.Count > 0;
    }

    public static class DiskScanner
    {
        public static long FilesScanned, BytesScanned, FoldersScanned;

        public static SizeNode Scan(string root, CancellationToken ct)
        {
            FilesScanned = BytesScanned = FoldersScanned = 0;
            ThreadPool.GetMinThreads(out int w, out int io);
            ThreadPool.SetMinThreads(Math.Max(w, Environment.ProcessorCount * 4), io);
            var node = new SizeNode { Name = root };
            ScanDir(node, root, 0, ct);
            return node;
        }

        static readonly EnumerationOptions Opts = new()
        {
            IgnoreInaccessible = true, RecurseSubdirectories = false, ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint // don't follow junctions/symlinks (no double counting)
        };

        static void ScanDir(SizeNode n, string path, int depth, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            long files = 0; int count = 0;
            try
            {
                var en = new FileSystemEnumerable<(string name, bool dir, long len)>(path,
                    (ref FileSystemEntry e) => (e.FileName.ToString(), e.IsDirectory, e.Length), Opts);
                foreach (var (name, dir, len) in en)
                {
                    if (dir) (n.Children ??= new List<SizeNode>()).Add(new SizeNode { Name = name, Parent = n });
                    else { files += len; count++; }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* access denied etc. */ }

            n.FilesSize = files; n.FileCount = count;
            Interlocked.Add(ref BytesScanned, files);
            Interlocked.Add(ref FilesScanned, count);
            Interlocked.Increment(ref FoldersScanned);

            if (n.Children != null)
            {
                if (n.Children.Count > 1)
                    Parallel.ForEach(n.Children, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Environment.ProcessorCount * 2 },
                        c => ScanDir(c, Path.Combine(path, c.Name), depth + 1, ct));
                else
                    foreach (var c in n.Children) ScanDir(c, Path.Combine(path, c.Name), depth + 1, ct);
                n.Children.Sort((a, b) => b.Size.CompareTo(a.Size));
                n.Children.TrimExcess();
            }

            long total = files, totalCount = count;
            if (n.Children != null) foreach (var c in n.Children) { total += c.Size; totalCount += c.FileCountTotal; }
            n.Size = total;
            n.FileCountTotal = totalCount;
        }

        // ---------- shared colors (treemap + list) ----------
        static readonly Color[] Palette =
        {
            Rgb(0x4C, 0x9B, 0xFF), Rgb(0x5B, 0xD6, 0x8A), Rgb(0xFF, 0xA5, 0x52), Rgb(0xB4, 0x8C, 0xFF), Rgb(0xFF, 0x7E, 0xB6),
            Rgb(0x4F, 0xD1, 0xC5), Rgb(0xF5, 0xC4, 0x51), Rgb(0xFF, 0x6B, 0x6B), Rgb(0x8F, 0xB8, 0xFF), Rgb(0xA3, 0xE6, 0x35)
        };
        static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

        public static Color ColorFor(SizeNode n, int index) =>
            n.IsUnknown ? Rgb(0x55, 0x60, 0x75) : n.IsFiles ? Rgb(0x7D, 0x8C, 0xA8) : Palette[index % Palette.Length];
    }
}

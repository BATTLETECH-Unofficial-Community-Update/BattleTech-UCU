// Packer-only: finds runs of a shipped file that are byte-identical to the user's retail install and describes the
// file as an ordered list of pieces (copy a retail range, or take residual bytes from the package's blob). The
// installer rebuilds the file from those pieces (Installer.cs, Gate.Open). The match is format-blind: any identical
// run counts, not only textures.
//
// Index: every retail file of 64 KiB or more under BattleTech_Data/ (not Managed/: mod loaders patch those DLLs, and a
// user's retail folder must never be required to match them) contributes the 64-bit polynomial rolling hash of each
// aligned 4 KiB block to one table, first occurrence kept. Blocks of a single repeated byte are skipped and a hash seen
// 8 or more times is dropped: such blocks anchor nothing useful and make every byte position of low-entropy data a hit.
// Scan: the same hash rolls over every byte position of a file that would otherwise be sealed; a hit is verified
// against the retail bytes (never trusted as a hash match), then the match is extended forward and backward in windows
// that grow from 4 KiB to 64 KiB, and kept at 16 KiB or more. A verified hit too short to keep is skipped past.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;

namespace ReleaseGate
{
    /// <summary>A retail install indexed by the rolling hash of each aligned 4 KiB block.</summary>
    public sealed class RetailIndex
    {
        public const int Block = 4096;
        const int MinIndexedFile = 64 * 1024;
        const long MinMatch = 16 * 1024;
        const int Chunk = 4 << 20;
        const int Compare = 64 * 1024;
        const int FirstWindow = 4 * 1024;
        const int MaxCopies = 8;        // a hash seen this often in retail is not an anchor
        const int CountShift = 56;      // index value: count << 56 | file << 32 | offset
        const long LocMask = (1L << CountShift) - 1;
        const ulong Base = 0x100000001B3UL;
        static readonly ulong Pow;

        static RetailIndex()
        {
            Pow = 1;
            for (int i = 0; i < Block - 1; i++) unchecked { Pow *= Base; }
        }

        readonly string[] _files;   // absolute paths
        readonly string[] _rels;    // install-relative, '/'
        readonly long[] _lengths;
        readonly Dictionary<ulong, long> _blocks;

        RetailIndex(string[] files, string[] rels, long[] lengths, Dictionary<ulong, long> blocks)
        {
            _files = files; _rels = rels; _lengths = lengths; _blocks = blocks;
        }

        /// <summary>Retail data a mod can't have changed: BattleTech_Data/, less Managed/.</summary>
#if UCU_MAC
        static bool Indexed(string rel, Layout lay)
        {
            return rel.StartsWith(lay.Data, StringComparison.OrdinalIgnoreCase) && !rel.StartsWith(lay.Managed, StringComparison.OrdinalIgnoreCase);
        }
#else
        static bool Indexed(string rel)
        {
            return rel.StartsWith("BattleTech_Data/", StringComparison.OrdinalIgnoreCase)
                && !rel.StartsWith("BattleTech_Data/Managed/", StringComparison.OrdinalIgnoreCase);
        }
#endif

        static bool AllSame(byte[] b, int off)
        {
            byte v = b[off];
            for (int i = 1; i < Block; i++)
                if (b[off + i] != v) return false;
            return true;
        }

        /// <summary>Indexes every file of 64 KiB or more under root's BattleTech_Data/ (not Managed/).</summary>
        public static RetailIndex Build(string root)
        {
            root = Path.GetFullPath(root).TrimEnd('\\', '/');
#if UCU_MAC
            Layout lay = Layout.Of(root);
#endif
            var found = new List<string>();
            foreach (string f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                string rel = f.Substring(root.Length + 1).Replace('\\', '/');
#if UCU_MAC
                if (!Indexed(rel, lay)) continue;
#else
                if (!Indexed(rel)) continue;
#endif
                long n = new FileInfo(f).Length;
                if (n < MinIndexedFile || n > uint.MaxValue) continue;
                found.Add(rel);
            }
            found.Sort(StringComparer.Ordinal);
            var files = new string[found.Count];
            var lengths = new long[found.Count];
            long total = 0;
            for (int i = 0; i < found.Count; i++)
            {
                files[i] = Path.Combine(root, found[i].Replace('/', '\\'));
                lengths[i] = new FileInfo(files[i]).Length;
                total += lengths[i];
            }
            if (files.Length >= 1 << 24) throw new InvalidOperationException("Too many retail files to index: " + files.Length);
            var blocks = new Dictionary<ulong, long>((int)Math.Min(int.MaxValue - 8, total / Block));
            var buf = new byte[Chunk];
            for (int i = 0; i < files.Length; i++)
            {
                using (var s = new FileStream(files[i], FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan))
                {
                    long at = 0;
                    while (at < lengths[i])
                    {
                        int want = (int)Math.Min(buf.Length, lengths[i] - at);
                        int got = ReadFully(s, buf, 0, want);
                        int whole = got - got % Block;
                        for (int p = 0; p < whole; p += Block)
                        {
                            if (AllSame(buf, p)) continue;
                            ulong h = HashBlock(buf, p);
                            long v;
                            if (!blocks.TryGetValue(h, out v)) blocks[h] = (1L << CountShift) | ((long)i << 32) | (uint)(at + p);
                            else if ((v >> CountShift) < MaxCopies) blocks[h] = v + (1L << CountShift);   // keeps the first occurrence
                        }
                        at += whole;
                        if (got < want || whole < got) break;   // only the last read can hold a partial block
                    }
                }
            }
            return new RetailIndex(files, found.ToArray(), lengths, blocks);
        }

        static int ReadFully(Stream s, byte[] b, int off, int count)
        {
            int got = 0, n;
            while (got < count && (n = s.Read(b, off + got, count - got)) > 0) got += n;
            return got;
        }

        static ulong HashBlock(byte[] b, int off)
        {
            ulong h = 0;
            for (int i = 0; i < Block; i++) unchecked { h = h * Base + b[off + i]; }
            return h;
        }

        /// <summary>One kept run of the scanned file, copied from retail.</summary>
        sealed class Match
        {
            public long Start, Length, RetailOffset;
            public string RetailPath;
        }

        /// <summary>The pieces of a scanned file and how many bytes they copy from retail.</summary>
        public sealed class Scan
        {
            public List<RangePiece> Pieces;
            public long Matched;
        }

        /// <summary>Scans one file; the result always covers the whole file.</summary>
        public Scan ScanFile(string path, CancellationToken stop)
        {
            long len = new FileInfo(path).Length;
            var matches = new List<Match>();
            using (var reader = new Reader(this))
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.None))
            {
                var buf = new byte[Chunk];
                int valid = 0;
                long baseOff = 0, pos = 0, lastEnd = 0;
                ulong h = 0;
                bool have = false;
                var ra = new byte[Block];
                var a = new byte[Compare];
                var b = new byte[Compare];
                long ticks = 0;
                while (pos + Block <= len)
                {
                    if ((++ticks & 0xFFFFF) == 0) stop.ThrowIfCancellationRequested();
                    if (!have)
                    {
                        baseOff = pos;
                        valid = ReadAt(fs, pos, buf, (int)Math.Min(buf.Length, len - pos));
                        if (valid < Block) break;
                        h = HashBlock(buf, 0);
                        have = true;
                    }
                    int rel = (int)(pos - baseOff);
                    if (rel + Block > valid) { have = false; continue; }
                    long loc;
                    if (_blocks.TryGetValue(h, out loc) && (loc >> CountShift) < MaxCopies)
                    {
                        loc &= LocMask;
                        int fi = (int)(loc >> 32);
                        long off = (long)(uint)loc;
                        if (BlockMatches(reader, fi, off, buf, rel, ra))
                        {
                            long back = ExtendBack(fs, pos, reader, fi, off, Math.Min(pos - lastEnd, off), a, b);
                            long maxForward = Math.Min(len - (pos + Block), _lengths[fi] - (off + Block));
                            long forward = maxForward <= 0 ? 0 : ExtendForward(fs, pos + Block, reader, fi, off + Block, maxForward, a, b);
                            long start = pos - back, length = Block + back + forward;
                            if (length >= MinMatch)
                            {
                                matches.Add(new Match { Start = start, Length = length, RetailOffset = off - back, RetailPath = _rels[fi] });
                                lastEnd = start + length;
                                pos = lastEnd;
                                have = false;
                                continue;
                            }
                            // Verified but too short to keep: nothing in the region it covers is worth anchoring again.
                            pos += Block + forward;
                            have = false;
                            continue;
                        }
                    }
                    if (rel + Block >= valid) { pos++; have = false; continue; }
                    unchecked { h = (h - buf[rel] * Pow) * Base + buf[rel + Block]; }
                    pos++;
                }
            }
            return Pieces(len, matches);
        }

        Scan Pieces(long len, List<Match> matches)
        {
            matches.Sort((x, y) => x.Start.CompareTo(y.Start));
            var pieces = new List<RangePiece>();
            long at = 0, matched = 0;
            foreach (Match m in matches)
            {
                if (m.Start > at) pieces.Add(new RangePiece { Length = m.Start - at });
                pieces.Add(new RangePiece { Offset = m.RetailOffset, Length = m.Length, Path = m.RetailPath });
                at = m.Start + m.Length;
                matched += m.Length;
            }
            if (at < len) pieces.Add(new RangePiece { Length = len - at });
            return new Scan { Pieces = pieces, Matched = matched };
        }

        bool BlockMatches(Reader reader, int file, long offset, byte[] buf, int at, byte[] scratch)
        {
            if (offset + Block > _lengths[file]) return false;
            if (ReadAt(reader.Stream(file), offset, scratch, Block) < Block) return false;
            for (int i = 0; i < Block; i++)
                if (buf[at + i] != scratch[i]) return false;
            return true;
        }

        static long ExtendForward(FileStream player, long playerAt, Reader reader, int file, long retailAt, long max, byte[] a, byte[] b)
        {
            long done = 0;
            int window = FirstWindow;
            FileStream r = reader.Stream(file);
            while (done < max)
            {
                int n = (int)Math.Min(window, max - done);
                window = Math.Min(window * 2, Compare);
                int na = ReadAt(player, playerAt + done, a, n);
                int nb = ReadAt(r, retailAt + done, b, n);
                int m = Math.Min(na, nb);
                for (int i = 0; i < m; i++)
                    if (a[i] != b[i]) return done + i;
                if (m < n) return done + m;
                done += m;
            }
            return done;
        }

        static long ExtendBack(FileStream player, long playerAt, Reader reader, int file, long retailAt, long max, byte[] a, byte[] b)
        {
            long done = 0;
            int window = FirstWindow;
            FileStream r = reader.Stream(file);
            while (done < max)
            {
                int n = (int)Math.Min(window, max - done);
                window = Math.Min(window * 2, Compare);
                int na = ReadAt(player, playerAt - done - n, a, n);
                int nb = ReadAt(r, retailAt - done - n, b, n);
                int m = Math.Min(na, nb);
                for (int i = m - 1; i >= 0; i--)
                    if (a[i] != b[i]) return done + (m - 1 - i);
                if (m < n) return done + m;
                done += m;
            }
            return done;
        }

        static int ReadAt(FileStream f, long offset, byte[] buf, int count)
        {
            if (offset < 0 || count <= 0) return 0;
            f.Position = offset;
            return ReadFully(f, buf, 0, count);
        }

        /// <summary>One handle per retail file, opened on demand; cleared when a scan touches too many files.</summary>
        sealed class Reader : IDisposable
        {
            readonly RetailIndex _index;
            readonly Dictionary<int, FileStream> _open = new Dictionary<int, FileStream>();

            public Reader(RetailIndex index) { _index = index; }

            public FileStream Stream(int file)
            {
                FileStream s;
                if (_open.TryGetValue(file, out s)) return s;
                if (_open.Count >= 32) { foreach (var x in _open.Values) x.Dispose(); _open.Clear(); }
                s = new FileStream(_index._files[file], FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.RandomAccess);
                _open[file] = s;
                return s;
            }

            public void Dispose()
            {
                foreach (var x in _open.Values) x.Dispose();
                _open.Clear();
            }
        }
    }

    /// <summary>The residual bytes of a ranged file: the parts no retail range covers, concatenated in order.</summary>
    public sealed class ResidualStream : Stream
    {
        readonly FileStream _src;
        readonly List<RangePiece> _gaps = new List<RangePiece>();
        readonly long _length;
        int _at = -1;
        long _left;

        public ResidualStream(string path, IEnumerable<RangePiece> pieces)
        {
            _src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
            long total = 0, at = 0;
            foreach (RangePiece p in pieces)
            {
                if (!p.FromRetail)
                {
                    // Offset in the file, not in the residual: the stream reads the gaps where they sit.
                    _gaps.Add(new RangePiece { Offset = at, Length = p.Length });
                    total += p.Length;
                }
                at += p.Length;
            }
            _length = total;
        }

        public override bool CanRead { get { return true; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return false; } }
        public override long Length { get { return _length; } }
        public override long Position { get { return 0; } set { throw new NotSupportedException(); } }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            while (count > 0 && _left == 0)
            {
                _at++;
                if (_at >= _gaps.Count) return 0;
                RangePiece p = _gaps[_at];
                _left = p.Length;
                _src.Position = p.Offset;
            }
            if (count > _left) count = (int)_left;
            int n = _src.Read(buffer, offset, count);
            _left -= n;
            return n;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _src.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>Hashing and sealing helpers for a residual stream.</summary>
    public static class RetailRanges
    {
        /// <summary>The SHA-256 of a ranged file's residual bytes.</summary>
        public static string ResidualSha256(string path, List<RangePiece> pieces)
        {
            using (var h = Gate.NewSha())
            using (var r = new ResidualStream(path, pieces))
                return Gate.Hex(h.ComputeHash(r));
        }

        /// <summary>Total bytes the pieces copy from retail.</summary>
        public static long RetailBytes(IEnumerable<RangePiece> pieces)
        {
            long n = 0;
            foreach (RangePiece p in pieces) if (p.FromRetail) n += p.Length;
            return n;
        }
    }
}

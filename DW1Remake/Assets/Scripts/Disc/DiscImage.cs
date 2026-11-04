using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DW1Remake.Disc
{
    /// <summary>One file or folder found on the disc.</summary>
    public sealed class DiscFile
    {
        public string Path;        // e.g. "/CHDAT/ALLTIM.TIM"
        public int Lba;            // first sector
        public int Size;           // size in bytes
        public bool IsDirectory;
    }

    /// <summary>
    /// Reads a PlayStation disc image (.bin, 2352 bytes per sector, or a plain .iso)
    /// from ANY stream. Works in the Editor, in a Windows build, and in a WebGL build
    /// (where the stream comes from the file the player selects).
    /// Nothing from the disc is ever saved by this class.
    /// </summary>
    public sealed class DiscImage
    {
        const int UserBytes = 2048;

        readonly Stream _stream;
        readonly int _stride;       // bytes between sector starts in the file
        readonly int _dataOffset;   // where the 2048 user bytes start inside a sector
        readonly List<DiscFile> _files = new List<DiscFile>();
        readonly Dictionary<string, DiscFile> _byPath =
            new Dictionary<string, DiscFile>(StringComparer.OrdinalIgnoreCase);

        public string VolumeId { get; private set; }
        public IReadOnlyList<DiscFile> Files { get { return _files; } }

        public DiscImage(Stream stream)
        {
            _stream = stream;

            // Detect layout: raw CD sectors begin with 00 FF x10 00
            byte[] head = new byte[16];
            _stream.Seek(0, SeekOrigin.Begin);
            ReadExact(head, 16);
            bool raw = head[0] == 0 && head[11] == 0;
            for (int i = 1; i <= 10 && raw; i++) raw = head[i] == 0xFF;

            if (raw)
            {
                _stride = 2352;
                _dataOffset = head[15] == 1 ? 16 : 24; // mode 1 vs mode 2 (PS1 = mode 2)
            }
            else
            {
                _stride = 2048;
                _dataOffset = 0;
            }

            byte[] pvd = new byte[UserBytes];
            ReadSector(16, pvd);
            if (pvd[0] != 1 || Encoding.ASCII.GetString(pvd, 1, 5) != "CD001")
                throw new InvalidDataException("This does not look like a PlayStation disc image.");

            VolumeId = Encoding.ASCII.GetString(pvd, 40, 32).Trim();
            int rootLba = BitConverter.ToInt32(pvd, 156 + 2);
            int rootSize = BitConverter.ToInt32(pvd, 156 + 10);
            ReadDirectory(rootLba, rootSize, "");
        }

        void ReadExact(byte[] buf, int count)
        {
            int got = 0;
            while (got < count)
            {
                int n = _stream.Read(buf, got, count - got);
                if (n <= 0) throw new EndOfStreamException("Disc image is too short.");
                got += n;
            }
        }

        /// <summary>Reads the 2048 user bytes of one sector.</summary>
        public void ReadSector(int lba, byte[] buffer)
        {
            _stream.Seek((long)lba * _stride + _dataOffset, SeekOrigin.Begin);
            ReadExact(buffer, UserBytes);
        }

        void ReadDirectory(int lba, int size, string parentPath)
        {
            byte[] data = new byte[((size + UserBytes - 1) / UserBytes) * UserBytes];
            byte[] sector = new byte[UserBytes];
            for (int i = 0; i < data.Length / UserBytes; i++)
            {
                ReadSector(lba + i, sector);
                Buffer.BlockCopy(sector, 0, data, i * UserBytes, UserBytes);
            }

            int p = 0;
            while (p < size)
            {
                int len = data[p];
                if (len == 0) { p = (p / UserBytes + 1) * UserBytes; continue; } // padding to next sector

                int nameLen = data[p + 32];
                bool isDir = (data[p + 25] & 2) != 0;
                int entryLba = BitConverter.ToInt32(data, p + 2);
                int entrySize = BitConverter.ToInt32(data, p + 10);
                string name = Encoding.ASCII.GetString(data, p + 33, nameLen);
                p += len;

                if (nameLen == 1 && (name[0] == '\0' || name[0] == '\u0001')) continue; // "." and ".."

                int semi = name.IndexOf(';');
                if (semi >= 0) name = name.Substring(0, semi);

                string path = parentPath + "/" + name;
                var f = new DiscFile { Path = path, Lba = entryLba, Size = entrySize, IsDirectory = isDir };
                _files.Add(f);
                _byPath[path] = f;
                if (isDir) ReadDirectory(entryLba, entrySize, path);
            }
        }

        /// <summary>True for a raw image (.bin, 2352 bytes a sector): only these keep the sector sub-headers and the XA audio sectors movies need.</summary>
        public bool IsRaw { get { return _stride == 2352; } }

        /// <summary>The whole 2352-byte sector (sync, header, sub-header at 16, data from 24). Raw images only.</summary>
        public void ReadRawSector(int lba, byte[] buffer)
        {
            if (!IsRaw) throw new InvalidOperationException("Not a raw (.bin) disc image.");
            _stream.Seek((long)lba * 2352, SeekOrigin.Begin);
            ReadExact(buffer, 2352);
        }

        public bool Exists(string path) { return _byPath.ContainsKey(path); }

        public DiscFile Find(string path)
        {
            DiscFile f;
            return _byPath.TryGetValue(path, out f) ? f : null;
        }

        /// <summary>Reads a whole file from the disc into memory.</summary>
        public byte[] ReadFile(string path)
        {
            DiscFile f = Find(path);
            if (f == null || f.IsDirectory) throw new FileNotFoundException("Not on disc: " + path);

            byte[] result = new byte[f.Size];
            byte[] sector = new byte[UserBytes];
            int done = 0, lba = f.Lba;
            while (done < f.Size)
            {
                ReadSector(lba++, sector);
                int n = Math.Min(UserBytes, f.Size - done);
                Buffer.BlockCopy(sector, 0, result, done, n);
                done += n;
            }
            return result;
        }
    }
}

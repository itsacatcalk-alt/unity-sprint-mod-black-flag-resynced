// SprintPatch.exe - the engine behind the Unity Sprint installer (JARVIS, 5 Nov 2026; v1.0.4).
// Modes:  install <gameDir> | uninstall <gameDir> | rollback <gameDir> | check <gameDir>
// Writes machine-readable lines to stdout for the installer:
//   P|<permille 0-1000>|<seconds left, or -1>|<status text>
//   E|<message for the user>          (then exits with code 1)
//   OK|<message>                      (then exits with code 0)
// User-facing text is deliberately generic: no internal names, IDs or offsets.
// v1.0.4: the Oodle library and the sprint animation are ordinary files installed next to this program
// (no embedded encrypted resources, nothing written to Temp, no manual library loading). Both are
// checked against their SHA-256 before use. Built by build.ps1 (Payload.cs holds the
// file names and hashes and is generated there).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading.Tasks;

public static class SprintPatch
{
    const string FORGE = "DataPC_boot_patch_01.forge";
    const string BAK = ".bak";
    const string LEGACY = ".stock";            // backup name used by the earlier script patcher
    const string TMP = ".usm-tmp";
    const string GAME_EXE = "ACBlackFlag.exe";
    const ulong BUNDLE_ID = 0x0000020A01049EF6UL;
    const ulong TARGET_ID = 0x000002084F080CEAUL;
    const uint TARGET_TYPE = 0x0FA3067F;
    const uint TARGET_KEY = 0x4EFB1209;
    const int BLOCK = 262144;
    const int ALIGN = 32768;
    static readonly byte[] PACKET_HEADER = { 0x33, 0xaa, 0xfb, 0x57, 0x99, 0xfa, 0x04, 0x10, 0x03, 0x00, 0x08, 0x00, 0x00, 0x04, 0x80 };

    // Resolved from this program's own folder, where the installer puts the library.
    [DllImport("oo2core_9_win64.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern long OodleLZ_Compress(int compressor, byte[] raw, long rawLen, byte[] comp, int level,
        IntPtr opts, IntPtr dictBase, IntPtr lrm, IntPtr scratch, long scratchSize);
    [DllImport("oo2core_9_win64.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern long OodleLZ_Decompress(byte[] comp, long compLen, byte[] raw, long rawLen, int fuzz, int crc,
        int verbose, IntPtr decBase, long decBaseSize, IntPtr cb, IntPtr cbData, IntPtr scratch, long scratchSize, int threadPhase);

    class UserError : Exception { public UserError(string m) : base(m) { } }
    static void Need(bool ok, string userMessage) { if (!ok) throw new UserError(userMessage); }
    const string DAMAGED = "The game files could not be read as expected. Nothing was changed.";

    // ------------------------------------------------------------------ progress with ETA
    static Stopwatch clock = Stopwatch.StartNew();
    static double[] weights; static int phase; static double phaseStart; static int lastPermille = -1; static string lastText = "";
    static long lastEmit;
    static void Phases(params double[] w) { double s = 0; foreach (double x in w) s += x; weights = new double[w.Length]; for (int i = 0; i < w.Length; i++) weights[i] = w[i] / s; phase = 0; phaseStart = 0; }
    static void Next(string text) { if (phase < weights.Length) phaseStart += weights[phase]; phase++; Report(0, text, true); }
    static void Report(double withinPhase, string text, bool force)
    {
        double f = phaseStart + (phase < weights.Length ? weights[phase] * Math.Max(0, Math.Min(1, withinPhase)) : 0);
        int pm = (int)Math.Round(Math.Min(1, f) * 1000);
        long now = clock.ElapsedMilliseconds;
        if (!force && pm == lastPermille && text == lastText) return;
        if (!force && now - lastEmit < 150) return;
        lastEmit = now; lastPermille = pm; lastText = text;
        int eta = -1;
        if (f > 0.04 && f < 1) eta = (int)Math.Ceiling(clock.Elapsed.TotalSeconds * (1 - f) / f);
        Console.WriteLine("P|" + pm + "|" + eta + "|" + text);
        Console.Out.Flush();
    }

    // ------------------------------------------------------------------ entry
    public static int Main(string[] args)
    {
        try
        {
            Need(args.Length == 2, "Unexpected installer call.");
            string mode = args[0].ToLowerInvariant();
            string dir = Path.GetFullPath(args[1].TrimEnd('\\', '"'));
            Need(Payload.Ok(Payload.Library, Payload.LIBRARY_SHA), "A required component is missing or damaged. Please download the installer again. Nothing was changed.");

            string msg;
            if (mode == "check") msg = Check(dir);
            else if (mode == "install") msg = Install(dir);
            else if (mode == "uninstall" || mode == "rollback") msg = Uninstall(dir);
            else throw new UserError("Unexpected installer call.");
            Console.WriteLine("OK|" + msg); Console.Out.Flush();
            return 0;
        }
        catch (UserError e) { Console.WriteLine("E|" + e.Message); Console.Out.Flush(); return 1; }
        catch (IOException e) { Console.WriteLine("E|A file could not be read or written (" + e.GetType().Name + "). Make sure the game is closed and try again."); return 1; }
        catch (UnauthorizedAccessException) { Console.WriteLine("E|Windows denied access to the game folder. Run the installer as administrator."); return 1; }
        catch (Exception e) { Console.WriteLine("E|Unexpected problem (" + e.GetType().Name + "). Nothing was changed."); return 1; }
    }

    // ------------------------------------------------------------------ checks shared by modes
    static string ForgePath(string dir)
    {
        string f = Path.Combine(dir, FORGE);
        Need(File.Exists(f), "This folder does not contain Assassin's Creed Black Flag Resynced. Please choose the game's installation folder.");
        return f;
    }

    static void NotRunning(string dir)
    {
        string prefix = dir.TrimEnd('\\') + "\\";
        int self = Process.GetCurrentProcess().Id;   // the installer places this engine inside the game folder
        foreach (Process p in Process.GetProcesses())
        {
            if (p.Id == self) continue;
            string path = null;
            try { path = p.MainModule.FileName; } catch { }
            if (path != null && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new UserError("The game is running. Please close it completely and try again.");
        }
    }

    static string Check(string dir)
    {
        ForgePath(dir); NotRunning(dir);
        return "ready";
    }

    // ------------------------------------------------------------------ install
    static string Install(string dir)
    {
        string forge = ForgePath(dir); NotRunning(dir);
        string bak = forge + BAK, legacy = forge + LEGACY, tmp = forge + TMP;
        Phases(3, 7, 60, 30);   // measured 5 Nov on an external HDD: check 0.2 s, prepare 1.2 s, write 10.9 s, verify 5.8 s
        Report(0, "Checking game files", true);
        if (File.Exists(tmp)) File.Delete(tmp);                    // our own leftover from an interrupted run

        Forge f = Forge.Read(forge); Bundle b = Open(f, true);
        string state = StateOf(b);
        if (state == "mod")
        {
            if (!File.Exists(bak) && File.Exists(legacy)) { Need(State(legacy, false) == "stock", DAMAGED); File.Move(legacy, bak); }
            Need(File.Exists(bak), "The mod is already applied, but no backup of the original file was found. Nothing was changed.");
            Report(1, "Already installed", true);
            return "The mod was already applied.";
        }
        Need(state == "stock", "This version of the game is not supported by this mod. Nothing was changed.");

        long need = new FileInfo(forge).Length + 200L * 1024 * 1024;
        Need(new DriveInfo(Path.GetPathRoot(forge)).AvailableFreeSpace > need,
             "Not enough free space on the game drive (about " + (need / (1L << 30) + 1) + " GB needed). Nothing was changed.");

        byte[] mod = Payload.Read(Payload.Motion);
        Need(Sha(mod) == Payload.MOTION_SHA, "The installer is damaged. Please download it again. Nothing was changed.");
        Build(f, b, mod, tmp);

        Next("Finishing");
        if (File.Exists(bak)) File.Delete(bak);                    // an older backup; the installed file is the original now
        if (File.Exists(legacy) && State(legacy, false) == "stock") File.Delete(legacy);
        File.Move(forge, bak);
        File.Move(tmp, forge);
        Report(1, "Done", true);
        return "Installed. The original file is kept as " + FORGE + BAK + ".";
    }

    // ------------------------------------------------------------------ uninstall / rollback
    static string Uninstall(string dir)
    {
        string forge = Path.Combine(dir, FORGE), bak = forge + BAK, legacy = forge + LEGACY, tmp = forge + TMP;
        Phases(60, 40);
        Report(0, "Checking game files", true);
        if (File.Exists(tmp)) File.Delete(tmp);
        if (!File.Exists(forge)) { Report(1, "Done", true); return "Nothing to restore."; }
        NotRunning(dir);
        if (State(forge, false) != "mod") { Report(1, "Done", true); return "The mod is not applied; nothing to restore."; }
        string backup = File.Exists(bak) ? bak : (File.Exists(legacy) ? legacy : null);
        Need(backup != null, "The backup of the original file is missing, so it cannot be restored. Use your game launcher's Verify files option.");
        Need(State(backup, false) == "stock", "The backup file is not the original game file, so it was not restored. Use your game launcher's Verify files option.");
        Next("Restoring the original file");
        string old = forge + ".usm-removed";
        if (File.Exists(old)) File.Delete(old);
        File.Move(forge, old);
        File.Move(backup, forge);
        File.Delete(old);
        Report(1, "Done", true);
        return "The original file is restored.";
    }

    // ------------------------------------------------------------------ archive model
    class Row { public ulong Off; public ulong Id; public uint Size; public uint Tag; }
    class Forge
    {
        public string Path; public byte[] Header; public int HeaderSize; public ulong TocAt; public List<Row> Rows = new List<Row>(); public int Bundle = -1;
        public static Forge Read(string path)
        {
            Forge f = new Forge(); f.Path = path;
            using (FileStream s = File.OpenRead(path))
            {
                BinaryReader r = new BinaryReader(s);
                Need(System.Text.Encoding.ASCII.GetString(r.ReadBytes(9)) == "scimitar\0" && r.ReadUInt32() == 50, DAMAGED);
                ulong hs = r.ReadUInt64(); Need(hs == 1050, DAMAGED); f.HeaderSize = (int)hs;
                s.Position = 0; f.Header = r.ReadBytes(f.HeaderSize + 20);
                uint count = BitConverter.ToUInt32(f.Header, f.HeaderSize);
                f.TocAt = BitConverter.ToUInt64(f.Header, f.HeaderSize + 4);
                Need(BitConverter.ToInt64(f.Header, f.HeaderSize + 12) == -1 && f.TocAt < (ulong)s.Length, DAMAGED);
                s.Position = (long)f.TocAt;
                for (int i = 0; i < count; i++)
                {
                    Row row = new Row(); row.Off = r.ReadUInt64(); row.Id = r.ReadUInt64(); row.Size = r.ReadUInt32(); row.Tag = r.ReadUInt32();
                    f.Rows.Add(row);
                    if (row.Id == BUNDLE_ID) { Need(f.Bundle < 0, DAMAGED); f.Bundle = i; }
                }
                Need(f.Rows.Count > 0 && f.Rows[0].Off == (ulong)f.Header.Length, DAMAGED);
                for (int i = 1; i < f.Rows.Count; i++) Need(f.Rows[i - 1].Off + f.Rows[i - 1].Size == f.Rows[i].Off, DAMAGED);
                Need(f.Rows[f.Rows.Count - 1].Off + f.Rows[f.Rows.Count - 1].Size == f.TocAt, DAMAGED);
                long pad = s.Length - s.Position;
                Need(pad == (ALIGN - s.Position % ALIGN) % ALIGN, DAMAGED);
                foreach (byte b in r.ReadBytes((int)pad)) Need(b == 0, DAMAGED);
            }
            Need(f.Bundle >= 0, "This version of the game is not supported by this mod. Nothing was changed.");
            return f;
        }
        public byte[] Payload(int i)
        {
            using (FileStream s = File.OpenRead(Path))
            {
                s.Position = (long)Rows[i].Off; byte[] b = new byte[Rows[i].Size]; Need(Fill(s, b, b.Length) == b.Length, DAMAGED); return b;
            }
        }
    }

    class Packet { public List<int> Sizes = new List<int>(); public List<byte[]> Packed = new List<byte[]>(); public byte[] Raw; }

    static Packet ReadPacket(byte[] d, ref int p, bool report, double share, double offset)
    {
        for (int i = 0; i < PACKET_HEADER.Length; i++) Need(d[p + i] == PACKET_HEADER[i], DAMAGED);
        p += PACKET_HEADER.Length;
        int count = BitConverter.ToInt32(d, p); p += 4; Need(count >= 1 && count <= 10000, DAMAGED);
        Packet k = new Packet(); int[] ps = new int[count]; long total = 0;
        for (int i = 0; i < count; i++)
        {
            int raw = BitConverter.ToInt32(d, p), packed = BitConverter.ToInt32(d, p + 4); p += 8;
            Need(packed > 0 && packed <= raw && raw <= BLOCK, DAMAGED); k.Sizes.Add(raw); ps[i] = packed; total += raw;
        }
        k.Raw = new byte[total]; long at = 0;
        for (int i = 0; i < count; i++)
        {
            uint sum = BitConverter.ToUInt32(d, p); p += 4;
            byte[] packed = new byte[ps[i]]; Buffer.BlockCopy(d, p, packed, 0, packed.Length); p += packed.Length;
            Need(Adler0(packed) == sum, DAMAGED); k.Packed.Add(packed);
            if (packed.Length == k.Sizes[i]) Buffer.BlockCopy(packed, 0, k.Raw, (int)at, packed.Length);
            else
            {
                byte[] o = new byte[k.Sizes[i] + 64];
                Need(OodleLZ_Decompress(packed, packed.Length, o, k.Sizes[i], 1, 0, 0, IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, 3) == k.Sizes[i], DAMAGED);
                Buffer.BlockCopy(o, 0, k.Raw, (int)at, k.Sizes[i]);
            }
            at += k.Sizes[i];
            if (report && (i & 15) == 0) Report(offset + share * (i + 1) / count, "Checking game files", false);
        }
        return k;
    }

    class Bundle { public Packet Toc, Body; public int Index, Start, OldSize, At; public byte[] Data; }

    static Bundle Open(Forge f, bool report)
    {
        byte[] data = f.Payload(f.Bundle); int p = 0;
        Bundle b = new Bundle();
        b.Toc = ReadPacket(data, ref p, false, 0, 0);
        b.Body = ReadPacket(data, ref p, report, 1.0, 0);
        Need(p == data.Length && b.Toc.Sizes.Count == 1, DAMAGED);
        byte[] toc = b.Toc.Raw;
        Need((toc.Length - 20) % 18 == 0 && BitConverter.ToUInt64(toc, 2) == BUNDLE_ID, DAMAGED);
        long sum = BitConverter.ToUInt32(toc, 10); b.Index = -1; long start = 0;
        for (int i = 0; i < (toc.Length - 20) / 18; i++)
        {
            ulong id = BitConverter.ToUInt64(toc, 20 + 18 * i); uint size = BitConverter.ToUInt32(toc, 28 + 18 * i);
            if (id == TARGET_ID) { Need(b.Index < 0, DAMAGED); b.Index = i; start = sum; b.OldSize = (int)size; }
            sum += size;
        }
        Need(b.Index >= 0, "This version of the game is not supported by this mod. Nothing was changed.");
        Need(sum == b.Body.Raw.Length, DAMAGED);
        b.Start = (int)start; byte[] body = b.Body.Raw;
        Need(BitConverter.ToUInt32(body, b.Start) == TARGET_TYPE, DAMAGED);
        b.At = b.Start + 12 + (int)BitConverter.ToUInt32(body, b.Start + 8);
        b.Data = new byte[b.Start + b.OldSize - b.At]; Buffer.BlockCopy(body, b.At, b.Data, 0, b.Data.Length);
        int bs; Header(b.Data, out bs); Need(bs == (int)BitConverter.ToUInt32(body, b.Start + 4), DAMAGED);
        return b;
    }

    static void Header(byte[] a, out int bodySize)
    {
        Need(a.Length > 40 && a[0] == 1 && a[1] == 2 && a[2] == 0 && a[3] == 0, DAMAGED);
        int at = 8 + 12 * (int)BitConverter.ToUInt32(a, 4);
        Need(BitConverter.ToUInt64(a, at) == TARGET_ID && BitConverter.ToUInt32(a, at + 8) == TARGET_TYPE && BitConverter.ToUInt32(a, at + 17) == TARGET_KEY, DAMAGED);
        bodySize = a.Length - at;
    }

    static string State(string path, bool report) { return StateOf(Open(Forge.Read(path), report)); }

    static string StateOf(Bundle b)
    {
        string h = Sha(b.Data);
        if (h == Payload.ORIGINAL_SHA) return "stock";
        if (h == Payload.MOTION_SHA) return "mod";
        return "unknown";
    }

    // ------------------------------------------------------------------ rebuild
    static void Build(Forge f, Bundle b, byte[] mod, string outPath)
    {
        string forgePath = f.Path;
        int newBody; Header(mod, out newBody);
        int delta = mod.Length - b.Data.Length;
        byte[] toc = (byte[])b.Toc.Raw.Clone();
        BitConverter.GetBytes((uint)(b.OldSize + delta)).CopyTo(toc, 20 + 18 * b.Index + 8);
        byte[] old = b.Body.Raw, body = new byte[old.Length + delta];
        Buffer.BlockCopy(old, 0, body, 0, b.At);
        BitConverter.GetBytes((uint)newBody).CopyTo(body, b.Start + 4);
        Buffer.BlockCopy(mod, 0, body, b.At, mod.Length);
        int after = b.Start + b.OldSize;
        Buffer.BlockCopy(old, after, body, b.At + mod.Length, old.Length - after);

        Next("Preparing the new sprint");
        byte[] pt = Encode(b.Toc, toc, false), pb = Encode(b.Body, body, true);
        byte[] bundle = new byte[pt.Length + pb.Length];
        Buffer.BlockCopy(pt, 0, bundle, 0, pt.Length); Buffer.BlockCopy(pb, 0, bundle, pt.Length, pb.Length);

        Next("Writing game files");
        Row br = f.Rows[f.Bundle]; long shift = (long)bundle.Length - br.Size; ulong tocAt = (ulong)((long)f.TocAt + shift);
        long total = (long)f.TocAt + shift, written = 0;
        using (FileStream src = File.OpenRead(forgePath))
        using (FileStream dst = new FileStream(outPath, FileMode.CreateNew, FileAccess.Write))
        {
            byte[] header = (byte[])f.Header.Clone(); BitConverter.GetBytes(tocAt).CopyTo(header, f.HeaderSize + 4);
            dst.Write(header, 0, header.Length);
            src.Position = header.Length; Copy(src, dst, (long)br.Off - header.Length, ref written, total, "Writing game files");
            dst.Write(bundle, 0, bundle.Length); written += bundle.Length;
            src.Position = (long)br.Off + br.Size; Copy(src, dst, (long)f.TocAt - src.Position, ref written, total, "Writing game files");
            BinaryWriter w = new BinaryWriter(dst);
            for (int i = 0; i < f.Rows.Count; i++)
            {
                Row r = f.Rows[i];
                w.Write(i > f.Bundle ? (ulong)((long)r.Off + shift) : r.Off); w.Write(r.Id);
                w.Write(i == f.Bundle ? (uint)bundle.Length : r.Size); w.Write(r.Tag);
            }
            long end = (long)tocAt + 24L * f.Rows.Count; w.Write(new byte[(ALIGN - end % ALIGN) % ALIGN]);
            w.Flush(); dst.Flush(true);
        }

        Next("Verifying");
        Forge g = Forge.Read(outPath);
        Need(g.Rows.Count == f.Rows.Count && g.Bundle == f.Bundle, "Verification failed. Nothing was changed.");
        Bundle nb = Open(g, false);
        Need(Sha(nb.Data) == Payload.MOTION_SHA && Same(nb.Toc.Raw, toc) && Same(nb.Body.Raw, body), "Verification failed. Nothing was changed.");
        long vTotal = 0, vDone = 0; foreach (Row r in f.Rows) vTotal += r.Size;
        using (FileStream a = File.OpenRead(forgePath))
        using (FileStream c = File.OpenRead(outPath))
        {
            for (int i = 0; i < f.Rows.Count; i++)
            {
                if (i == f.Bundle) { vDone += f.Rows[i].Size; continue; }
                Need(g.Rows[i].Id == f.Rows[i].Id && g.Rows[i].Size == f.Rows[i].Size && g.Rows[i].Tag == f.Rows[i].Tag, "Verification failed. Nothing was changed.");
                a.Position = (long)f.Rows[i].Off; c.Position = (long)g.Rows[i].Off;
                Need(SameStream(a, c, f.Rows[i].Size, ref vDone, vTotal), "Verification failed. Nothing was changed.");
            }
        }
    }

    static byte[] Encode(Packet stock, byte[] raw, bool report)
    {
        int count = stock.Sizes.Count; byte[][] chunks = new byte[count][]; int off = 0;
        for (int i = 0; i < count; i++)
        {
            int len = i < count - 1 ? stock.Sizes[i] : raw.Length - off;
            Need(len > 0 && len <= BLOCK, "This version of the game is not supported by this mod. Nothing was changed.");
            chunks[i] = new byte[len]; Buffer.BlockCopy(raw, off, chunks[i], 0, len); off += len;
        }
        byte[][] packed = new byte[count][]; int sOff = 0; List<int> todo = new List<int>();
        for (int i = 0; i < count; i++)
        {
            bool eq = chunks[i].Length == stock.Sizes[i];
            if (eq) for (int j = 0; j < chunks[i].Length; j++) if (chunks[i][j] != stock.Raw[sOff + j]) { eq = false; break; }
            if (eq) packed[i] = stock.Packed[i]; else todo.Add(i);
            sOff += stock.Sizes[i];
        }
        int done = 0; object gate = new object();
        Parallel.ForEach(todo, i =>
        {
            packed[i] = Compress(chunks[i]);
            if (report) lock (gate) { done++; Report((double)done / todo.Count, "Preparing the new sprint", false); }
        });
        MemoryStream ms = new MemoryStream(); BinaryWriter w = new BinaryWriter(ms);
        w.Write(PACKET_HEADER); w.Write(count);
        for (int i = 0; i < count; i++) { w.Write(chunks[i].Length); w.Write(packed[i].Length); }
        for (int i = 0; i < count; i++) { w.Write(Adler0(packed[i])); w.Write(packed[i]); }
        w.Flush(); return ms.ToArray();
    }

    static byte[] Compress(byte[] chunk)
    {
        byte[] o = new byte[chunk.Length + 65536];
        long n = OodleLZ_Compress(9, chunk, chunk.Length, o, 7, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0);
        Need(n > 0 && n <= o.Length, "A required component failed. Nothing was changed.");
        if (n * 10 >= (long)chunk.Length * 9) return chunk;
        byte[] r = new byte[n]; Buffer.BlockCopy(o, 0, r, 0, (int)n); return r;
    }

    // ------------------------------------------------------------------ helpers
    static uint Adler0(byte[] d)
    {
        uint a = 0, b = 0; int i = 0, n = d.Length;
        while (n > 0) { int t = n < 5552 ? n : 5552; n -= t; while (t-- > 0) { a += d[i++]; b += a; } a %= 65521; b %= 65521; }
        return (b << 16) | a;
    }
    static string Sha(byte[] d) { using (SHA256 h = SHA256.Create()) return BitConverter.ToString(h.ComputeHash(d)).Replace("-", "").ToLowerInvariant(); }
    static bool Same(byte[] x, byte[] y) { if (x.Length != y.Length) return false; for (int i = 0; i < x.Length; i++) if (x[i] != y[i]) return false; return true; }
    static int Fill(Stream s, byte[] buf, int want) { int got = 0; while (got < want) { int k = s.Read(buf, got, want - got); if (k <= 0) break; got += k; } return got; }
    static void Copy(Stream s, Stream d, long n, ref long written, long total, string text)
    {
        byte[] buf = new byte[4 << 20];
        while (n > 0)
        {
            int k = s.Read(buf, 0, (int)Math.Min(buf.Length, n)); Need(k > 0, DAMAGED);
            d.Write(buf, 0, k); n -= k; written += k; Report((double)written / total, text, false);
        }
    }
    static bool SameStream(Stream a, Stream b, long n, ref long done, long total)
    {
        byte[] x = new byte[4 << 20], y = new byte[4 << 20];
        while (n > 0)
        {
            int want = (int)Math.Min(x.Length, n);
            if (Fill(a, x, want) != want || Fill(b, y, want) != want) return false;
            for (int i = 0; i < want; i++) if (x[i] != y[i]) return false;
            n -= want; done += want; Report((double)done / total, "Verifying", false);
        }
        return true;
    }
}

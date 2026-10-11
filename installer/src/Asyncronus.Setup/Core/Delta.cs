// Delta binario (algoritmo bsdiff de Colin Percival, BSD-2; reimplementado aqui) con formato propio "ASYNPAT1".
// Crear: solo se usa en build.ps1. Aplicar: lo usa el instalador en el PC del usuario.
// Formato: magic(8) | newSize(i64) | sha256(old)(32) | sha256(new)(32) | 3 bloques [rawLen i64][compLen i64][deflate]: ctrl, diff, extra
using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Asyncronus.Setup
{
    public static class Delta
    {
        const string Magic = "ASYNPAT1";

        public static string Sha256Hex(byte[] data)
        {
            using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(data));
        }

        public static string Sha256Hex(string file)
        {
            using (var sha = SHA256.Create())
            using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                return Hex(sha.ComputeHash(fs));
        }

        static string Hex(byte[] h)
        {
            var c = new char[h.Length * 2];
            for (int i = 0; i < h.Length; i++) { c[i * 2] = "0123456789abcdef"[h[i] >> 4]; c[i * 2 + 1] = "0123456789abcdef"[h[i] & 15]; }
            return new string(c);
        }

        // ---------------------------------------------------------------- aplicar
        public static byte[] Apply(byte[] old, string patchFile)
        {
            using (var fs = File.OpenRead(patchFile))
            using (var br = new BinaryReader(fs))
            {
                var magic = new string(br.ReadChars(8));
                if (magic != Magic) throw new InvalidDataException("Fichero de parche no valido.");
                long newSize = br.ReadInt64();
                byte[] oldSha = br.ReadBytes(32), newSha = br.ReadBytes(32);
                using (var sha = SHA256.Create())
                    if (Hex(sha.ComputeHash(old)) != Hex(oldSha)) throw new InvalidDataException("El fichero original no coincide con el que espera el parche.");
                byte[] ctrl = ReadBlock(br), diff = ReadBlock(br), extra = ReadBlock(br);

                var nw = new byte[newSize];
                long oldpos = 0, newpos = 0; int cp = 0, dp = 0, ep = 0;
                while (newpos < newSize)
                {
                    long c0 = BitConverter.ToInt64(ctrl, cp), c1 = BitConverter.ToInt64(ctrl, cp + 8), c2 = BitConverter.ToInt64(ctrl, cp + 16);
                    cp += 24;
                    if (c0 < 0 || c1 < 0 || newpos + c0 + c1 > newSize) throw new InvalidDataException("Parche corrupto.");
                    for (long i = 0; i < c0; i++)
                    {
                        byte b = diff[dp++];
                        long o = oldpos + i;
                        if (o >= 0 && o < old.Length) b = unchecked((byte)(b + old[o]));
                        nw[newpos + i] = b;
                    }
                    newpos += c0; oldpos += c0;
                    Buffer.BlockCopy(extra, ep, nw, (int)newpos, (int)c1);
                    ep += (int)c1; newpos += c1; oldpos += c2;
                }
                using (var sha = SHA256.Create())
                    if (Hex(sha.ComputeHash(nw)) != Hex(newSha)) throw new InvalidDataException("El resultado de aplicar el parche no tiene el hash esperado.");
                return nw;
            }
        }

        static byte[] ReadBlock(BinaryReader br)
        {
            long raw = br.ReadInt64(), comp = br.ReadInt64();
            var ms = new MemoryStream(br.ReadBytes((int)comp));
            var res = new byte[raw];
            using (var ds = new DeflateStream(ms, CompressionMode.Decompress))
            {
                int got = 0;
                while (got < raw) { int n = ds.Read(res, got, (int)raw - got); if (n <= 0) throw new EndOfStreamException(); got += n; }
            }
            return res;
        }

        // ---------------------------------------------------------------- crear
        public static void Create(byte[] old, byte[] nw, string patchFile)
        {
            var ctrl = new MemoryStream(); var diff = new MemoryStream(); var extra = new MemoryStream();
            BsDiff(old, nw, ctrl, diff, extra);
            using (var fs = File.Create(patchFile))
            using (var bw = new BinaryWriter(fs))
            {
                bw.Write(System.Text.Encoding.ASCII.GetBytes(Magic));
                bw.Write((long)nw.Length);
                using (var sha = SHA256.Create()) { bw.Write(sha.ComputeHash(old)); bw.Write(sha.ComputeHash(nw)); }
                WriteBlock(bw, ctrl.ToArray()); WriteBlock(bw, diff.ToArray()); WriteBlock(bw, extra.ToArray());
            }
        }

        static void WriteBlock(BinaryWriter bw, byte[] raw)
        {
            var ms = new MemoryStream();
            using (var ds = new DeflateStream(ms, CompressionLevel.Optimal, true)) ds.Write(raw, 0, raw.Length);
            bw.Write((long)raw.Length); bw.Write((long)ms.Length); bw.Write(ms.ToArray());
        }

        static void Split(int[] I, int[] V, int start, int len, int h)
        {
            int i, j, k, x, tmp, jj, kk;
            if (len < 16)
            {
                for (k = start; k < start + len; k += j)
                {
                    j = 1; x = V[I[k] + h];
                    for (i = 1; k + i < start + len; i++)
                    {
                        if (V[I[k + i] + h] < x) { x = V[I[k + i] + h]; j = 0; }
                        if (V[I[k + i] + h] == x) { tmp = I[k + j]; I[k + j] = I[k + i]; I[k + i] = tmp; j++; }
                    }
                    for (i = 0; i < j; i++) V[I[k + i]] = k + j - 1;
                    if (j == 1) I[k] = -1;
                }
                return;
            }
            x = V[I[start + len / 2] + h];
            jj = 0; kk = 0;
            for (i = start; i < start + len; i++)
            {
                if (V[I[i] + h] < x) jj++;
                if (V[I[i] + h] == x) kk++;
            }
            jj += start; kk += jj;
            i = start; j = 0; k = 0;
            while (i < jj)
            {
                if (V[I[i] + h] < x) i++;
                else if (V[I[i] + h] == x) { tmp = I[i]; I[i] = I[jj + j]; I[jj + j] = tmp; j++; }
                else { tmp = I[i]; I[i] = I[kk + k]; I[kk + k] = tmp; k++; }
            }
            while (jj + j < kk)
            {
                if (V[I[jj + j] + h] == x) j++;
                else { tmp = I[jj + j]; I[jj + j] = I[kk + k]; I[kk + k] = tmp; k++; }
            }
            if (jj > start) Split(I, V, start, jj - start, h);
            for (i = 0; i < kk - jj; i++) V[I[jj + i]] = kk - 1;
            if (jj == kk - 1) I[jj] = -1;
            if (start + len > kk) Split(I, V, kk, start + len - kk, h);
        }

        static int[] QsufSort(byte[] old)
        {
            int n = old.Length;
            var I = new int[n + 1]; var V = new int[n + 1];
            var buckets = new int[256];
            int i, h, len;
            for (i = 0; i < n; i++) buckets[old[i]]++;
            for (i = 1; i < 256; i++) buckets[i] += buckets[i - 1];
            for (i = 255; i > 0; i--) buckets[i] = buckets[i - 1];
            buckets[0] = 0;
            for (i = 0; i < n; i++) I[++buckets[old[i]]] = i;
            I[0] = n;
            for (i = 0; i < n; i++) V[i] = buckets[old[i]];
            V[n] = 0;
            for (i = 1; i < 256; i++) if (buckets[i] == buckets[i - 1] + 1) I[buckets[i]] = -1;
            I[0] = -1;
            for (h = 1; I[0] != -(n + 1); h += h)
            {
                len = 0;
                for (i = 0; i < n + 1;)
                {
                    if (I[i] < 0) { len -= I[i]; i -= I[i]; }
                    else
                    {
                        if (len != 0) I[i - len] = -len;
                        len = V[I[i]] + 1 - i;
                        Split(I, V, i, len, h);
                        i += len; len = 0;
                    }
                }
                if (len != 0) I[i - len] = -len;
            }
            for (i = 0; i < n + 1; i++) I[V[i]] = i;
            return I;
        }

        static int MatchLen(byte[] a, int ao, byte[] b, int bo)
        {
            int i = 0;
            while (ao + i < a.Length && bo + i < b.Length && a[ao + i] == b[bo + i]) i++;
            return i;
        }

        static int Search(int[] I, byte[] old, byte[] nw, int nwo, int st, int en, out int pos)
        {
            if (en - st < 2)
            {
                int x = MatchLen(old, I[st], nw, nwo), y = MatchLen(old, I[en], nw, nwo);
                if (x > y) { pos = I[st]; return x; }
                pos = I[en]; return y;
            }
            int m = st + (en - st) / 2;
            // comparar old[I[m]..] con nw[nwo..]
            int a = I[m], b = nwo, cmp = 0;
            while (a < old.Length && b < nw.Length) { if (old[a] != nw[b]) { cmp = old[a] < nw[b] ? -1 : 1; break; } a++; b++; }
            if (cmp < 0) return Search(I, old, nw, nwo, m, en, out pos);
            return Search(I, old, nw, nwo, st, m, out pos);
        }

        static void BsDiff(byte[] old, byte[] nw, Stream ctrl, Stream diff, Stream extra)
        {
            int oldsize = old.Length, newsize = nw.Length;
            var I = QsufSort(old);
            var db = new byte[newsize + 1]; var eb = new byte[newsize + 1];
            int dblen = 0, eblen = 0;
            int scan = 0, pos = 0, len = 0, lastscan = 0, lastpos = 0, lastoffset = 0;
            int i, s, Sf, lenf, Sb, lenb, overlap, Ss, lens, oldscore, scsc;
            var cw = new BinaryWriter(ctrl);
            while (scan < newsize)
            {
                oldscore = 0;
                for (scsc = scan += len; scan < newsize; scan++)
                {
                    len = Search(I, old, nw, scan, 0, oldsize, out pos);
                    for (; scsc < scan + len; scsc++)
                        if (scsc + lastoffset < oldsize && old[scsc + lastoffset] == nw[scsc]) oldscore++;
                    if ((len == oldscore && len != 0) || len > oldscore + 8) break;
                    if (scan + lastoffset < oldsize && old[scan + lastoffset] == nw[scan]) oldscore--;
                }
                if (len != oldscore || scan == newsize)
                {
                    s = 0; Sf = 0; lenf = 0;
                    for (i = 0; lastscan + i < scan && lastpos + i < oldsize;)
                    {
                        if (old[lastpos + i] == nw[lastscan + i]) s++;
                        i++;
                        if (s * 2 - i > Sf * 2 - lenf) { Sf = s; lenf = i; }
                    }
                    lenb = 0;
                    if (scan < newsize)
                    {
                        s = 0; Sb = 0;
                        for (i = 1; scan >= lastscan + i && pos >= i; i++)
                        {
                            if (old[pos - i] == nw[scan - i]) s++;
                            if (s * 2 - i > Sb * 2 - lenb) { Sb = s; lenb = i; }
                        }
                    }
                    if (lastscan + lenf > scan - lenb)
                    {
                        overlap = (lastscan + lenf) - (scan - lenb);
                        s = 0; Ss = 0; lens = 0;
                        for (i = 0; i < overlap; i++)
                        {
                            if (nw[lastscan + lenf - overlap + i] == old[lastpos + lenf - overlap + i]) s++;
                            if (nw[scan - lenb + i] == old[pos - lenb + i]) s--;
                            if (s > Ss) { Ss = s; lens = i + 1; }
                        }
                        lenf += lens - overlap;
                        lenb -= lens;
                    }
                    for (i = 0; i < lenf; i++) db[dblen + i] = unchecked((byte)(nw[lastscan + i] - old[lastpos + i]));
                    int extralen = (scan - lenb) - (lastscan + lenf);
                    for (i = 0; i < extralen; i++) eb[eblen + i] = nw[lastscan + lenf + i];
                    dblen += lenf; eblen += extralen;
                    cw.Write((long)lenf); cw.Write((long)extralen); cw.Write((long)((pos - lenb) - (lastpos + lenf)));
                    lastscan = scan - lenb; lastpos = pos - lenb; lastoffset = pos - scan;
                }
            }
            cw.Flush();
            diff.Write(db, 0, dblen); extra.Write(eb, 0, eblen);
        }
    }
}

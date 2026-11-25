using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace DynamicIsland.Services;

/// <summary>
/// Переупаковывает фрагментированные HLS fMP4-чанки SoundCloud (styp/sidx/moof/mdat с duration=0)
/// в стандартный монолитный ISO-BMFF M4A (ftyp + moov + mdat) с точными таблицами перемотки.
/// </summary>
public static class Mp4FragmentRepacker
{
    private readonly record struct BoxRange(string Type, int Start, int TotalSize, int HeaderSize)
    {
        public int PayloadStart => Start + HeaderSize;
        public int End => Start + TotalSize;
    }

    private readonly record struct ChunkSlice(int SampleCount, byte[] Data, int Offset, int Length);

    public static bool TryRepackToStandardM4a(byte[][] chunks, string outputPath)
    {
        if (chunks == null || chunks.Length < 2 || string.IsNullOrWhiteSpace(outputPath))
            return false;

        try
        {
            var init = chunks[0];
            if (init == null || init.Length < 24)
                return false;

            if (!TryParseInitSegment(
                    init,
                    out uint mediaTimescale,
                    out byte[]? stsdBox,
                    out byte[]? hdlrBox,
                    out byte[]? smhdBox,
                    out byte[]? dinfBox,
                    out uint trexDefDur,
                    out uint trexDefSize))
            {
                return false;
            }

            if (mediaTimescale == 0 || stsdBox == null)
                return false;

            var sampleSizes = new List<uint>(chunks.Length * 450);
            var sttsRuns = new List<(uint Count, uint Delta)>();
            var chunkSlices = new List<ChunkSlice>(chunks.Length - 1);
            ulong totalMediaDuration = 0;
            long totalAudioBytes = 0;

            for (int c = 1; c < chunks.Length; c++)
            {
                var chunk = chunks[c];
                if (chunk == null || chunk.Length < 16)
                    continue;

                if (!TryParseMediaFragment(
                        chunk,
                        trexDefDur,
                        trexDefSize,
                        out int segSamples,
                        out int audioOffset,
                        out int audioLength,
                        sampleSizes,
                        sttsRuns,
                        ref totalMediaDuration))
                {
                    return false;
                }

                if (segSamples > 0 && audioLength > 0)
                {
                    chunkSlices.Add(new ChunkSlice(segSamples, chunk, audioOffset, audioLength));
                    totalAudioBytes += audioLength;
                }
            }

            if (sampleSizes.Count == 0 || chunkSlices.Count == 0 || totalAudioBytes <= 0)
                return false;

            byte[] ftyp = BuildStandardM4aFtyp();
            byte[] mvhd = BuildMvhd(totalMediaDuration, mediaTimescale);
            ulong mvhdDurationMs = (ulong)Math.Round(totalMediaDuration * 1000.0 / mediaTimescale);
            byte[] tkhd = BuildTkhd(mvhdDurationMs);
            byte[] mdhd = BuildMdhd(mediaTimescale, totalMediaDuration);
            byte[] stts = BuildStts(sttsRuns);
            byte[] stsc = BuildStsc(chunkSlices);
            byte[] stsz = BuildStsz(sampleSizes);
            byte[] stco = new byte[16 + chunkSlices.Count * 4];
            WriteBoxHeader(stco, "stco", (uint)stco.Length, versionAndFlags: 0);
            BinaryPrimitives.WriteUInt32BigEndian(stco.AsSpan(12, 4), (uint)chunkSlices.Count);

            byte[] stbl = WrapContainer("stbl", stsdBox, stts, stsc, stsz, stco);
            byte[] minf = WrapContainer(
                "minf",
                smhdBox ?? BuildDefaultSmhd(),
                dinfBox ?? BuildDefaultDinf(),
                stbl);
            byte[] mdia = WrapContainer("mdia", mdhd, hdlrBox ?? BuildDefaultSoundHdlr(), minf);
            byte[] trak = WrapContainer("trak", tkhd, mdia);
            byte[] moov = WrapContainer("moov", mvhd, trak);

            long estimatedTotalSize = ftyp.Length + moov.Length + 8L + totalAudioBytes;
            if (estimatedTotalSize > uint.MaxValue)
                return false;

            // Заполняем смещения чанков (указывают прямо на начало аудиоданных внутри mdat)
            int stcoOffsetInMoov = FindLastChildBoxOffset(moov, "stco", out int stcoLen);
            if (stcoOffsetInMoov < 0 || stcoLen != stco.Length)
                return false;

            uint curDataOffset = (uint)(ftyp.Length + moov.Length + 8);
            int entriesBase = stcoOffsetInMoov + 16;
            for (int i = 0; i < chunkSlices.Count; i++)
            {
                BinaryPrimitives.WriteUInt32BigEndian(
                    moov.AsSpan(entriesBase + i * 4, 4),
                    curDataOffset);
                curDataOffset += (uint)chunkSlices[i].Length;
            }

            var parentDir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(parentDir))
                Directory.CreateDirectory(parentDir);

            using (var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920))
            {
                fs.Write(ftyp);
                fs.Write(moov);

                Span<byte> mdatHeader = stackalloc byte[8];
                BinaryPrimitives.WriteUInt32BigEndian(mdatHeader[..4], (uint)(8 + totalAudioBytes));
                Encoding.ASCII.GetBytes("mdat", mdatHeader.Slice(4, 4));
                fs.Write(mdatHeader);

                foreach (var slice in chunkSlices)
                    fs.Write(slice.Data, slice.Offset, slice.Length);
            }

            var info = new FileInfo(outputPath);
            return info.Exists && info.Length > 10_000;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParseInitSegment(
        byte[] init,
        out uint mediaTimescale,
        out byte[]? stsdBox,
        out byte[]? hdlrBox,
        out byte[]? smhdBox,
        out byte[]? dinfBox,
        out uint trexDefDur,
        out uint trexDefSize)
    {
        mediaTimescale = 0;
        stsdBox = null;
        hdlrBox = null;
        smhdBox = null;
        dinfBox = null;
        trexDefDur = 0;
        trexDefSize = 0;

        if (!TryFindChildBox(init, 0, init.Length, "moov", out var moov))
            return false;

        if (TryFindChildBox(init, moov.PayloadStart, moov.End, "mvex", out var mvex) &&
            TryFindChildBox(init, mvex.PayloadStart, mvex.End, "trex", out var trex) &&
            trex.PayloadStart + 24 <= trex.End)
        {
            trexDefDur = BinaryPrimitives.ReadUInt32BigEndian(init.AsSpan(trex.PayloadStart + 12, 4));
            trexDefSize = BinaryPrimitives.ReadUInt32BigEndian(init.AsSpan(trex.PayloadStart + 16, 4));
        }

        // Ищем аудиотрек (mdia с stsd или hdlr='soun')
        int pos = moov.PayloadStart;
        while (TryReadBox(init, pos, moov.End, out var trak))
        {
            if (trak.Type == "trak" &&
                TryFindChildBox(init, trak.PayloadStart, trak.End, "mdia", out var mdia))
            {
                byte[]? candHdlr = null;
                byte[]? candSmhd = null;
                byte[]? candDinf = null;
                byte[]? candStsd = null;
                uint candScale = 0;

                if (TryFindChildBox(init, mdia.PayloadStart, mdia.End, "mdhd", out var mdhd) &&
                    mdhd.PayloadStart + 16 <= mdhd.End)
                {
                    byte ver = init[mdhd.PayloadStart];
                    if (ver == 0 && mdhd.PayloadStart + 20 <= mdhd.End)
                        candScale = BinaryPrimitives.ReadUInt32BigEndian(init.AsSpan(mdhd.PayloadStart + 12, 4));
                    else if (ver == 1 && mdhd.PayloadStart + 28 <= mdhd.End)
                        candScale = BinaryPrimitives.ReadUInt32BigEndian(init.AsSpan(mdhd.PayloadStart + 20, 4));
                }

                if (TryFindChildBox(init, mdia.PayloadStart, mdia.End, "hdlr", out var hdlr))
                    candHdlr = init.AsSpan(hdlr.Start, hdlr.TotalSize).ToArray();

                if (TryFindChildBox(init, mdia.PayloadStart, mdia.End, "minf", out var minf))
                {
                    if (TryFindChildBox(init, minf.PayloadStart, minf.End, "smhd", out var smhd))
                        candSmhd = init.AsSpan(smhd.Start, smhd.TotalSize).ToArray();
                    if (TryFindChildBox(init, minf.PayloadStart, minf.End, "dinf", out var dinf))
                        candDinf = init.AsSpan(dinf.Start, dinf.TotalSize).ToArray();
                    if (TryFindChildBox(init, minf.PayloadStart, minf.End, "stbl", out var stbl) &&
                        TryFindChildBox(init, stbl.PayloadStart, stbl.End, "stsd", out var stsd))
                    {
                        candStsd = init.AsSpan(stsd.Start, stsd.TotalSize).ToArray();
                    }
                }

                if (candScale > 0 && candStsd != null)
                {
                    mediaTimescale = candScale;
                    stsdBox = candStsd;
                    hdlrBox = candHdlr;
                    smhdBox = candSmhd;
                    dinfBox = candDinf;
                    return true;
                }
            }
            pos = trak.End;
        }

        return false;
    }

    private static bool TryParseMediaFragment(
        byte[] chunk,
        uint trexDefDur,
        uint trexDefSize,
        out int segSamples,
        out int audioOffset,
        out int audioLength,
        List<uint> sampleSizes,
        List<(uint Count, uint Delta)> sttsRuns,
        ref ulong totalMediaDuration)
    {
        segSamples = 0;
        audioOffset = 0;
        audioLength = 0;

        if (!TryFindChildBox(chunk, 0, chunk.Length, "moof", out var moof))
            return false;

        TryFindChildBox(chunk, 0, chunk.Length, "mdat", out var mdat);

        if (!TryFindChildBox(chunk, moof.PayloadStart, moof.End, "traf", out var traf))
            return false;

        uint segDefDur = trexDefDur;
        uint segDefSize = trexDefSize;

        if (TryFindChildBox(chunk, traf.PayloadStart, traf.End, "tfhd", out var tfhd) &&
            tfhd.PayloadStart + 8 <= tfhd.End)
        {
            uint tfhdFlags = ReadUInt24(chunk, tfhd.PayloadStart + 1);
            int off = tfhd.PayloadStart + 8; // version(1)+flags(3)+track_id(4)
            if ((tfhdFlags & 0x01) != 0) off += 8; // base-data-offset
            if ((tfhdFlags & 0x02) != 0) off += 4; // sample-description-index
            if ((tfhdFlags & 0x08) != 0 && off + 4 <= tfhd.End)
            {
                segDefDur = BinaryPrimitives.ReadUInt32BigEndian(chunk.AsSpan(off, 4));
                off += 4;
            }
            if ((tfhdFlags & 0x10) != 0 && off + 4 <= tfhd.End)
            {
                segDefSize = BinaryPrimitives.ReadUInt32BigEndian(chunk.AsSpan(off, 4));
            }
        }

        int? trunDataOffset = null;
        int segAudioBytes = 0;

        int pos = traf.PayloadStart;
        while (TryReadBox(chunk, pos, traf.End, out var child))
        {
            if (child.Type == "trun")
            {
                if (child.PayloadStart + 8 > child.End)
                    return false;

                uint trunFlags = ReadUInt24(chunk, child.PayloadStart + 1);
                uint sampleCount = BinaryPrimitives.ReadUInt32BigEndian(chunk.AsSpan(child.PayloadStart + 4, 4));
                int off = child.PayloadStart + 8;

                if ((trunFlags & 0x01) != 0)
                {
                    if (off + 4 > child.End) return false;
                    int dOff = BinaryPrimitives.ReadInt32BigEndian(chunk.AsSpan(off, 4));
                    trunDataOffset ??= dOff;
                    off += 4;
                }
                if ((trunFlags & 0x04) != 0)
                    off += 4; // first-sample-flags

                for (uint i = 0; i < sampleCount; i++)
                {
                    uint dur;
                    if ((trunFlags & 0x100) != 0)
                    {
                        if (off + 4 > child.End) return false;
                        dur = BinaryPrimitives.ReadUInt32BigEndian(chunk.AsSpan(off, 4));
                        off += 4;
                    }
                    else
                    {
                        dur = segDefDur > 0 ? segDefDur : 1024u;
                    }

                    uint size;
                    if ((trunFlags & 0x200) != 0)
                    {
                        if (off + 4 > child.End) return false;
                        size = BinaryPrimitives.ReadUInt32BigEndian(chunk.AsSpan(off, 4));
                        off += 4;
                    }
                    else
                    {
                        size = segDefSize;
                    }

                    if ((trunFlags & 0x400) != 0) off += 4; // sample-flags
                    if ((trunFlags & 0x800) != 0) off += 4; // sample-composition-time-offset

                    if (size == 0 || segAudioBytes + (long)size > int.MaxValue)
                        return false;

                    sampleSizes.Add(size);
                    if (sttsRuns.Count > 0 && sttsRuns[^1].Delta == dur)
                    {
                        var last = sttsRuns[^1];
                        sttsRuns[^1] = (last.Count + 1, dur);
                    }
                    else
                    {
                        sttsRuns.Add((1, dur));
                    }

                    totalMediaDuration += dur;
                    segAudioBytes += (int)size;
                    segSamples++;
                }
            }
            pos = child.End;
        }

        if (segSamples == 0 || segAudioBytes <= 0)
            return false;

        int resolvedStart = -1;
        if (trunDataOffset.HasValue)
        {
            long candidateStart = (long)moof.Start + trunDataOffset.Value;
            if (candidateStart >= 0 && candidateStart + segAudioBytes <= chunk.Length)
                resolvedStart = (int)candidateStart;
        }

        if (resolvedStart < 0 && mdat.TotalSize > 0 &&
            mdat.PayloadStart + segAudioBytes <= chunk.Length)
        {
            resolvedStart = mdat.PayloadStart;
        }

        if (resolvedStart < 0)
            return false;

        audioOffset = resolvedStart;
        audioLength = segAudioBytes;
        return true;
    }

    private static bool TryReadBox(byte[] buf, int offset, int limit, out BoxRange box)
    {
        box = default;
        if (offset < 0 || offset + 8 > limit)
            return false;

        uint sz32 = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(offset, 4));
        string type = Encoding.ASCII.GetString(buf, offset + 4, 4);

        int headerSize = 8;
        long totalSize;

        if (sz32 == 1)
        {
            if (offset + 16 > limit)
                return false;
            ulong sz64 = BinaryPrimitives.ReadUInt64BigEndian(buf.AsSpan(offset + 8, 8));
            if (sz64 < 16 || sz64 > (ulong)(limit - offset))
                return false;
            headerSize = 16;
            totalSize = (long)sz64;
        }
        else if (sz32 == 0)
        {
            totalSize = limit - offset;
        }
        else
        {
            totalSize = sz32;
        }

        if (totalSize < headerSize || offset + totalSize > limit)
            return false;

        box = new BoxRange(type, offset, (int)totalSize, headerSize);
        return true;
    }

    private static bool TryFindChildBox(byte[] buf, int start, int limit, string type, out BoxRange found)
    {
        int pos = start;
        while (TryReadBox(buf, pos, limit, out var box))
        {
            if (box.Type == type)
            {
                found = box;
                return true;
            }
            pos = box.End;
        }
        found = default;
        return false;
    }

    private static int FindLastChildBoxOffset(byte[] buf, string targetType, out int totalSize)
    {
        int foundStart = -1;
        int foundSize = 0;

        void Walk(int start, int limit)
        {
            int pos = start;
            while (TryReadBox(buf, pos, limit, out var box))
            {
                if (box.Type == targetType)
                {
                    foundStart = box.Start;
                    foundSize = box.TotalSize;
                }
                else if (box.Type is "moov" or "trak" or "mdia" or "minf" or "stbl")
                {
                    Walk(box.PayloadStart, box.End);
                }
                pos = box.End;
            }
        }

        Walk(0, buf.Length);
        totalSize = foundSize;
        return foundStart;
    }

    private static uint ReadUInt24(byte[] buf, int offset)
        => ((uint)buf[offset] << 16) | ((uint)buf[offset + 1] << 8) | buf[offset + 2];

    private static void WriteBoxHeader(byte[] buf, string type, uint size, uint? versionAndFlags = null)
    {
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(0, 4), size);
        Encoding.ASCII.GetBytes(type, buf.AsSpan(4, 4));
        if (versionAndFlags.HasValue)
            BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(8, 4), versionAndFlags.Value);
    }

    private static void WriteUnityMatrix(Span<byte> dest)
    {
        dest.Clear();
        BinaryPrimitives.WriteUInt32BigEndian(dest.Slice(0, 4), 0x00010000);
        BinaryPrimitives.WriteUInt32BigEndian(dest.Slice(16, 4), 0x00010000);
        BinaryPrimitives.WriteUInt32BigEndian(dest.Slice(32, 4), 0x40000000);
    }

    private static byte[] BuildStandardM4aFtyp()
    {
        var buf = new byte[28];
        WriteBoxHeader(buf, "ftyp", 28);
        Encoding.ASCII.GetBytes("M4A ", buf.AsSpan(8, 4));
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(12, 4), 0);
        Encoding.ASCII.GetBytes("M4A ", buf.AsSpan(16, 4));
        Encoding.ASCII.GetBytes("mp42", buf.AsSpan(20, 4));
        Encoding.ASCII.GetBytes("isom", buf.AsSpan(24, 4));
        return buf;
    }

    private static byte[] BuildMvhd(ulong totalMediaDuration, uint mediaTimescale)
    {
        const uint mvhdTimescale = 1000u;
        ulong durMs = (ulong)Math.Round(totalMediaDuration * (double)mvhdTimescale / mediaTimescale);
        bool v1 = durMs > uint.MaxValue;
        int size = v1 ? 120 : 108;
        var buf = new byte[size];
        WriteBoxHeader(buf, "mvhd", (uint)size, v1 ? 0x01000000u : 0u);

        int tail;
        if (!v1)
        {
            BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(20, 4), mvhdTimescale);
            BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(24, 4), (uint)durMs);
            tail = 28;
        }
        else
        {
            BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(28, 4), mvhdTimescale);
            BinaryPrimitives.WriteUInt64BigEndian(buf.AsSpan(32, 8), durMs);
            tail = 40;
        }

        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(tail, 4), 0x00010000); // rate = 1.0
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(tail + 4, 2), 0x0100); // volume = 1.0
        WriteUnityMatrix(buf.AsSpan(tail + 16, 36));
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(size - 4, 4), 2); // next_track_ID
        return buf;
    }

    private static byte[] BuildTkhd(ulong durMs)
    {
        bool v1 = durMs > uint.MaxValue;
        int size = v1 ? 104 : 92;
        var buf = new byte[size];
        WriteBoxHeader(buf, "tkhd", (uint)size, (v1 ? 0x01000000u : 0u) | 0x000003u);

        int tail;
        if (!v1)
        {
            BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(20, 4), 1); // track_ID
            BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(28, 4), (uint)durMs);
            tail = 32;
        }
        else
        {
            BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(28, 4), 1); // track_ID
            BinaryPrimitives.WriteUInt64BigEndian(buf.AsSpan(36, 8), durMs);
            tail = 44;
        }

        // reserved(8) + layer(2) + alt_group(2) + volume(2) + reserved(2)
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(tail + 12, 2), 0x0100);
        WriteUnityMatrix(buf.AsSpan(tail + 16, 36));
        return buf;
    }

    private static byte[] BuildMdhd(uint mediaTimescale, ulong totalMediaDuration)
    {
        bool v1 = totalMediaDuration > uint.MaxValue;
        int size = v1 ? 44 : 32;
        var buf = new byte[size];
        WriteBoxHeader(buf, "mdhd", (uint)size, v1 ? 0x01000000u : 0u);

        int langOff;
        if (!v1)
        {
            BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(20, 4), mediaTimescale);
            BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(24, 4), (uint)totalMediaDuration);
            langOff = 28;
        }
        else
        {
            BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(28, 4), mediaTimescale);
            BinaryPrimitives.WriteUInt64BigEndian(buf.AsSpan(32, 8), totalMediaDuration);
            langOff = 40;
        }

        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(langOff, 2), 0x55C4); // "und"
        return buf;
    }

    private static byte[] BuildStts(List<(uint Count, uint Delta)> runs)
    {
        int size = 16 + runs.Count * 8;
        var buf = new byte[size];
        WriteBoxHeader(buf, "stts", (uint)size, 0);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(12, 4), (uint)runs.Count);
        for (int i = 0; i < runs.Count; i++)
        {
            int o = 16 + i * 8;
            BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(o, 4), runs[i].Count);
            BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(o + 4, 4), runs[i].Delta);
        }
        return buf;
    }

    private static byte[] BuildStsc(List<ChunkSlice> chunks)
    {
        var entries = new List<(uint FirstChunk, uint SamplesPerChunk, uint Sdi)>();
        int prevSamples = -1;
        for (int i = 0; i < chunks.Count; i++)
        {
            if (chunks[i].SampleCount != prevSamples)
            {
                entries.Add(((uint)(i + 1), (uint)chunks[i].SampleCount, 1u));
                prevSamples = chunks[i].SampleCount;
            }
        }

        int size = 16 + entries.Count * 12;
        var buf = new byte[size];
        WriteBoxHeader(buf, "stsc", (uint)size, 0);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(12, 4), (uint)entries.Count);
        for (int i = 0; i < entries.Count; i++)
        {
            int o = 16 + i * 12;
            BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(o, 4), entries[i].FirstChunk);
            BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(o + 4, 4), entries[i].SamplesPerChunk);
            BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(o + 8, 4), entries[i].Sdi);
        }
        return buf;
    }

    private static byte[] BuildStsz(List<uint> sampleSizes)
    {
        int size = 20 + sampleSizes.Count * 4;
        var buf = new byte[size];
        WriteBoxHeader(buf, "stsz", (uint)size, 0);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(12, 4), 0); // variable sample size
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(16, 4), (uint)sampleSizes.Count);
        for (int i = 0; i < sampleSizes.Count; i++)
            BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(20 + i * 4, 4), sampleSizes[i]);
        return buf;
    }

    private static byte[] WrapContainer(string type, params byte[][] children)
    {
        int total = 8;
        foreach (var ch in children) total += ch.Length;
        var buf = new byte[total];
        WriteBoxHeader(buf, type, (uint)total);
        int off = 8;
        foreach (var ch in children)
        {
            ch.CopyTo(buf, off);
            off += ch.Length;
        }
        return buf;
    }

    private static byte[] BuildDefaultSmhd()
    {
        var buf = new byte[16];
        WriteBoxHeader(buf, "smhd", 16, 0);
        return buf;
    }

    private static byte[] BuildDefaultDinf()
    {
        var url = new byte[12];
        WriteBoxHeader(url, "url ", 12, 1); // self-contained
        var dref = new byte[28];
        WriteBoxHeader(dref, "dref", 28, 0);
        BinaryPrimitives.WriteUInt32BigEndian(dref.AsSpan(12, 4), 1);
        url.CopyTo(dref, 16);
        return WrapContainer("dinf", dref);
    }

    private static byte[] BuildDefaultSoundHdlr()
    {
        const string name = "SoundHandler\0";
        int size = 32 + name.Length;
        var buf = new byte[size];
        WriteBoxHeader(buf, "hdlr", (uint)size, 0);
        Encoding.ASCII.GetBytes("soun", buf.AsSpan(16, 4));
        Encoding.ASCII.GetBytes(name, buf.AsSpan(32));
        return buf;
    }
}

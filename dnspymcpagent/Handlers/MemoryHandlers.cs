using System;
using System.IO;
using System.Text;
using DnSpyMcp.Agent.Services;
using Iced.Intel;
using IcedDecoder = Iced.Intel.Decoder;

namespace DnSpyMcp.Agent.Handlers;

public static class MemoryHandlers
{
    public static void Register(Dispatcher d)
    {
        d.Register("memory.read",
            "[DEBUG] Read raw bytes at a virtual address. Returns hex string. Params: {address:ulong, size:int}.",
            p =>
            {
                var address = Dispatcher.Req<ulong>(p, "address");
                var size = Dispatcher.Req<int>(p, "size");
                if (size <= 0 || size > 1024 * 1024) throw new ArgumentException("size out of range (1..1MB)");
                var buf = ReadMemory(address, size, out int got);
                return new
                {
                    address = (long)address,
                    requested = size,
                    read = got,
                    hex = ToHex(buf, got),
                };
            });

        d.Register("memory.write",
            "[DEBUG] Write bytes at a virtual address (attached only). Params: {address:ulong, hex:string}.",
            p =>
            {
                var address = Dispatcher.Req<ulong>(p, "address");
                var hex = Dispatcher.Req<string>(p, "hex");
                var data = FromHex(hex);
                int wrote = Program.Session.OnDbg(() =>
                {
                    foreach (var proc in Program.Session.DnDebugger.Processes)
                    {
                        var cp = proc.CorProcess;
                        var hr = cp.WriteMemory(address, data, 0, data.Length, out int w);
                        if (hr < 0) throw new InvalidOperationException($"WriteMemory hr=0x{hr:X8}");
                        return w;
                    }
                    return 0;
                });
                return new { address = (long)address, wrote };
            });

        d.Register("memory.read_int",
            "[DEBUG] Read a typed integer at address. Params: {address:ulong, kind?:string='i32' in {i8,u8,i16,u16,i32,u32,i64,u64}}.",
            p =>
            {
                var address = Dispatcher.Req<ulong>(p, "address");
                var kind = Dispatcher.Opt<string>(p, "kind", "i32");
                int sz = kind switch
                {
                    "i8" or "u8" => 1,
                    "i16" or "u16" => 2,
                    "i32" or "u32" => 4,
                    "i64" or "u64" => 8,
                    _ => throw new ArgumentException($"unknown kind: {kind}")
                };
                var b = ReadMemory(address, sz, out _);
                object v = kind switch
                {
                    "i8"  => (long)(sbyte)b[0],
                    "u8"  => (ulong)b[0],
                    "i16" => (long)BitConverter.ToInt16(b, 0),
                    "u16" => (ulong)BitConverter.ToUInt16(b, 0),
                    "i32" => (long)BitConverter.ToInt32(b, 0),
                    "u32" => (ulong)BitConverter.ToUInt32(b, 0),
                    "i64" => BitConverter.ToInt64(b, 0),
                    "u64" => BitConverter.ToUInt64(b, 0),
                    _ => 0,
                };
                return new { address = (long)address, kind, value = v };
            });

        d.Register("memory.disasm",
            "[DEBUG] Disassemble x64 machine code at an address. Params: {address:ulong, size?:int=128}.",
            p =>
            {
                var address = Dispatcher.Req<ulong>(p, "address");
                var size = Dispatcher.Opt<int>(p, "size", 128);
                if (size <= 0 || size > 65536) throw new ArgumentException("size out of range (1..64KB)");
                var bytes = ReadMemory(address, size, out int got);

                var codeReader = new ByteArrayCodeReader(bytes, 0, got);
                var decoder = IcedDecoder.Create(64, codeReader);
                decoder.IP = address;
                var formatter = new NasmFormatter();
                formatter.Options.DigitSeparator = "";
                formatter.Options.FirstOperandCharIndex = 10;
                var output = new StringOutput();
                var sw = new StringWriter();

                ulong endRip = address + (ulong)got;
                while (decoder.IP < endRip)
                {
                    decoder.Decode(out var instr);
                    formatter.Format(instr, output);
                    sw.Write($"0x{instr.IP:X16}  ");
                    int byteLen = instr.Length;
                    int bStart = (int)(instr.IP - address);
                    for (int i = 0; i < byteLen && i < 10; i++) sw.Write($"{bytes[bStart + i]:X2} ");
                    for (int i = byteLen; i < 10; i++) sw.Write("   ");
                    sw.Write(" ");
                    sw.Write(output.ToStringAndReset());
                    sw.WriteLine();
                }
                return new { address = (long)address, bytes = got, listing = sw.ToString() };
            });
    }

    private static byte[] ReadMemory(ulong address, int size, out int got)
    {
        if (Program.Session.IsAttached)
        {
            var result = Program.Session.OnDbg(() =>
            {
                foreach (var proc in Program.Session.DnDebugger.Processes)
                {
                    var buf = new byte[size];
                    var cp = proc.CorProcess;
                    var hr = cp.ReadMemory(address, buf, 0, size, out int r);
                    if (hr < 0) throw new InvalidOperationException($"ReadMemory hr=0x{hr:X8}");
                    return (buf, r);
                }
                throw new InvalidOperationException("no process");
            });
            got = result.Item2;
            return result.Item1;
        }
        // Dump / offline path via ClrMD
        var dr = Program.Session.ClrMdTarget.DataReader;
        var dbuf = new byte[size];
        got = dr.Read(address, dbuf);
        return dbuf;
    }

    private static string ToHex(byte[] buf, int len)
    {
        var sb = new StringBuilder(len * 2);
        for (int i = 0; i < len; i++) sb.Append(buf[i].ToString("X2"));
        return sb.ToString();
    }

    private static byte[] FromHex(string hex)
    {
        hex = hex.Replace(" ", "").Replace("\n", "").Replace("\r", "");
        if ((hex.Length & 1) != 0) throw new ArgumentException("hex must be even length");
        var buf = new byte[hex.Length / 2];
        for (int i = 0; i < buf.Length; i++)
            buf[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return buf;
    }
}

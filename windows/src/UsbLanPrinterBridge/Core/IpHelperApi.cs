using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;

namespace UsbLanPrinterBridge.Core
{
    /// <summary>
    /// Adds and removes secondary IPv4 addresses through iphlpapi (CreateUnicastIpAddressEntry, Vista+).
    ///
    /// Why not netsh: "netsh interface ipv4 add address" switches a DHCP-configured adapter to static
    /// configuration, which drops the gateway and DNS the adapter got from DHCP and takes the PC off the
    /// internet. The API below only adds a runtime address entry: the adapter keeps its DHCP lease, gateway
    /// and DNS, and the extra address disappears by itself at reboot.
    /// </summary>
    public static class IpHelperApi
    {
        private const ushort AF_INET = 2;
        public const int ERROR_SUCCESS = 0;
        public const int ERROR_ACCESS_DENIED = 5;
        public const int ERROR_NOT_FOUND = 1168;
        public const int ERROR_OBJECT_ALREADY_EXISTS = 5010;

        private const int IpPrefixOriginManual = 1;
        private const int IpSuffixOriginManual = 1;
        private const int IpDadStatePreferred = 4;

        /// <summary>MIB_UNICASTIPADDRESS_ROW (80 bytes on x86 and x64). Only the IPv4 part of SOCKADDR_INET is mapped.</summary>
        [StructLayout(LayoutKind.Explicit, Size = 80)]
        public struct MIB_UNICASTIPADDRESS_ROW
        {
            [FieldOffset(0)] public ushort Family;           // SOCKADDR_INET.si_family / sin_family
            [FieldOffset(2)] public ushort Port;             // sin_port (unused)
            [FieldOffset(4)] public uint Ipv4Address;        // sin_addr, network byte order
            [FieldOffset(32)] public ulong InterfaceLuid;    // NET_LUID (8-byte aligned after the 28-byte sockaddr)
            [FieldOffset(40)] public uint InterfaceIndex;
            [FieldOffset(44)] public int PrefixOrigin;
            [FieldOffset(48)] public int SuffixOrigin;
            [FieldOffset(52)] public uint ValidLifetime;
            [FieldOffset(56)] public uint PreferredLifetime;
            [FieldOffset(60)] public byte OnLinkPrefixLength;
            [FieldOffset(61)] public byte SkipAsSource;
            [FieldOffset(64)] public int DadState;
            [FieldOffset(68)] public uint ScopeId;
            [FieldOffset(72)] public long CreationTimeStamp;
        }

        [DllImport("iphlpapi.dll", ExactSpelling = true)]
        private static extern void InitializeUnicastIpAddressEntry(ref MIB_UNICASTIPADDRESS_ROW row);

        [DllImport("iphlpapi.dll", ExactSpelling = true)]
        private static extern int CreateUnicastIpAddressEntry(ref MIB_UNICASTIPADDRESS_ROW row);

        [DllImport("iphlpapi.dll", ExactSpelling = true)]
        private static extern int DeleteUnicastIpAddressEntry(ref MIB_UNICASTIPADDRESS_ROW row);

        [DllImport("iphlpapi.dll", ExactSpelling = true)]
        private static extern int ConvertInterfaceIndexToLuid(uint interfaceIndex, out ulong interfaceLuid);

        [DllImport("iphlpapi.dll", ExactSpelling = true)]
        private static extern int GetUnicastIpAddressTable(ushort family, out IntPtr table);

        [DllImport("iphlpapi.dll", ExactSpelling = true)]
        private static extern void FreeMibTable(IntPtr table);

        private static MIB_UNICASTIPADDRESS_ROW MakeRow(int interfaceIndex, IPAddress ip)
        {
            if (ip == null || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                throw new ArgumentException("IPv4 address required.", "ip");
            var row = new MIB_UNICASTIPADDRESS_ROW();
            InitializeUnicastIpAddressEntry(ref row);
            row.Family = AF_INET;
            row.Ipv4Address = BitConverter.ToUInt32(ip.GetAddressBytes(), 0); // bytes already in network order
            row.InterfaceIndex = (uint)interfaceIndex;
            ulong luid;
            if (ConvertInterfaceIndexToLuid((uint)interfaceIndex, out luid) == ERROR_SUCCESS) row.InterfaceLuid = luid;
            return row;
        }

        /// <summary>Adds a non-persistent unicast IPv4 address to an interface. Returns a Win32 error code (0 = ok).</summary>
        public static int AddAddress(int interfaceIndex, IPAddress ip, int prefixLength, bool skipAsSource)
        {
            MIB_UNICASTIPADDRESS_ROW row = MakeRow(interfaceIndex, ip);
            row.OnLinkPrefixLength = (byte)prefixLength;
            row.SkipAsSource = skipAsSource ? (byte)1 : (byte)0;
            row.PrefixOrigin = IpPrefixOriginManual;
            row.SuffixOrigin = IpSuffixOriginManual;
            row.ValidLifetime = 0xFFFFFFFF;
            row.PreferredLifetime = 0xFFFFFFFF;
            // Preferred = usable at once; if the stack still runs duplicate-address detection the caller waits for it.
            row.DadState = IpDadStatePreferred;
            return CreateUnicastIpAddressEntry(ref row);
        }

        public static int RemoveAddress(int interfaceIndex, IPAddress ip)
        {
            MIB_UNICASTIPADDRESS_ROW row = MakeRow(interfaceIndex, ip);
            return DeleteUnicastIpAddressEntry(ref row);
        }

        public static string Describe(int error)
        {
            if (error == ERROR_SUCCESS) return "OK";
            return new Win32Exception(error).Message + " (error " + error + ")";
        }

        public sealed class UnicastRow
        {
            public int InterfaceIndex;
            public IPAddress Address;
            public int PrefixLength;
            public bool SkipAsSource;
            public int DadState;
            public int PrefixOrigin;
        }

        /// <summary>Reads the live IPv4 unicast address table (no admin rights needed). Used for diagnostics and self-tests.</summary>
        public static List<UnicastRow> GetIPv4Rows()
        {
            IntPtr table;
            int err = GetUnicastIpAddressTable(AF_INET, out table);
            if (err != ERROR_SUCCESS) throw new Win32Exception(err, "GetUnicastIpAddressTable: " + Describe(err));
            try
            {
                var result = new List<UnicastRow>();
                int count = Marshal.ReadInt32(table);
                int rowSize = Marshal.SizeOf(typeof(MIB_UNICASTIPADDRESS_ROW));
                IntPtr p = IntPtr.Add(table, 8); // ULONG NumEntries + padding so rows are 8-byte aligned
                for (int i = 0; i < count; i++)
                {
                    var row = (MIB_UNICASTIPADDRESS_ROW)Marshal.PtrToStructure(p, typeof(MIB_UNICASTIPADDRESS_ROW));
                    result.Add(new UnicastRow
                    {
                        InterfaceIndex = (int)row.InterfaceIndex,
                        Address = new IPAddress(BitConverter.GetBytes(row.Ipv4Address)),
                        PrefixLength = row.OnLinkPrefixLength,
                        SkipAsSource = row.SkipAsSource != 0,
                        DadState = row.DadState,
                        PrefixOrigin = row.PrefixOrigin
                    });
                    p = IntPtr.Add(p, rowSize);
                }
                return result;
            }
            finally
            {
                FreeMibTable(table);
            }
        }
    }
}

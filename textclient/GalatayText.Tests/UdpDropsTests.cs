using GalatayText;
using Xunit;

public class UdpDropsTests
{
    const string Snmp = "Ip: Forwarding DefaultTTL\nIp: 1 64\nUdp: InDatagrams NoPorts InErrors OutDatagrams RcvbufErrors SndbufErrors InCsumErrors IgnoredMulti MemErrors\nUdp: 46650 0 1327 16612 1327 0 0 0 0\nUdpLite: InDatagrams NoPorts InErrors OutDatagrams RcvbufErrors SndbufErrors InCsumErrors IgnoredMulti MemErrors\nUdpLite: 0 0 0 0 9 0 0 0 0\n";

    [Fact] public void ParsesUdpNotUdpLite() => Assert.Equal(1327, Program.ParseUdpRcvbufErrors(Snmp));
    [Fact] public void MissingIsMinusOne() { Assert.Equal(-1, Program.ParseUdpRcvbufErrors("")); Assert.Equal(-1, Program.ParseUdpRcvbufErrors("Ip: a\nIp: 1\n")); }
    [Fact] public void NoteOnlyOnIncrease()
    {
        Assert.Null(Program.UdpDropNote(-1, 5, 10));
        Assert.Null(Program.UdpDropNote(5, 5, 10));
        Assert.Null(Program.UdpDropNote(9, 3, 10));
        Assert.Contains("dropped 4 incoming UDP", Program.UdpDropNote(1, 5, 10));
    }
    [Fact] public void WantsEightMegabytes() => Assert.Equal(8 * 1024 * 1024, Program.UdpSockBufWanted);
}

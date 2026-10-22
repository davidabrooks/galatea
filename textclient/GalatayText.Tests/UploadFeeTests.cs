using GalatayText;
using Xunit;
public class UploadFeeTests
{
    [Fact] public void TwoKFeeByAccountType()
    {
        Assert.Equal(50, Program.TwoKFeeFor("Base"));
        Assert.Equal(50, Program.TwoKFeeFor("Plus"));
        Assert.Equal(40, Program.TwoKFeeFor("Premium"));
        Assert.Equal(40, Program.TwoKFeeFor("Monthly"));   // 2026-10-21 regression: Monthly was unknown, L$10 was offered and refused
        Assert.Equal(0, Program.TwoKFeeFor("Premium_Plus"));
        Assert.Equal(-1, Program.TwoKFeeFor(null));
        Assert.Equal(-1, Program.TwoKFeeFor("Something"));
    }
    [Fact] public void FeeRefusedWithoutChargeReArms()
    {
        Assert.True(Program.FeeRefusedNoCharge("failure", "The server expects a different upload fee", 1126, 1126));
        Assert.False(Program.FeeRefusedNoCharge("failure", "The server expects a different upload fee", 1126, 1086)); // charged
        Assert.False(Program.FeeRefusedNoCharge("failure", "The server expects a different upload fee", 1126, null)); // unknown balance
        Assert.False(Program.FeeRefusedNoCharge("failure", "some other error", 1126, 1126));
        Assert.False(Program.FeeRefusedNoCharge("complete", null, 1126, 1086));
    }
}

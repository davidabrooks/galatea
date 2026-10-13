using GalatayText;
using LibreMetaverse;
using Xunit;

public class PbrExportTests
{
    // scene-mesher reads material-<id>.bin from the export dir (Program.Get); the names must agree
    [Fact] public void FileNameMatchesMesher() =>
        Assert.Equal("material-d44524df-8a3a-f8ad-3a26-04e638f1fe24.bin", Program.PbrFileName(new UUID("d44524df-8a3a-f8ad-3a26-04e638f1fe24")));
}

using System.Text.Json.Nodes;
using Colibri.Core.Engine;

namespace Colibri.Engine.Aria2.Tests;

public class Aria2StatusTests
{
    private static JsonObject Status(string status) => JsonNode.Parse($$"""
        {
          "gid": "2089b05ecca3d829",
          "status": "{{status}}",
          "totalLength": "34896138",
          "completedLength": "901120",
          "downloadSpeed": "15158",
          "connections": "4",
          "bitfield": "f000",
          "numPieces": "34",
          "pieceLength": "1048576",
          "files": [ { "index": "1", "path": "{{Path.GetTempPath().Replace("\\", "/")}}file.bin" } ]
        }
        """)!.AsObject();

    [Fact]
    public void Numbers_sent_as_strings_are_parsed()
    {
        var status = Aria2Status.Parse(Status("active"));

        Assert.Equal("2089b05ecca3d829", status.Handle);
        Assert.Equal(34896138, status.TotalBytes);
        Assert.Equal(901120, status.CompletedBytes);
        Assert.Equal(15158, status.DownloadSpeed);
        Assert.Equal(4, status.Connections);
        Assert.Equal("f000", status.Bitfield);
        Assert.Equal(34, status.NumPieces);
        Assert.Equal(1048576, status.PieceLength);
    }

    [Fact]
    public void Sizes_above_4_GiB_do_not_overflow()
    {
        var json = Status("active");
        json["totalLength"] = "53687091200";

        Assert.Equal(53_687_091_200L, Aria2Status.Parse(json).TotalBytes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("-5")]
    [InlineData("1.5")]
    public void Missing_or_unreadable_numbers_count_as_zero(string? value)
    {
        Assert.Equal(0, Aria2Status.ParseLong(value is null ? null : JsonValue.Create(value)));
    }

    [Theory]
    [InlineData("active", EngineDownloadState.Active)]
    [InlineData("waiting", EngineDownloadState.Waiting)]
    [InlineData("paused", EngineDownloadState.Paused)]
    [InlineData("complete", EngineDownloadState.Complete)]
    [InlineData("error", EngineDownloadState.Error)]
    [InlineData("removed", EngineDownloadState.Removed)]
    public void Each_aria2_status_maps_to_an_engine_state(string aria2Status, EngineDownloadState expected)
    {
        Assert.Equal(expected, Aria2Status.Parse(Status(aria2Status)).State);
    }

    [Fact]
    public void Unknown_status_is_rejected()
    {
        Assert.Throws<FormatException>(() => Aria2Status.Parse(Status("exploded")));
    }

    [Fact]
    public void Error_status_carries_code_without_native_credential_text()
    {
        var json = Status("error");
        json["errorCode"] = "3";
        json["errorMessage"] = "Failure at http://user:private-password@proxy.test";

        var status = Aria2Status.Parse(json);

        Assert.Equal("aria2 error 3", status.ErrorMessage);
    }

    [Fact]
    public void Error_message_is_only_set_for_errors()
    {
        var json = Status("complete");
        json["errorCode"] = "0";
        json["errorMessage"] = "";

        Assert.Null(Aria2Status.Parse(json).ErrorMessage);
    }

    [Fact]
    public void File_path_comes_from_the_first_file_in_platform_form()
    {
        var status = Aria2Status.Parse(Status("active"));

        Assert.Equal(Path.Combine(Path.GetTempPath(), "file.bin"), status.FilePath);
    }

    [Fact]
    public void Unknown_path_and_empty_bitfield_are_null()
    {
        var json = Status("waiting");
        json["files"] = JsonNode.Parse("""[{"index":"1","path":""}]""");
        json["bitfield"] = "";

        var status = Aria2Status.Parse(json);

        Assert.Null(status.FilePath);
        Assert.Null(status.Bitfield);
    }
}

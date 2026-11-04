using System.IO;
using GUI.Recording;
using LnScope.Core.Devices;
using Microsoft.VisualBasic.FileIO;

internal static class SparseRecordingChecks
{
    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
        Console.WriteLine("PASS: " + message);
    }
    internal static List<string[]> ReadCsv(string path)
    {
        using var parser = new TextFieldParser(path) { HasFieldsEnclosedInQuotes = true };
        parser.SetDelimiters(",");
        var rows = new List<string[]>();
        while (!parser.EndOfData) rows.Add(parser.ReadFields()!);
        return rows;
    }
    public static async Task RunAsync()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "sparse-recording-checks", Guid.NewGuid().ToString("N"));
        var journal = new RecordingJournal();
        var state = new AxisTelemetry("stage", "X", "um", "Ready", 12, 12, false, "None", DateTimeOffset.UtcNow, 0);
        var options = new RecordingOptions { OutputDirectory = root, FileNameTemplate = "constant.mp4", QueueCapacity = 8 };
        await using var recorder = new VideoRecorder(options, Console.WriteLine, () => [state], journal);
        Check(recorder.Start("camera", 64, 48, 10, CameraPixelFormat.Mono8), "sparse recording starts");
        var first = recorder.CurrentFile!;
        for (var i = 0; i < 4; i++)
        {
            recorder.Push(new(new byte[64*48],64,48,64,CameraPixelFormat.Mono8,i,DateTimeOffset.UtcNow));
            journal.Position(state with { SampleUtc = DateTimeOffset.UtcNow, AgeMs = i });
            await Task.Delay(20);
        }
        state = state with { C1 = 13, C2 = 13 }; journal.Position(state);
        state = state with { C1 = 12, C2 = 12 }; journal.Position(state);
        state = state with { Moving = true }; journal.Position(state);
        state = state with { Limit = "Positive" }; journal.Position(state);
        state = state with { AgeMs = 4000 }; journal.Position(state);
        state = state with { State = "Disconnected", C1 = null, C2 = null, Moving = null, Limit = "unknown" }; journal.Position(state);
        recorder.Push(new(new byte[64*48],64,48,64,CameraPixelFormat.Mono8,4,DateTimeOffset.UtcNow));
        await recorder.StopAsync();
        journal.Position(state with { C1 = 999 });
        Check(recorder.Status.StartsWith("saved "), "sparse CSV and video finalize successfully");
        var rows = ReadCsv(Path.ChangeExtension(first, ".csv"));
        Check(rows.All(r => r.Length == 20), "all recording events match the telemetry-only schema");
        var positions = rows.Skip(1).Where(r => r[16] == "position").ToArray();
        Check(positions.Length == 7 && positions.Select(r => r[9]).SequenceEqual(new[] {"12","13","12","12","12","12",""}),
            "only value/state changes are logged, including moves and return between video frames");
        Check(positions[5][15] == "False" && positions[6][8] == "Disconnected", "stale feedback and disconnect transitions are explicit");
        var frames = rows.Skip(1).Where(r => r[16] == "frame").ToArray();
        Check(frames.Length == recorder.WrittenFrames && frames.Select(r => r[0]).SequenceEqual(new[] {"0","1","2","3","4"}) &&
            frames.All(r => r[0] == r[1] && DateTimeOffset.TryParse(r[2], out _) && r[9] == ""),
            "every saved frame has a capture timestamp without duplicated coordinates");
        Check(positions.Skip(1).All(r => r[0].Length > 0 && DateTimeOffset.TryParse(r[17], out _)), "sparse updates reference video frames and their own UTC event time");
        Check(Directory.GetFiles(Path.GetDirectoryName(first)!).Length == 2, "session folder contains only video and CSV");
        Check(recorder.Start("camera",64,48,10,CameraPixelFormat.Mono8), "second recording starts with the same configured file name");
        recorder.Push(new(new byte[64*48],64,48,64,CameraPixelFormat.Mono8,0,DateTimeOffset.UtcNow));
        await recorder.StopAsync();
        Check(Path.GetDirectoryName(first) != Path.GetDirectoryName(recorder.CurrentFile!) && File.Exists(first), "each recording receives a new timestamped directory without overwriting");
        Check(Directory.GetFiles(Path.GetDirectoryName(recorder.CurrentFile!)!).Length == 2, "second session also contains exactly two files");
    }
}

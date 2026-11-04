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
        var journal = new RecordingJournal(new Dictionary<string, string> { ["stage"] = "COM3", ["manipulator"] = "COM3" });
        var state = new AxisTelemetry("stage", "X", "um", "Ready", 12, 12, false, "None", DateTimeOffset.UtcNow, 0);
        var stationary = state with { Device = "manipulator", C1 = 42, C2 = 42 };
        var options = new RecordingOptions { OutputDirectory = root, FileNameTemplate = "constant.mp4", QueueCapacity = 8 };
        await using var recorder = new VideoRecorder(options, Console.WriteLine, () => [state, stationary], journal);
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
        int Col(string name) => Array.IndexOf(rows[0], name);
        Check(rows.All(r => r.Length == rows[0].Length && r.All(v => v.Length > 0)), "snapshot rows have no empty fields");
        Check(rows[1][0] == "initial" && rows[1][Col("stage_X_um")] == "12" &&
            rows[1][Col("manipulator_X_um")] == "42" && rows.Skip(1).All(r =>
                r[Col("stage_port")] == "COM3" && r[Col("manipulator_port")] == "COM3"), "initial snapshot includes both devices and their shared port");
        var positions = rows.Where(r => r[0] == "position").ToArray();
        Check(positions.Select(r => r[Col("stage_X_um")]).SequenceEqual(new[] {"13","12","12","12","12","NA"}),
            "moves and returns between frames remain visible");
        Check(positions[4][Col("stage_X_valid")] == "False" && positions[5][Col("stage_X_state")] == "Disconnected",
            "stale and disconnected feedback are explicit");
        var frames = rows.Where(r => r[0] == "frame").ToArray();
        Check(frames.Length == recorder.WrittenFrames && frames.Select(r => r[Col("video_frame")]).SequenceEqual(new[] {"0","1","2","3","4"}) &&
            frames.All(r => DateTimeOffset.TryParse(r[Col("capture_utc")], out _) && r[Col("manipulator_X_um")] == "42"),
            "each video frame carries its capture timestamp and coordinates");
        Check(Directory.GetFiles(Path.GetDirectoryName(first)!).Length == 2, "session folder contains only video and CSV");
        Check(recorder.Start("camera",64,48,10,CameraPixelFormat.Mono8), "second recording starts with the same configured file name");
        recorder.Push(new(new byte[64*48],64,48,64,CameraPixelFormat.Mono8,0,DateTimeOffset.UtcNow));
        await recorder.StopAsync();
        Check(Path.GetDirectoryName(first) != Path.GetDirectoryName(recorder.CurrentFile!) && File.Exists(first), "each recording receives a new timestamped directory without overwriting");
        Check(Directory.GetFiles(Path.GetDirectoryName(recorder.CurrentFile!)!).Length == 2, "second session also contains exactly two files");
    }
}

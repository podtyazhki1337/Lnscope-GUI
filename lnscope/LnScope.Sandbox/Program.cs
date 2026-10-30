using System.Text.Json;
using LnScope.Core.Devices;
using LnScope.Devices.LuigsNeumann;

var configPath = args.Length > 0 ? args[0] : "devices.json";

var options = JsonSerializer.Deserialize<LnManipulatorOptions>(
    File.ReadAllText(configPath),
    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
    ?? throw new InvalidOperationException($"Не удалось прочитать {configPath}.");

Console.WriteLine($"Транспорт: {options.Transport}, осей в конфигурации: {options.Axes.Count}");

await using var manipulator = new LnManipulator(options);

manipulator.StateChanged += (_, state) => Console.WriteLine($"[состояние] {state}");

manipulator.PositionChanged += (_, position) =>
    Console.WriteLine($"[движение] {position.AxisId} = {position.Reference,10:0.000} мкм, в движении: {position.IsMoving}");

await manipulator.ConnectAsync();

foreach (var axis in manipulator.Axes)
{
    var position = await manipulator.ReadPositionAsync(axis.Id);
    Console.WriteLine($"{axis.Id}: {position.Reference:0.000} {axis.Unit}");
}

var testAxis = manipulator.Axes[0].Id;
Console.WriteLine($"\nПеремещение оси {testAxis} в 1500 мкм...");
await manipulator.MoveAbsoluteAsync(testAxis, 1500, MoveSpeed.Fast);

await Task.Delay(3000);

var final = await manipulator.ReadPositionAsync(testAxis);
Console.WriteLine($"\nИтоговая позиция {testAxis}: {final.Reference:0.000} мкм");

await manipulator.DisconnectAsync();

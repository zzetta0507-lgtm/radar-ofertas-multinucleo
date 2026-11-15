using System.Text.Json;
using System.Text.Json.Serialization;
using RadarOfertasMultinucleo.Configuration;

return await App.RunAsync(args);

internal static class App
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var configPath = Path.Combine(AppContext.BaseDirectory, "config.json");
            await using var configFile = File.OpenRead(configPath);
            var options = await JsonSerializer.DeserializeAsync<PipelineOptions>(
                configFile,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    Converters = { new JsonStringEnumConverter() }
                }) ?? throw new InvalidDataException("config.json está vacío o no es válido.");

            ApplyArguments(options, args);
            options.Validate();

            Console.WriteLine($"Configuración válida. Modo seleccionado: {options.Mode}.");
            Console.WriteLine("Las etapas del pipeline se implementarán incrementalmente.");
            return 0;
        }
        catch (Exception exception) when (
            exception is IOException or JsonException or InvalidDataException or ArgumentException)
        {
            Console.Error.WriteLine($"No se pudo iniciar el radar: {exception.Message}");
            return 1;
        }
    }

    private static void ApplyArguments(PipelineOptions options, string[] args)
    {
        if (args.Length == 0)
        {
            return;
        }

        if (args.Length != 2 || args[0] != "--mode")
        {
            throw new ArgumentException("Uso: RadarOfertasMultinucleo [--mode sequential|concurrent|parallel]");
        }

        if (!Enum.TryParse<ExecutionMode>(args[1], ignoreCase: true, out var mode))
        {
            throw new ArgumentException($"Modo desconocido: {args[1]}.");
        }

        options.Mode = mode;
    }
}

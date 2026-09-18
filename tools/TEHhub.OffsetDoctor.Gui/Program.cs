namespace TEHhub.OffsetDoctor.Gui;

using System;
using System.Threading.Tasks;

internal static class Program
{
    private static async Task Main(string[] args)
    {
        try
        {
            using var app = new OffsetDoctorApp();
            await app.Run();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[OffsetDoctor.Gui] Fatal error: {ex.Message}");
        }
    }
}
